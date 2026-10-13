"""Opt-in regressions that exercise real native data and error recovery.

The metadata fixture helper uses the installed COM library to write a separate
test VGD. It never changes installed samples or adds a public mutation API.
"""

from __future__ import annotations

import hashlib
import os
from pathlib import Path
import shutil
import subprocess

import pytest

from avantage_mcp.bridge_client import BridgeClient, BridgeError


pytestmark = pytest.mark.skipif(
    os.environ.get("AVANTAGE_INTEGRATION") != "1",
    reason="Requires an installed Avantage and opt-in AVANTAGE_INTEGRATION=1",
)


@pytest.fixture
def native():
    with BridgeClient(timeout=60) as client:
        yield client


@pytest.fixture
def source(tmp_path):
    home = Path(os.environ.get("AVANTAGE_HOME", r"C:\Program Files\Thermo Scientific\Avantage"))
    sample = home / "Sample Data" / "Avantage Spectra" / "PET1407.DATA" / "C1s.VGD"
    destination = tmp_path / "source.vgd"
    shutil.copy2(sample, destination)
    return destination


def peak(**changes):
    return {"label": "C-C", "centre_ev": 284.8, "height": 300, "fwhm_ev": 1.2, **changes}


def test_finite_amplitude_overflow_preserves_session(native, source):
    sid = native.call("open_spectrum", {"path": str(source)})["session_id"]
    created = native.call("add_peak", {"session_id": sid, "peak": peak(height=1e200)})
    original_pid = native.pid
    with pytest.raises(BridgeError) as error:
        native.call("get_model_curve", {"session_id": sid, "limit": 1})
    assert error.value.code == "NUMERICAL_OVERFLOW"
    assert native.pid == original_pid
    model = native.call("get_fit_model", {"session_id": sid})["model"]
    assert model["peaks"][0]["peak_id"] == created["peak"]["peak_id"]
    native.call("update_peak", {"session_id": sid, "peak_id": created["peak"]["peak_id"], "patch": {"height": 300}})
    assert native.call("get_model_curve", {"session_id": sid, "limit": 1})["metrics"]["rmse"] >= 0


def test_rejected_first_peak_does_not_create_model(native, source):
    sid = native.call("open_spectrum", {"path": str(source)})["session_id"]
    with pytest.raises(BridgeError) as error:
        native.call("add_peak", {"session_id": sid, "peak": peak(fwhm_ev=0)})
    assert error.value.code == "INVALID_ARGUMENT"
    assert native.call("get_fit_model", {"session_id": sid})["model"] is None
    assert native.call("charge_shift", {"session_id": sid, "delta_ev": 0.1})["dirty"] is True


def test_save_succeeds_after_original_source_is_renamed(native, source, tmp_path):
    sid = native.call("open_spectrum", {"path": str(source)})["session_id"]
    before = native.call("get_spectrum_data", {"session_id": sid})
    source.rename(tmp_path / "renamed-source.vgd")
    output = tmp_path / "saved.vgd"
    result = native.call("save_spectrum", {"session_id": sid, "path": str(output)})
    assert result["source_unchanged"] is None
    assert result["source_check_error"]
    assert result["sha256"].lower() == hashlib.sha256(output.read_bytes()).hexdigest()
    reopened = native.call("open_spectrum", {"path": str(output)})["session_id"]
    after = native.call("get_spectrum_data", {"session_id": reopened})
    for field in ("x", "intensity", "binding_energy"):
        assert after[field] == before[field]


METADATA_FIXTURE_SOURCE = r'''
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VGDATASTORELib;
class MetadataFixture {
    static string bin;
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern bool SetDllDirectory(string path);
    [STAThread] static int Main(string[] args) {
        bin=Path.Combine(args[0],"bin");SetDllDirectory(bin);
        AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=> {
            string path=Path.Combine(bin,new AssemblyName(e.Name).Name+".dll");
            return File.Exists(path)?Assembly.LoadFrom(path):null;
        };
        try {Run(args[1],args[2]);return 0;}
        catch(Exception e){Console.Error.WriteLine(e.GetType().Name+": "+e.Message);return 1;}
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Run(string source,string output) {
        var data=new DataSpaceClass();
        try {
            data.LoadPersistFile(source,0,1,0);
            double energy=Convert.ToDouble(data.GetProperty(DSPropSet.STDPROPSET,DSPropID.DS_SOPROPID_ENERGY));
            data.SetProperty(DSPropSet.STDPROPSET,DSPropID.DS_SOPROPID_ENERGY,energy+10.0);
            var persist=(System.Runtime.InteropServices.ComTypes.IPersistFile)data;
            persist.Save(output,false);persist.SaveCompleted(null);
        } finally {Marshal.FinalReleaseComObject(data);}
    }
}
'''


def test_model_fingerprint_includes_binding_energy_metadata(native, source, tmp_path):
    home = Path(os.environ.get("AVANTAGE_HOME", r"C:\Program Files\Thermo Scientific\Avantage"))
    candidates = (
        Path(r"C:\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"),
        Path(os.environ.get("WINDIR", r"C:\Windows")) / "Microsoft.NET" / "Framework64" / "v4.0.30319" / "csc.exe",
    )
    compiler = next((path for path in candidates if path.is_file()), None)
    if compiler is None:
        pytest.skip("A .NET Framework C# compiler is needed for the metadata fixture")
    helper_source = tmp_path / "MetadataFixture.cs"
    helper_executable = tmp_path / "MetadataFixture.exe"
    helper_source.write_text(METADATA_FIXTURE_SOURCE, encoding="utf-8")
    flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
    compiled = subprocess.run(
        [str(compiler), "/nologo", "/platform:x64", f"/out:{helper_executable}",
         f"/reference:{home / 'bin' / 'Interop.VGDATASTORELib.dll'}", str(helper_source)],
        capture_output=True, timeout=60, creationflags=flags,
    )
    assert compiled.returncode == 0, compiled.stdout.decode(errors="replace") + compiled.stderr.decode(errors="replace")
    changed = tmp_path / "changed-metadata.vgd"
    original_hash = hashlib.sha256(source.read_bytes()).digest()
    generated = subprocess.run(
        [str(helper_executable), str(home), str(source), str(changed)],
        capture_output=True, timeout=60, creationflags=flags,
    )
    assert generated.returncode == 0, generated.stdout.decode(errors="replace") + generated.stderr.decode(errors="replace")
    assert hashlib.sha256(source.read_bytes()).digest() == original_hash
    first = native.call("open_spectrum", {"path": str(source)})["session_id"]
    second = native.call("open_spectrum", {"path": str(changed)})["session_id"]
    before = native.call("get_spectrum_data", {"session_id": first})
    after = native.call("get_spectrum_data", {"session_id": second})
    assert after["x"] == before["x"] and after["intensity"] == before["intensity"]
    assert after["binding_energy"] == pytest.approx([value + 10 for value in before["binding_energy"]])
    native.call("add_peak", {"session_id": first, "peak": peak()})
    existing = native.call("add_peak", {"session_id": second, "peak": peak(centre_ev=294.8)})["peak"]
    recipe = tmp_path / "model.json"
    native.call("export_fit_model", {"session_id": first, "path": str(recipe)})
    with pytest.raises(BridgeError) as error:
        native.call("import_fit_model", {"session_id": second, "path": str(recipe)})
    assert error.value.code == "SPECTRUM_MISMATCH"
    assert native.call("get_fit_model", {"session_id": second})["model"]["peaks"][0]["peak_id"] == existing["peak_id"]
