"""Backed-up replacement of a native-verified complete VGP bundle.

The native saver retains responsibility for grid/model/raw-data validation.
This module only publishes identical bytes under an existing original name.
"""
from __future__ import annotations

from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import shutil
import stat
import ctypes
from uuid import uuid4

from .bridge_client import BridgeError
from .operation_lock import live_transaction


def require_unopened_original(path: Path) -> None:
    """Reject an open Windows project before replacing its complete bundle."""
    if os.name != 'nt':
        return
    native = ctypes.WinDLL('kernel32', use_last_error=True)
    native.CreateFileW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32,
                                  ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p]
    native.CreateFileW.restype = ctypes.c_void_p
    native.CloseHandle.argtypes = [ctypes.c_void_p]
    handle = native.CreateFileW(str(path), 0x80000000, 0, None, 3, 0x80, None)
    if handle == ctypes.c_void_p(-1).value:
        error = ctypes.get_last_error()
        if error in (32, 33):
            raise BridgeError('ORIGINAL_IN_USE', 'Close this original document without saving before file writeback. No original file was replaced.')
        raise ctypes.WinError(error)
    native.CloseHandle(handle)


def save_project_model_in_place(client, session_id: str) -> dict:
    """Verify a complete private result and publish it to the backed-up original."""
    with client.exclusive():
        status = client.call('status')
        if status.get('native_project_save_verified') is not True:
            raise BridgeError('PROJECT_SAVE_NOT_VERIFIED', 'No project write was sent')
        info = client.call('get_spectrum_info', {'session_id': session_id})
        target = Path(info['source_path']).absolute()
        expected = manifest(target)
        require_unopened_original(target)
        stage = _archive_root(target) / ('native-save-' + uuid4().hex)
        stage.mkdir()
        saved = client.call('save_project_model', {'session_id': session_id, 'path': str(stage / target.name)})
        required = ('roundtrip_verified', 'raw_data_verified', 'unrelated_spectra_verified', 'grid_references_verified')
        if not all(saved.get(key) is True for key in required):
            raise BridgeError('PROJECT_SAVE_UNVERIFIED', 'Private result verification incomplete; original retained', saved)
        require_unopened_original(target)
        result = publish_bundle(stage / target.name, target, expected)
        return {**saved, **result, 'in_place': True, 'saved_project_verified': True,
                'application_windows_opened': 0,
                'note': 'Verified complete model saved to the original path with a full backup. No application window was opened.'}


def _plain(path: Path) -> None:
    for item in (path, *path.parents):
        if item.exists() and (item.is_symlink() or getattr(item.lstat(), 'st_file_attributes', 0) & 0x400):
            raise ValueError(f'Reparse points are not supported: {item}')


def _archive_root(target: Path) -> Path:
    """Keep recovery material outside the sample folder on the same volume.

    Same-volume storage is required for the journalled rename transaction.
    Never redirect it to a global C: temporary directory for E: projects.
    """
    target = Path(target).absolute()
    identity = hashlib.sha256(str(target.parent).casefold().encode('utf-8')).hexdigest()[:12]
    # Avantage still uses legacy MAX_PATH APIs inside its own extra staging layer.
    # Keep this root short even when the user's sample directory is deeply nested.
    configured = os.environ.get('AVANTAGE_RECOVERY_ROOT')
    root = (Path(configured).absolute() if configured else Path(target.anchor) / 'AvantageMCP' / 'recovery') / identity
    _plain(root)
    root.mkdir(parents=True, exist_ok=True)
    if root.stat().st_dev != target.parent.stat().st_dev:
        raise ValueError('Recovery directory must be on the original project volume')
    return root


def manifest(path: Path) -> dict[str, str]:
    """Hash the full bundle, rejecting redirected paths and special files."""
    path = Path(path).absolute()
    data = path.with_suffix('.DATA')
    _plain(path)
    _plain(data)
    if path.suffix.lower() != '.vgp' or not path.is_file() or not data.is_dir():
        raise ValueError('An existing VGP and matching DATA directory are required')
    result = {}
    for entry in (path, *sorted(data.rglob('*'))):
        _plain(entry)
        if entry.is_dir():
            continue
        if not stat.S_ISREG(entry.stat().st_mode):
            raise ValueError(f'Unsupported bundle entry: {entry}')
        with entry.open('rb') as stream:
            digest = hashlib.sha256()
            for chunk in iter(lambda: stream.read(1024 * 1024), b''):
                digest.update(chunk)
            result[str(entry.relative_to(path.parent))] = digest.hexdigest()
    return result


@contextmanager
def _target_lock(target: Path):
    lock = target.parent / ('.' + target.name + '.avantage-writeback.lock')
    with lock.open('x', encoding='utf-8') as stream:
        stream.write(str(os.getpid()))
    try:
        yield
    finally:
        lock.unlink()


def publish_bundle(source: Path, target: Path, expected: dict[str, str]) -> dict:
    """Replace an existing bundle, keeping the original and rolling back errors.

    Each rename is atomic. The pair is journalled, not claimed to be atomic
    across a machine crash. Never run two writers against the same target.
    """
    source, target = Path(source).absolute(), Path(target).absolute()
    if source.name != target.name or source.parent == target.parent:
        raise ValueError('Staged and original bundles must have the same basename in separate directories')
    if source.parent.is_relative_to(target.with_suffix('.DATA')) or target.parent.is_relative_to(source.with_suffix('.DATA')):
        raise ValueError('Overlapping project bundles are not supported')
    verified = manifest(source)
    if not expected:
        raise ValueError('The original manifest is required')
    with _target_lock(target):
        if manifest(target) != expected:
            raise ValueError('Original project changed; no files replaced')
        work = _archive_root(target) / ('writeback-' + uuid4().hex)
        pending, backup = work / 'pending', work / 'backup'
        pending.mkdir(parents=True)
        backup.mkdir()
        journal = work / 'journal.json'
        state = {'target': str(target), 'backup': str(backup), 'original_manifest': expected,
                 'new_manifest': verified, 'completed': [], 'status': 'preparing'}

        def record(status):
            state['status'] = status
            with journal.open('w', encoding='utf-8') as stream:
                json.dump(state, stream, ensure_ascii=False, indent=2)
                stream.flush()
                os.fsync(stream.fileno())

        staged = pending / target.name
        shutil.copy2(source, staged)
        shutil.copytree(source.with_suffix('.DATA'), staged.with_suffix('.DATA'))
        if manifest(staged) != verified or manifest(target) != expected:
            raise ValueError(f'Bundle changed before replacement; originals retained; diagnostic: {work}')
        record('prepared')
        moves = [
            (target, backup / target.name),
            (target.with_suffix('.DATA'), (backup / target.name).with_suffix('.DATA')),
            (staged.with_suffix('.DATA'), target.with_suffix('.DATA')),
            (staged, target),
        ]
        completed = []
        try:
            for old, new in moves:
                old.rename(new)
                completed.append((old, new))
                state['completed'].append([str(old), str(new)])
                record('replacing')
            if manifest(target) != verified or manifest(backup / target.name) != expected:
                raise ValueError('Published bundle or backup verification failed')
            record('committed')
        except BaseException:
            # Reverse only renames actually completed by this transaction.
            # No overwrite or recursive deletion occurs on either path.
            try:
                for old, new in reversed(completed):
                    new.rename(old)
                if manifest(target) != expected:
                    raise ValueError('Original restoration hash mismatch')
                record('rolled_back')
            except BaseException as recovery_error:
                record('recovery_required')
                raise RuntimeError(f'Recovery required; journal: {journal}') from recovery_error
            raise
        return {'path': str(target), 'backup_path': str(backup / target.name),
                'journal_path': str(journal), 'original_backup_verified': True,
                'published_bytes_verified': True, 'file_count': len(verified)}


def save_document_in_place(client, document_id: str, path: str) -> dict:
    """Back up the complete bundle, then save the existing application window."""
    with client.exclusive(), live_transaction():
        return _save_document_in_place(client,document_id,path)


def _save_document_in_place(client, document_id: str, path: str) -> dict:
    target = Path(path).absolute()
    expected = manifest(target)
    with client.exclusive():
        status = client.call('status')
        if status.get('native_live_document_bridge_verified') is not True:
            raise BridgeError('LIVE_DOCUMENT_BRIDGE_NOT_VERIFIED', 'No write was sent')
        if status.get('native_document_lifecycle_verified') is not True:
            raise BridgeError('DOCUMENT_LIFECYCLE_NOT_VERIFIED', 'Upgrade the native bridge before original-path saving; no write was sent')
        documents = client.call('list_processing_documents')['documents']
        own = next((row for row in documents if row['document_id'] == document_id), None)
        if own is None or not own.get('path') or Path(own['path']).absolute() != target:
            raise ValueError('Save requires this window\'s own original path')
        if any(Path(row['path']).absolute() == target and row['document_id'] != document_id for row in documents if row.get('path')):
            raise ValueError('Another open document uses the target path; save or close it first')
        with _target_lock(target):
            if manifest(target) != expected:
                raise ValueError('Original changed before backup; no save was sent')
            work = _archive_root(target) / ('live-save-' + uuid4().hex)
            backup = work / 'backup' / target.name
            backup.parent.mkdir(parents=True)
            shutil.copy2(target, backup)
            shutil.copytree(target.with_suffix('.DATA'), backup.with_suffix('.DATA'))
            if manifest(backup) != expected or manifest(target) != expected:
                raise ValueError(f'Backup verification failed; no save was sent; diagnostic: {work}')
            journal = work / 'journal.json'
            result = {'path': str(target), 'backup_path': str(backup),
                      'journal_path': str(journal), 'original_backup_verified': True}
            state = {**result, 'original_manifest': expected, 'status': 'prepared'}

            def record(status):
                state['status'] = status
                with journal.open('w', encoding='utf-8') as stream:
                    json.dump(state, stream, ensure_ascii=False, indent=2)
                    stream.flush()
                    os.fsync(stream.fileno())

            record('prepared')
            try:
                saved = client.call('save_processing_document_existing', {'document_id': document_id, 'path': str(target)})
                required = ('roundtrip_verified', 'live_readback_verified', 'raw_data_verified',
                            'all_spectrum_models_verified', 'grid_bindings_verified',
                            'no_extra_windows_verified', 'same_window_retained', 'saved_state_verified')
                if not all(saved.get(key) is True for key in required):
                    raise ValueError('Native save verification incomplete')
                if saved['document']['document_id'] != document_id or Path(saved['document']['path']).absolute() != target:
                    raise ValueError('Native save changed the document identity or original path')
                state['saved_manifest'] = manifest(target)
                record('committed')
            except Exception as exc:
                record('inspection_required')
                raise BridgeError('ORIGINAL_SAVE_UNVERIFIED', 'Native save did not verify; full backup retained. Inspect the original before retrying.', result) from exc
            return {**saved, **result, 'in_place': True,
                    'note': 'Original saved with a verified full backup; same window retained. Reattach before further edits.'}


def apply_document_model(client, session_id: str, save_to_original: bool = True) -> dict:
    """Apply and, by default, complete a backed-up original-path save."""
    with client.exclusive(), live_transaction():
        status = client.call('status')
        if status.get('native_live_document_bridge_verified') is not True:
            raise BridgeError('LIVE_DOCUMENT_BRIDGE_NOT_VERIFIED', 'No live operation was sent')
        target = None
        if save_to_original:
            if status.get('native_document_lifecycle_verified') is not True:
                raise BridgeError('DOCUMENT_LIFECYCLE_NOT_VERIFIED', 'Upgrade the native bridge before original-path saving; no write was sent')
            info = client.call('get_spectrum_info', {'session_id': session_id})
            target = Path(info['source_path']).absolute()
            private_root = Path(os.environ.get('AVANTAGE_WORK_DIR', str(Path(os.environ.get('LOCALAPPDATA', str(Path.home()))) / 'AvantageMCP' / 'sessions'))).absolute()
            if target.is_relative_to(private_root) or any(part.startswith(('.avantage-native-save-', '.avantage-writeback-')) for part in target.parts):
                raise BridgeError('ORIGINAL_PATH_REQUIRED', 'Reattach the original VGP before applying; a private intermediate is not an original')
            expected = manifest(target)
        applied = client.call('apply_fit_to_document', {'session_id': session_id})
        if target is None:
            return applied
        if manifest(target) != expected:
            raise BridgeError('ORIGINAL_CHANGED', 'Original changed while preparing the model; inspect before saving', {'applied_document': applied.get('document')})
        saved = save_document_in_place(client, applied['document']['document_id'], str(target))
        return {**applied, 'document': saved['document'], 'path': str(target),
                'in_place': True, 'no_extra_windows_verified': True, 'saved_project_verified': True,
                'backup_path': saved['backup_path'], 'original_save': saved,
                'note': 'Model saved to the original VGP with a complete backup and the same window retained. Reattach before the next edit.'}
