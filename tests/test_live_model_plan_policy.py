"""Exercise the actual COM-free plan validator, including malformed rollback data."""
import os
from pathlib import Path
import subprocess
import pytest

ROOT = Path(__file__).resolve().parents[1]
VALID = ["valid", "valid-empty-source", "valid-clear", "valid-unc", "valid-linked",
         "valid-description-difference", "valid-old-missing-background"]
INVALID = [
    "request-id-malformed",
    "source-wrong-count", "source-duplicate", "source-reversed", "source-new-in-middle", "source-outside",
    "null-plan", "missing-top", "extra-top", "version-fraction", "integer-decimal-fraction",
    "integer-string", "integer-bool", "path-relative", "path-drive-relative", "path-url",
    "path-wrong-ext", "path-null", "bad-hash", "negative-index", "large-index",
    "dictionary-array", "string-array", "null-array", "too-many-peaks", "missing-peak-field",
    "extra-peak-field", "bad-peak-index", "empty-label", "long-label", "label-null",
    "number-string", "number-bool", "number-nan", "number-infinity", "negative-height",
    "zero-width", "bad-gl", "no-background", "mixed-background", "bg-bounds", "bg-zero-step",
    "extra-bg-field", "missing-extension", "extra-extension", "bad-extension-number",
    "constraint-self", "constraint-outside", "constraint-null", "constraint-cycle",
    "constraint-duplicate", "constraint-zero-operator", "constraint-division-zero",
    "constraint-unlinked-reference", "constraint-bool", "bad-old-model",
]


@pytest.fixture(scope="module")
def plan_policy(tmp_path_factory):
    if os.name != "nt":
        pytest.skip("Windows C# test")
    compiler = next((p for p in (
        Path(r"C:\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"),
        Path(os.environ.get("WINDIR", r"C:\Windows"))/"Microsoft.NET/Framework64/v4.0.30319/csc.exe",
    ) if p.is_file()), None)
    if compiler is None:
        pytest.skip("C# compiler unavailable")
    executable = tmp_path_factory.mktemp("live-model-policy")/"Policy.exe"
    flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    built = subprocess.run([str(compiler), "/nologo", "/platform:x64", f"/out:{executable}",
                            str(ROOT/"native/LiveModelPlanPolicy.cs"),
                            str(ROOT/"tests/native/LiveModelPlanPolicyTests.cs")],
                           capture_output=True, text=True, timeout=60, creationflags=flags)
    assert built.returncode == 0, built.stdout + built.stderr
    return executable


@pytest.mark.parametrize("case,valid", [(c, True) for c in VALID]+[(c, False) for c in INVALID])
def test_plan_validation(plan_policy, case, valid):
    result = subprocess.run([str(plan_policy), case], capture_output=True, text=True,
                            timeout=20, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    assert result.returncode == 0, result.stdout + result.stderr
    assert (result.stdout.strip() == "OK") if valid else result.stdout.startswith("LIVE_PLAN_INVALID:"), result.stdout
