"""Compile and exercise the real readonly COM probe without the vendor app.

Uses Windows' in-memory structured storage, not mocks of its COM ABI. The
host-guard branch runs in a separate, short-lived fixture executable named
Avantage.exe; it never connects to the installed application or sample files.
"""
import os
from pathlib import Path
import shutil
import subprocess

import pytest

ROOT = Path(__file__).resolve().parents[1]


@pytest.fixture(scope="module")
def probe_executables(tmp_path_factory):
    if os.name != "nt":
        pytest.skip("Windows COM storage test")
    compiler = next((p for p in (
        Path(r"C:\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"),
        Path(os.environ.get("WINDIR", r"C:\Windows")) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe",
    ) if p.is_file()), None)
    if compiler is None:
        pytest.skip("C# compiler unavailable")
    work = tmp_path_factory.mktemp("readonly-context-probe")
    library = work / "AvantageMcp.InProcessProbe.dll"
    executable = work / "ProbeTests.exe"
    flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)

    def compile_cs(args):
        result = subprocess.run([str(compiler), "/nologo", "/platform:x64", *args],
                                capture_output=True, text=True, timeout=60,
                                creationflags=flags)
        assert result.returncode == 0, result.stdout + result.stderr

    compile_cs(["/target:library", f"/out:{library}", "/r:System.Web.Extensions.dll",
                str(ROOT / "native/inprocess/ContextProbe.cs"),
                str(ROOT / "native/inprocess/IdentityProbe.cs"),
                str(ROOT / "native/inprocess/NativeContextIdentity.cs")])
    compile_cs(["/target:exe", f"/out:{executable}", f"/r:{library}",
                str(ROOT / "tests/native/ContextProbeTests.cs")])
    host = work / "Avantage.exe"
    shutil.copyfile(executable, host)
    identity = work / "IdentityTests.exe"
    compile_cs(["/target:exe", f"/out:{identity}", f"/r:{library}",
                str(ROOT / "tests/native/NativeContextIdentityTests.cs")])
    return executable, host, identity


@pytest.mark.parametrize("host_callback,expected", [(False, "TOTAL 30"), (True, "TOTAL 8")])
def test_real_com_storage_and_callbacks(probe_executables, host_callback, expected):
    normal, host, _ = probe_executables
    command = [str(host), "--host-callback"] if host_callback else [str(normal)]
    result = subprocess.run(command, capture_output=True, text=True, timeout=20,
                            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    assert result.returncode == 0, result.stdout + result.stderr
    assert expected in result.stdout


def test_native_identity_memory_guards(probe_executables):
    _, _, executable = probe_executables
    result = subprocess.run([str(executable)], capture_output=True, text=True, timeout=20,
                            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    assert result.returncode == 0, result.stdout + result.stderr
    assert "TOTAL 16" in result.stdout
