"""Exercise the real live-apply topology policy without Avantage or COM.

The production source ``native/LiveParameterPolicy.cs`` is compiled verbatim
together with a tiny probe that supplies only the ``Fault`` type. No Avantage
assembly is referenced, no document is opened, and nothing is sent to the
application: this checks the actual production code path that
``DirectLiveParameters.ValidateLiveParameterUpdate`` calls.
"""

import os
from pathlib import Path
import subprocess

import pytest

POLICY = Path(__file__).resolve().parents[1] / "native" / "LiveParameterPolicy.cs"

PROBE = r'''
using System;
namespace AvantageMcp {
public class Fault : Exception {
    public string Code;
    public Fault(string code, string message) : base(message) { Code = code; }
}
class LiveParameterPolicyProbe {
    static int Main(string[] args) {
        int existing, candidate;
        switch(args[0]) {
            case "empty-to-nonempty": existing = 0; candidate = 2; break;
            case "add": existing = 2; candidate = 3; break;
            case "remove": existing = 3; candidate = 2; break;
            case "empty-to-empty": existing = 0; candidate = 0; break;
            case "same": existing = 2; candidate = 2; break;
            default: Console.WriteLine("UNKNOWN_CASE"); return 2;
        }
        try {
            LiveParameterPolicy.ValidatePeakTopology(existing, candidate);
            Console.WriteLine("OK");
        } catch(Fault error) {
            Console.WriteLine(error.Code);
        } catch(Exception error) {
            Console.WriteLine("UNEXPECTED:" + error.GetType().Name);
            return 3;
        }
        return 0;
    }
}
}
'''


def _compiler() -> Path:
    for candidate in (
        Path(r"C:\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"),
        Path(os.environ.get("WINDIR", r"C:\Windows")) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe",
    ):
        if candidate.is_file():
            return candidate
    pytest.skip("C# compiler unavailable")


@pytest.fixture(scope="module")
def policy(tmp_path_factory):
    if os.name != "nt":
        pytest.skip("The live bridge only runs on Windows")
    work = tmp_path_factory.mktemp("live-parameter-policy")
    source = work / "Probe.cs"
    source.write_text(PROBE, encoding="utf-8")
    executable = work / "PolicyProbe.exe"
    built = subprocess.run(
        [str(_compiler()), "/nologo", "/target:exe", f"/out:{executable}",
         str(source), str(POLICY)],
        capture_output=True, text=True, timeout=60,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    )
    assert built.returncode == 0, built.stdout + built.stderr

    def run(case: str) -> str:
        result = subprocess.run([str(executable), case], capture_output=True, text=True, timeout=20,
                                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        assert result.returncode == 0, result.stdout + result.stderr
        return result.stdout.strip()

    return run


@pytest.mark.parametrize(
    ("case", "code"),
    [
        ("empty-to-nonempty", "LIVE_EMPTY_MODEL"),
        ("add", "LIVE_STRUCTURE_CHANGE_UNSUPPORTED"),
        ("remove", "LIVE_STRUCTURE_CHANGE_UNSUPPORTED"),
        ("empty-to-empty", "LIVE_STRUCTURE_CHANGE_UNSUPPORTED"),
    ],
)
def test_topology_changes_are_rejected_with_the_documented_code(policy, case, code):
    assert policy(case) == code


def test_identical_peak_count_is_allowed(policy):
    assert policy("same") == "OK"
