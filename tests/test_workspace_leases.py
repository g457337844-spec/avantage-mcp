"""Exercise the actual native lease helper across Windows processes, without COM."""

import os
from pathlib import Path
import subprocess
import time

import pytest

from avantage_mcp import workspace


@pytest.mark.skipif(os.name != "nt", reason="Native bridge uses Windows sharing semantics")
def test_native_project_and_spectrum_leases_protect_other_workers(tmp_path, monkeypatch):
    compiler = Path(r"C:\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe")
    if not compiler.is_file():
        compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
    if not compiler.is_file():
        pytest.skip("C# compiler unavailable")
    root = tmp_path / "sessions"
    root.mkdir()
    monkeypatch.setenv("AVANTAGE_WORK_DIR", str(root))
    spectrum, project = root / "spectrum-worker", root / "project-worker"
    for folder in (spectrum, project):
        folder.mkdir()
        (folder / "data").write_bytes(b"private graph")
    ready, released = tmp_path / "ready", tmp_path / "released"
    source = tmp_path / "Probe.cs"
    source.write_text('''
using System;
using System.IO;
using AvantageMcp;
class LeaseProbe {
    public static void Main(string[] args) {
        using(var leases=new WorkspaceLeases(args[0])) {
            leases.Sync(new[]{args[1],args[2]});
            File.WriteAllText(args[3],"ready");
            Console.ReadLine();
            leases.Sync(new[]{args[1]});
            File.WriteAllText(args[4],"released");
            Console.ReadLine();
        }
    }
}
''', encoding="utf-8")
    executable = tmp_path / "LeaseProbe.exe"
    native = Path(__file__).resolve().parents[1] / "native/WorkspaceLeases.cs"
    subprocess.run([str(compiler), "/nologo", "/target:exe", f"/out:{executable}",
                    str(source), str(native)], check=True, capture_output=True, timeout=30)
    creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    process = subprocess.Popen([str(executable), str(root), str(spectrum), str(project),
                                str(ready), str(released)], stdin=subprocess.PIPE,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               creationflags=creationflags)

    def wait_for(path):
        deadline = time.monotonic() + 10
        while not path.exists():
            assert process.poll() is None, "Lease holder exited before readiness"
            assert time.monotonic() < deadline, "Lease holder did not become ready"
            time.sleep(0.02)

    try:
        wait_for(ready)
        usage = workspace.workspace_usage(stale_hours=0)
        result = workspace.prune_workspace(older_than_hours=0, dry_run=False)
        assert usage["active_folders"] == result["kept_active"] == 2
        assert result["deleted"] == 0
        assert spectrum.is_dir() and project.is_dir()
        process.stdin.write(b"close-project\n")
        process.stdin.flush()
        wait_for(released)
        result = workspace.prune_workspace(older_than_hours=0, dry_run=False)
        assert result["deleted"] == result["kept_active"] == 1
        assert spectrum.is_dir() and not project.exists()
        process.communicate(b"close-spectrum\n", timeout=10)
        assert process.returncode == 0
        assert workspace.prune_workspace(older_than_hours=0, dry_run=False)["deleted"] == 1
    finally:
        if process.poll() is None:
            process.kill()
        process.communicate(timeout=10)
