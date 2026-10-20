"""Workspace and recovery-store housekeeping against real temporary folders.

These tests use a temporary AVANTAGE_WORK_DIR so they never touch a real
installation's private sessions or recovery material.
"""

from __future__ import annotations

import os
import time
from pathlib import Path

import pytest

from avantage_mcp import workspace


@pytest.fixture
def workdir(tmp_path: Path, monkeypatch) -> Path:
    root = tmp_path / "sessions"
    root.mkdir()
    monkeypatch.setenv("AVANTAGE_WORK_DIR", str(root))
    return root


def make_session(root: Path, name: str, *, age_hours: float, size: int = 1024) -> Path:
    folder = root / name
    folder.mkdir()
    payload = folder / "spectrum.vgd"
    payload.write_bytes(b"x" * size)
    marker = folder / workspace._LEASE_MARKER
    marker.touch()
    stamp = time.time() - age_hours * 3600
    os.utime(payload, (stamp, stamp))
    os.utime(marker, (stamp, stamp))
    os.utime(folder, (stamp, stamp))
    return folder


def test_work_root_honours_the_environment(workdir: Path):
    assert workspace.work_root() == workdir.absolute()


def test_usage_counts_folders_without_modifying_them(workdir: Path):
    make_session(workdir, "old", age_hours=100, size=2048)
    make_session(workdir, "new", age_hours=1, size=512)
    usage = workspace.workspace_usage()
    assert usage["folders"] == 2
    assert usage["total_bytes"] == 2560
    assert usage["oldest_age_hours"] >= 99
    assert len(list(workdir.iterdir())) == 2


def test_usage_predicts_what_a_prune_would_reclaim(workdir: Path):
    make_session(workdir, "old", age_hours=100, size=2048)
    make_session(workdir, "new", age_hours=1, size=512)
    usage = workspace.workspace_usage(stale_hours=72)
    assert usage["stale_hours"] == 72
    assert usage["stale_folders"] == 1
    assert usage["stale_bytes"] == 2048
    # The prediction must agree with the actual dry run.
    plan = workspace.prune_workspace(older_than_hours=72, dry_run=True)
    assert plan["eligible"] == usage["stale_folders"]


def test_usage_stale_counts_exclude_active_sessions(workdir: Path):
    folder = make_session(workdir, "active", age_hours=500, size=4096)
    usage = workspace.workspace_usage(
        keep_paths=[str(folder / "spectrum.vgd")], stale_hours=1
    )
    assert usage["active_folders"] == 1
    assert usage["stale_folders"] == 0
    assert usage["stale_bytes"] == 0


def test_dry_run_reports_without_deleting(workdir: Path):
    make_session(workdir, "old", age_hours=100)
    plan = workspace.prune_workspace(older_than_hours=72, dry_run=True)
    assert plan["eligible"] == 1
    assert plan["deleted"] == 0
    assert (workdir / "old").is_dir()


def test_recent_folders_are_never_eligible(workdir: Path):
    make_session(workdir, "old", age_hours=100)
    make_session(workdir, "fresh", age_hours=2)
    plan = workspace.prune_workspace(older_than_hours=72, dry_run=False)
    assert plan["deleted"] == 1
    assert (workdir / "old").is_dir() is False
    assert (workdir / "fresh").is_dir() is True


def test_active_sessions_are_kept_even_when_old(workdir: Path):
    folder = make_session(workdir, "active", age_hours=500)
    working = str(folder / "spectrum.vgd")
    plan = workspace.prune_workspace(older_than_hours=1, keep_paths=[working], dry_run=False)
    assert plan["deleted"] == 0
    assert plan["kept_active"] == 1
    assert folder.is_dir()


def test_unmarked_legacy_folders_are_never_deleted(workdir: Path):
    folder = make_session(workdir, "legacy-project", age_hours=500)
    (folder / workspace._LEASE_MARKER).unlink()
    usage = workspace.workspace_usage(stale_hours=0)
    result = workspace.prune_workspace(older_than_hours=0, dry_run=False)
    assert usage["unverified_folders"] == 1
    assert usage["stale_folders"] == result["eligible"] == 0
    assert result["kept_unverified"] == 1
    assert folder.is_dir()


def test_max_delete_bounds_one_call(workdir: Path):
    for index in range(5):
        make_session(workdir, f"s{index}", age_hours=100)
    plan = workspace.prune_workspace(older_than_hours=72, dry_run=False, max_delete=2)
    assert plan["deleted"] == 2
    assert plan["truncated"] is True
    assert len(list(workdir.iterdir())) == 3


def test_invalid_arguments_are_rejected(workdir: Path):
    with pytest.raises(ValueError):
        workspace.prune_workspace(older_than_hours=-1)
    with pytest.raises(ValueError):
        workspace.prune_workspace(max_delete=-1)


def test_missing_root_is_reported_not_created(tmp_path: Path, monkeypatch):
    missing = tmp_path / "absent"
    monkeypatch.setenv("AVANTAGE_WORK_DIR", str(missing))
    usage = workspace.workspace_usage()
    assert usage["exists"] is False
    assert usage["folders"] == 0
    assert not missing.exists()
    assert workspace.prune_workspace(dry_run=False)["deleted"] == 0


def _recovery(tmp_path: Path, monkeypatch) -> Path:
    root = tmp_path / "recovery"
    root.mkdir()
    monkeypatch.setenv("AVANTAGE_RECOVERY_ROOT", str(root))
    return root


def test_backup_listing_reads_journals(tmp_path: Path, monkeypatch):
    root = _recovery(tmp_path, monkeypatch)
    sample = root / "abc123"
    committed = sample / "live-save-1"
    committed.mkdir(parents=True)
    (committed / "backup").mkdir()
    (committed / "backup" / "2.vgp").write_bytes(b"x" * 100)
    (committed / "journal.json").write_text(
        '{"status": "committed", "target": "E:\\\\data\\\\2.vgp"}', encoding="utf-8"
    )
    listing = workspace.list_backups()
    assert listing["count"] == 1
    entry = listing["entries"][0]
    assert entry["kind"] == "live-save"
    assert entry["status"] == "committed"
    assert entry["has_backup"] is True
    assert entry["target"] == r"E:\data\2.vgp"


def test_live_save_journals_report_the_original_from_path(tmp_path: Path, monkeypatch):
    """project_writeback records live saves under "path", not "target"."""
    root = _recovery(tmp_path, monkeypatch)
    work = root / "abc" / "live-save-1"
    work.mkdir(parents=True)
    (work / "journal.json").write_text(
        '{"status": "committed", "path": "E:\\\\data\\\\2.vgp"}', encoding="utf-8"
    )
    entry = workspace.list_backups()["entries"][0]
    assert entry["target"] == r"E:\data\2.vgp"


def test_prune_backups_requires_confirmation(tmp_path: Path, monkeypatch):
    root = _recovery(tmp_path, monkeypatch)
    work = root / "abc" / "writeback-1"
    work.mkdir(parents=True)
    (work / "journal.json").write_text('{"status": "committed"}', encoding="utf-8")
    old = time.time() - 40 * 86400
    os.utime(work / "journal.json", (old, old))
    with pytest.raises(ValueError, match="confirm=true"):
        workspace.prune_backups(older_than_days=30, dry_run=False)
    assert work.is_dir()


def test_prune_backups_only_touches_settled_journals(tmp_path: Path, monkeypatch):
    root = _recovery(tmp_path, monkeypatch)
    old = time.time() - 40 * 86400
    for name, status in (("committed", "committed"), ("pending", "inspection_required")):
        work = root / "abc" / f"{name}-1"
        work.mkdir(parents=True)
        journal = work / "journal.json"
        journal.write_text(f'{{"status": "{status}"}}', encoding="utf-8")
        os.utime(journal, (old, old))
        os.utime(work, (old, old))
    plan = workspace.prune_backups(older_than_days=30, dry_run=True)
    assert plan["candidates"] == 1
    result = workspace.prune_backups(older_than_days=30, dry_run=False, confirm=True)
    assert result["deleted"] == 1
    assert not (root / "abc" / "committed-1").exists()
    assert (root / "abc" / "pending-1").is_dir()


def test_recent_backups_are_retained(tmp_path: Path, monkeypatch):
    root = _recovery(tmp_path, monkeypatch)
    work = root / "abc" / "live-save-1"
    work.mkdir(parents=True)
    (work / "journal.json").write_text('{"status": "committed"}', encoding="utf-8")
    assert workspace.prune_backups(older_than_days=30, dry_run=True)["candidates"] == 0
