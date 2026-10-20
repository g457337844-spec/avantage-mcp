"""Private workspace and recovery-store housekeeping.

The native bridge copies every opened spectrum into a per-session folder under
``%LOCALAPPDATA%\\AvantageMCP\\sessions``. Nothing removed those folders when a
session closed, so a long-lived installation accumulates one directory per
opened spectrum forever. This module owns the only code that may delete them,
plus read-only enumeration of the recovery stores.

Safety rules enforced here:

* A folder is eligible only after the age cutoff and with an unlocked native
  lease. Open sessions/projects across workers hold exclusive lease handles.
* Folders from older versions without a lease are always kept.
* Paths supplied by the caller as ``keep_paths`` are never selected.
* Symbolic links and reparse points are skipped rather than followed.
* Deletion is bounded per call and every failure is reported, never raised.
"""

from __future__ import annotations

import os
import shutil
import string
import time
from pathlib import Path
from typing import Iterable, Iterator

# A session folder holds the spectrum copy plus, briefly, an isolated fit job.
# Anything beyond this many entries is not a folder this tool created.
_MAX_WALK_ENTRIES = 5000
# Directory name prefixes produced by project_writeback.
_SAVE_PREFIXES = ("live-save-", "native-save-", "writeback-")
_LEASE_MARKER = ".avantage-mcp-lease"


def _lease_state(folder: Path) -> str:
    """Closed native leases are reclaimable; unknown or busy ones are kept.

    Native workers hold FileShare.None on Windows. They never reuse closed
    session/project directories. Non-Windows platforms have no verified sharing
    contract for this bridge and therefore cannot automatically reclaim them.
    """
    marker = folder / _LEASE_MARKER
    if os.name != "nt" or not marker.is_file() or _is_reparse(marker):
        return "unknown"
    try:
        with marker.open("r+b"):
            return "closed"
    except OSError:
        return "active"


def work_root() -> Path:
    """Resolve the private session root exactly as the native bridge does."""
    configured = os.environ.get("AVANTAGE_WORK_DIR")
    if configured:
        return Path(configured).expanduser().absolute()
    base = os.environ.get("LOCALAPPDATA") or str(Path.home())
    return (Path(base) / "AvantageMCP" / "sessions").absolute()


def _is_reparse(entry: os.DirEntry[str] | Path) -> bool:
    try:
        stat = entry.stat(follow_symlinks=False)
    except OSError:
        return True
    attributes = getattr(stat, "st_file_attributes", 0)
    return bool(attributes & 0x400) or entry.is_symlink()


def _walk_files(root: Path) -> Iterator[os.DirEntry[str]]:
    """Yield regular files below ``root`` without following links."""
    seen = 0
    stack = [str(root)]
    while stack:
        current = stack.pop()
        try:
            with os.scandir(current) as entries:
                for entry in entries:
                    seen += 1
                    if seen > _MAX_WALK_ENTRIES:
                        return
                    if entry.is_symlink():
                        continue
                    try:
                        if entry.is_dir(follow_symlinks=False):
                            stack.append(entry.path)
                            continue
                    except OSError:
                        continue
                    yield entry
        except OSError:
            continue


def folder_metrics(path: Path) -> tuple[int, float]:
    """Return ``(bytes, newest_mtime)`` for a folder, tolerating vanished files."""
    total = 0
    newest = 0.0
    for entry in _walk_files(path):
        try:
            stat = entry.stat(follow_symlinks=False)
        except OSError:
            continue
        total += stat.st_size
        newest = max(newest, stat.st_mtime)
    try:
        newest = max(newest, path.stat().st_mtime)
    except OSError:
        pass
    return total, newest


def _is_protected(folder: Path, keep: set[str]) -> bool:
    """True when ``folder`` is, contains, or sits under a protected path.

    Callers supply working-file paths (``<root>/<id>/spectrum.vgd``), so a
    folder is protected when it equals a kept path, is an ancestor of one, or
    is a descendant of one.
    """
    target = str(folder.absolute()).casefold()
    for item in keep:
        if target == item or item.startswith(target + os.sep) or target.startswith(item + os.sep):
            return True
    return False


def workspace_usage(keep_paths: Iterable[str] = (), *, stale_hours: float = 72.0) -> dict:
    """Summarize the private session root without modifying it.

    ``stale_folders`` / ``stale_bytes`` report what a prune at ``stale_hours``
    would reclaim, so the caller can size the job before running it.
    """
    root = work_root()
    keep = {str(Path(item).absolute()).casefold() for item in keep_paths}
    result = {
        "work_root": str(root),
        "exists": root.is_dir(),
        "folders": 0,
        "active_folders": 0,
        "unverified_folders": 0,
        "total_bytes": 0,
        "oldest_mtime": None,
        "newest_mtime": None,
        "stale_folders": 0,
        "stale_bytes": 0,
        "stale_hours": stale_hours,
    }
    if not root.is_dir():
        return result
    now = time.time()
    stale_cutoff = now - stale_hours * 3600
    oldest = newest = None
    for child in sorted(root.iterdir()):
        try:
            if not child.is_dir() or _is_reparse(child):
                continue
        except OSError:
            continue
        size, mtime = folder_metrics(child)
        result["folders"] += 1
        result["total_bytes"] += size
        oldest = mtime if oldest is None else min(oldest, mtime)
        newest = mtime if newest is None else max(newest, mtime)
        lease = _lease_state(child)
        if _is_protected(child, keep) or lease == "active":
            result["active_folders"] += 1
        elif lease != "closed":
            result["unverified_folders"] += 1
        elif mtime <= stale_cutoff:
            result["stale_folders"] += 1
            result["stale_bytes"] += size
    if oldest is not None:
        result["oldest_mtime"] = time.strftime("%Y-%m-%dT%H:%M:%S", time.localtime(oldest))
        result["oldest_age_hours"] = round((now - oldest) / 3600, 1)
    if newest is not None:
        result["newest_mtime"] = time.strftime("%Y-%m-%dT%H:%M:%S", time.localtime(newest))
    return result


def prune_workspace(
    *,
    older_than_hours: float = 72.0,
    keep_paths: Iterable[str] = (),
    dry_run: bool = True,
    max_delete: int = 400,
) -> dict:
    """Delete stale private session folders.

    Only folders whose newest file predates ``older_than_hours`` and whose
    native lease is present and unlocked are eligible.
    ``keep_paths`` (the working paths of sessions this worker still holds) are
    never removed. At most ``max_delete`` folders are removed per call.
    """
    if older_than_hours < 0:
        raise ValueError("older_than_hours must not be negative")
    if max_delete < 0:
        raise ValueError("max_delete must not be negative")
    root = work_root()
    summary = {
        "work_root": str(root),
        "dry_run": dry_run,
        "older_than_hours": older_than_hours,
        "scanned": 0,
        "eligible": 0,
        "deleted": 0,
        "kept_active": 0,
        "kept_unverified": 0,
        "bytes_freed": 0,
        "folders": [],
        "errors": [],
    }
    if not root.is_dir():
        return summary
    keep = {str(Path(item).absolute()).casefold() for item in keep_paths}
    cutoff = time.time() - older_than_hours * 3600
    candidates: list[tuple[float, int, Path]] = []
    for child in root.iterdir():
        try:
            if not child.is_dir() or _is_reparse(child):
                continue
        except OSError:
            continue
        summary["scanned"] += 1
        lease = _lease_state(child)
        if _is_protected(child, keep) or lease == "active":
            summary["kept_active"] += 1
            continue
        if lease != "closed":
            summary["kept_unverified"] += 1
            continue
        size, newest = folder_metrics(child)
        if newest > cutoff:
            continue
        candidates.append((newest, size, child))
    candidates.sort()
    summary["eligible"] = len(candidates)
    for _, size, child in candidates[:max_delete]:
        if not dry_run:
            if _lease_state(child) != "closed":
                summary["kept_active"] += 1
                continue
            try:
                shutil.rmtree(child)
            except OSError as exc:
                summary["errors"].append({"path": str(child), "error": str(exc)})
                continue
            summary["deleted"] += 1
            summary["bytes_freed"] += size
        summary["folders"].append(
            {"path": str(child), "bytes": size, "removed": not dry_run}
        )
    summary["truncated"] = len(candidates) > max_delete
    return summary


def recovery_roots() -> list[Path]:
    """List existing ``<drive>:\\AvantageMCP\\recovery`` stores."""
    configured = os.environ.get("AVANTAGE_RECOVERY_ROOT")
    if configured:
        root = Path(configured).expanduser().absolute()
        return [root] if root.is_dir() else []
    found: list[Path] = []
    for letter in string.ascii_uppercase:
        candidate = Path(f"{letter}:\\AvantageMCP\\recovery")
        try:
            if candidate.is_dir():
                found.append(candidate)
        except OSError:
            continue
    return found


def _read_journal(path: Path) -> dict:
    import json

    try:
        with path.open("r", encoding="utf-8") as stream:
            value = json.load(stream)
    except (OSError, ValueError):
        return {}
    return value if isinstance(value, dict) else {}


def list_backups(*, limit: int = 200) -> dict:
    """Enumerate recovery material written by save/writeback transactions."""
    if limit < 1 or limit > 5000:
        raise ValueError("limit must be between 1 and 5000")
    roots = recovery_roots()
    entries: list[dict] = []
    errors: list[dict] = []
    for root in roots:
        for sample in root.iterdir():
            if not sample.is_dir() or _is_reparse(sample):
                continue
            try:
                children = list(sample.iterdir())
            except OSError as exc:
                errors.append({"path": str(sample), "error": str(exc)})
                continue
            for work in children:
                if not work.is_dir() or _is_reparse(work):
                    continue
                name = work.name
                kind = next((p.rstrip("-") for p in _SAVE_PREFIXES if name.startswith(p)), "other")
                journal = work / "journal.json"
                state = _read_journal(journal) if journal.is_file() else {}
                size, newest = folder_metrics(work)
                # live-save journals record the original under "path";
                # writeback journals use "target". Accept either.
                target = state.get("target") or state.get("path")
                entries.append(
                    {
                        "kind": kind,
                        "path": str(work),
                        "bytes": size,
                        "modified": time.strftime("%Y-%m-%dT%H:%M:%S", time.localtime(newest)),
                        "age_hours": round((time.time() - newest) / 3600, 1),
                        "status": state.get("status"),
                        "target": str(target) if target else None,
                        "journal": str(journal) if journal.is_file() else None,
                        "has_backup": (work / "backup").is_dir(),
                    }
                )
    entries.sort(key=lambda item: item["modified"], reverse=True)
    total = sum(item["bytes"] for item in entries)
    return {
        "roots": [str(root) for root in roots],
        "count": len(entries),
        "total_bytes": total,
        "truncated": len(entries) > limit,
        "entries": entries[:limit],
        "errors": errors,
    }


def _settled_status(status: object) -> bool:
    return status in {"committed", "rolled_back"}


def prune_backups(
    *, older_than_days: float = 30.0, dry_run: bool = True, confirm: bool = False
) -> dict:
    """Remove recovery folders that a journalled transaction already finished.

    Only ``committed`` and ``rolled_back`` journals are eligible, and only once
    they are older than ``older_than_days``. ``native-save-*`` staging folders
    carry no journal, so they are left untouched. Deleting requires both
    ``dry_run=False`` and ``confirm=True``.
    """
    if older_than_days < 0:
        raise ValueError("older_than_days must not be negative")
    if not dry_run and not confirm:
        raise ValueError("Set confirm=true to delete backups; nothing was removed.")
    cutoff = time.time() - older_than_days * 86400
    listing = list_backups(limit=5000)
    removable = [
        item
        for item in listing["entries"]
        if _settled_status(item["status"]) and item["age_hours"] * 3600 > (time.time() - cutoff)
    ]
    result = {
        "dry_run": dry_run,
        "older_than_days": older_than_days,
        "candidates": len(removable),
        "deleted": 0,
        "bytes_freed": 0,
        "folders": [],
        "errors": [],
    }
    for item in removable:
        path = Path(item["path"])
        if not dry_run:
            try:
                shutil.rmtree(path)
            except OSError as exc:
                result["errors"].append({"path": str(path), "error": str(exc)})
                continue
            result["deleted"] += 1
            result["bytes_freed"] += item["bytes"]
        result["folders"].append({"path": str(path), "bytes": item["bytes"]})
    return result
