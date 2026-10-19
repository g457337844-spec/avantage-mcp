from pathlib import Path
import shutil

import pytest

from avantage_mcp.project_writeback import manifest, publish_bundle


@pytest.fixture(autouse=True)
def isolated_recovery(tmp_path, monkeypatch):
    monkeypatch.setenv('AVANTAGE_RECOVERY_ROOT', str(tmp_path / 'recovery'))


def bundle(folder, content):
    folder.mkdir(parents=True)
    path = folder / '2.vgp'
    path.write_text(content)
    data = path.with_suffix('.DATA')
    (data / 'nested').mkdir(parents=True)
    (data / 'nested' / 'spectrum.vgd').write_text(content + '-data')
    return path


def test_replacement_preserves_complete_backup(tmp_path):
    old = bundle(tmp_path / 'original', 'original')
    new = bundle(tmp_path / 'new', 'new')
    expected = manifest(old)
    result = publish_bundle(new, old, expected)
    assert manifest(old) == manifest(new)
    assert manifest(Path(result['backup_path'])) == expected
    assert result['published_bytes_verified']
    assert Path(result['backup_path']).is_relative_to(tmp_path / 'recovery')
    assert not list(old.parent.glob('.avantage-*'))


def test_concurrent_change_is_rejected(tmp_path):
    old = bundle(tmp_path / 'original', 'original')
    new = bundle(tmp_path / 'new', 'new')
    expected = manifest(old)
    old.write_text('user changed it')
    with pytest.raises(ValueError, match='changed'):
        publish_bundle(new, old, expected)
    assert old.read_text() == 'user changed it'


@pytest.mark.parametrize('phase', [1, 2, 3, 4])
def test_every_rename_failure_restores_original(tmp_path, monkeypatch, phase):
    old = bundle(tmp_path / 'original', 'original')
    new = bundle(tmp_path / 'new', 'new')
    expected = manifest(old)
    rename = Path.rename
    count = 0

    def fail_once(self, dest):
        nonlocal count
        count += 1
        if count == phase:
            raise PermissionError('Injected lock failure')
        return rename(self, dest)

    monkeypatch.setattr(Path, 'rename', fail_once)
    with pytest.raises(PermissionError):
        publish_bundle(new, old, expected)
    assert manifest(old) == expected
    assert manifest(new)


def test_copy_failure_cannot_touch_original(tmp_path, monkeypatch):
    old = bundle(tmp_path / 'original', 'original')
    new = bundle(tmp_path / 'new', 'new')
    expected = manifest(old)
    def fail(*args, **kwargs):
        raise OSError('Disk full')
    monkeypatch.setattr(shutil, 'copytree', fail)
    with pytest.raises(OSError):
        publish_bundle(new, old, expected)
    assert manifest(old) == expected


def test_basename_and_incomplete_bundle_rejected(tmp_path):
    old = bundle(tmp_path / 'original', 'original')
    new = bundle(tmp_path / 'new', 'new')
    different = new.with_name('different.vgp')
    new.rename(different)
    with pytest.raises(ValueError, match='basename'):
        publish_bundle(different, old, manifest(old))
    with pytest.raises(ValueError, match='existing VGP'):
        manifest(new)


def test_existing_lock_leaves_original_untouched(tmp_path):
    old = bundle(tmp_path / 'original', 'original')
    new = bundle(tmp_path / 'new', 'new')
    expected = manifest(old)
    (old.parent / '.2.vgp.avantage-writeback.lock').write_text('another writer')
    with pytest.raises(FileExistsError):
        publish_bundle(new, old, expected)
    assert manifest(old) == expected
