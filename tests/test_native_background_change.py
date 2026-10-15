"""Existing native peak IDs and constraints survive an in-place background edit."""
from __future__ import annotations

import os
from pathlib import Path
import subprocess

import pytest

from avantage_mcp.bridge_client import BridgeClient, BridgeError
from test_native_projects import choose_fitted, native, open_project, project_copy, select
from test_native_project_save import (capture_project, comparable_model, source_hashes)


pytestmark = pytest.mark.skipif(
    os.environ.get("AVANTAGE_INTEGRATION") != "1" or not os.environ.get("AVANTAGE_VGP_FIXTURE"),
    reason="Requires installed Avantage and an explicitly supplied private VGP fixture",
)


def test_existing_background_edit_preserves_peak_ids_native_details_and_constraints(
    native, project_copy, tmp_path,
):
    original_hashes = source_hashes(project_copy)
    project = open_project(native, project_copy)
    project_before = capture_project(native, project)
    row = choose_fitted(project)
    selected = select(native, project, row)
    sid = selected["session_id"]
    peaks = selected["model"]["peaks"]
    assert len(peaks) >= 2
    first, second = peaks[:2]
    native.call("update_peak", {
        "session_id": sid, "peak_id": first["peak_id"], "patch": {
            "gaussian_lorentz": 0.35, "tail_mix": 0.12,
            "tail_height": 0.03, "tail_exponential": 0.25,
        },
    })
    native.call("set_peak_constraint", {
        "session_id": sid, "peak_id": first["peak_id"], "attribute": "gl",
        "constraint": {"type": "fixed", "value1": 0.35},
    })
    native.call("set_peak_constraint", {
        "session_id": sid, "peak_id": second["peak_id"], "attribute": "gl",
        "constraint": {"type": "linked", "reference_peak_id": first["peak_id"],
                       "operator": "multiply", "value1": 1},
    })
    before = native.call("get_fit_model", {"session_id": sid})
    result = native.call("set_background", {
        "session_id": sid, "kind": "shirley", "shirley_iterations": 80,
        "shirley_convergence": 0.4, "shirley_smooth": True,
        "start_binding_ev": selected["background"]["start_binding_ev"],
        "end_binding_ev": selected["background"]["end_binding_ev"],
    })
    after = native.call("get_fit_model", {"session_id": sid})
    assert result["background"]["kind"] == "shirley"
    assert after["background"] == result["background"]
    # Includes IDs, asymmetric parameters, all original range constraints and
    # the newly added fixed / linked constraints with their original references.
    assert after["model"]["peaks"] == before["model"]["peaks"]
    assert after["model"]["fit_executed"] is False
    assert native.call("get_model_curve", {"session_id": sid, "limit": 1})["count"] == 1

    saved = tmp_path / "changed-existing-background.vgp"
    exported = native.call("save_project_model", {"session_id": sid, "path": str(saved)})
    assert exported["roundtrip_verified"] is True
    # The existing peak IDs let the saver recover each original chemical/RSF
    # table entry instead of treating the peaks as new unquantified components.
    assert exported["primary_metadata_preserved"] is True
    with BridgeClient(timeout=90) as reopened:
        project_after = capture_project(reopened, open_project(reopened, saved))
    for index, original in project_before.items():
        if index == row["spectrum_index"]:
            assert project_after[index]["model"] == comparable_model(after["model"])
            assert project_after[index]["background"] == after["background"]
            for field in ("x", "intensity", "binding_energy"):
                assert project_after[index][field] == original[field]
        else:
            assert project_after[index] == original
    assert source_hashes(project_copy) == original_hashes


def test_rejected_existing_background_edit_retains_background_and_entire_model(
    native, project_copy,
):
    project = open_project(native, project_copy)
    selected = select(native, project, choose_fitted(project))
    sid = selected["session_id"]
    before = native.call("get_fit_model", {"session_id": sid})
    with pytest.raises(BridgeError, match="INVALID_ARGUMENT"):
        native.call("set_background", {
            "session_id": sid, "kind": "shirley", "shirley_iterations": 0,
        })
    assert native.call("get_fit_model", {"session_id": sid}) == before
    # A subsequent valid assignment in the same worker must still succeed.
    native.call("set_background", {"session_id": sid, "kind": "linear"})
    assert native.call("get_fit_model", {"session_id": sid})["model"]["peaks"] == before["model"]["peaks"]


def test_first_background_can_be_attached_to_an_existing_unconstrained_model(
    native, project_copy,
):
    project = open_project(native, project_copy)
    row = next(row for row in project["spectra"] if not row["fitted_peak_count"])
    sid = select(native, project, row)["session_id"]
    data = native.call("get_spectrum_data", {"session_id": sid, "limit": 100000})
    peak = native.call("add_peak", {"session_id": sid, "peak": {
        "centre_ev": data["binding_energy"][len(data["binding_energy"]) // 2],
        "height": 100, "fwhm_ev": 1,
    }})["peak"]
    native.call("set_background", {"session_id": sid, "kind": "linear"})
    assert native.call("get_fit_model", {"session_id": sid})["model"]["peaks"] == [peak]


ROLLBACK_HELPER = r'''
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Web.Script.Serialization;
using AvantageMcp;
using VGDATASTORELib;

// Only the test proxy injects a one-time failure, after the real native peak has
// accepted its new background. The product's rollback uses unmodified code and
// actual Avantage background / peak / constraint objects throughout.
sealed class FailAfterAssignment : RealProxy {
    readonly IAvFittedPeak native; bool armed=true;
    public FailAfterAssignment(IAvFittedPeak value):base(typeof(IAvFittedPeak)){native=value;}
    public override IMessage Invoke(IMessage message) {
        var call=(IMethodCallMessage)message;var values=call.Args;
        try {
            object result=((MethodInfo)call.MethodBase).Invoke(native,values);
            if(call.MethodName=="SetBackground"&&armed){armed=false;throw new InvalidOperationException("Injected post-assignment failure");}
            return new ReturnMessage(result,values,values.Length,call.LogicalCallContext,call);
        } catch(Exception error) {
            return new ReturnMessage(error is TargetInvocationException?error.InnerException:error,call);
        }
    }
}
class BackgroundRollbackCheck {
    static string bin;
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern bool SetDllDirectory(string path);
    [STAThread]static int Main(string[] args) {
        Program.InstallRoot=args[0];bin=Path.Combine(args[0],"bin");SetDllDirectory(bin);
        AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>{
            string path=Path.Combine(bin,new AssemblyName(e.Name).Name+".dll");
            return File.Exists(path)?Assembly.LoadFrom(path):null;
        };
        try{Check(args[1],true);Check(args[1],false);Console.WriteLine("native_post_assignment_rollback_verified");return 0;}
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    [MethodImpl(MethodImplOptions.NoInlining)]static void Check(string source,bool existingBackground) {
        using(var session=new SpectrumSession{Id="rollback-test",Data=new DataSpaceClass()}) {
            session.Data.LoadPersistFile(source,0,1,0);session.Refresh();
            if(existingBackground)session.BackgroundState=NativeBackground.Create(session,J.Obj("kind","linear"));
            var model=new NativePeakModel(session.Data);session.FitState=model;
            double centre=session.BindingEnergy[session.BindingEnergy.Length/2];
            var first=(Dictionary<string,object>)model.Add(J.Obj("label","rollback A","centre_ev",centre,"height",100.0,"fwhm_ev",1.2,"tail_mix",0.12,"tail_height",0.03,"tail_exponential",0.2));
            var second=(Dictionary<string,object>)model.Add(J.Obj("label","rollback B","centre_ev",centre+1,"height",40.0,"fwhm_ev",1.2));
            if(existingBackground)model.AttachBackground(session.BackgroundState.Background,typeof(IAvantageBackground));
            string a=J.Str(first,"peak_id"),b=J.Str(second,"peak_id");
            model.Constraint(a,"gl",J.Obj("type","fixed","value1",0.3));
            model.Constraint(a,"fwhm",J.Obj("type","range","value1",0.5,"value2",3.5));
            model.Constraint(b,"centre",J.Obj("type","linked","reference_peak_id",a,"operator","plus","value1",1.0));
            model.MarkFitResult(J.Obj("test_existing_report",true));
            var json=new JavaScriptSerializer();string before=json.Serialize(model.Info());
            var actual=model.Peaks.Select(p=>p.Native).ToArray();
            var snapshot=existingBackground?NativePeakTablePersistence.ReadPeaks(actual):null;
            var original=model.Peaks[1].Native;
            model.Peaks[1].Native=(IAvFittedPeak)new FailAfterAssignment(original).GetTransparentProxy();
            try {
                using(var replacement=NativeBackground.Create(session,J.Obj("kind","shirley"))) {
                    bool failed=false;
                    try{NativeModelBackgroundChange.Replace(model,replacement);}
                    catch(InvalidOperationException e){failed=e.Message=="Injected post-assignment failure";}
                    if(!failed)throw new Exception("Expected the post-assignment fault to propagate.");
                }
            } finally {model.Peaks[1].Native=original;}
            if(json.Serialize(model.Info())!=before)throw new Exception("Model IDs, parameters, constraint specs or fit report changed after rollback.");
            if(existingBackground&&!NativePeakTablePersistence.Equivalent(snapshot,NativePeakTablePersistence.ReadPeaks(actual)))
                throw new Exception("Native background, parameters or linked constraints changed after rollback.");
            if(!existingBackground)foreach(var peak in actual){
                IntPtr background=IntPtr.Zero;
                try{try{peak.GetBackground(out background);}catch(Exception e){if(e.HResult!=unchecked((int)0x80004003))throw;}
                    if(background!=IntPtr.Zero)throw new Exception("Initially absent background was not restored.");}
                finally{if(background!=IntPtr.Zero)Marshal.Release(background);}
            }
        }
    }
}
'''


def test_native_post_assignment_failure_rolls_back_background_parameters_and_links(
    project_copy, tmp_path,
):
    home = Path(os.environ.get("AVANTAGE_HOME", r"C:\Program Files\Thermo Scientific\Avantage"))
    compiler = Path(r"C:\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe")
    if not compiler.is_file():
        compiler = Path(os.environ.get("WINDIR", r"C:\Windows")) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
    if not compiler.is_file():
        pytest.skip("The rollback fault-injection helper requires a .NET Framework C# compiler")
    repo = Path(__file__).resolve().parents[1]
    helper = tmp_path / "BackgroundRollbackCheck.cs"
    helper.write_text(ROLLBACK_HELPER, encoding="utf-8")
    executable = tmp_path / "BackgroundRollbackCheck.exe"
    flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
    compiled = subprocess.run(
        [str(compiler), "/nologo", "/target:exe", "/platform:x64",
         "/main:BackgroundRollbackCheck", f"/out:{executable}",
         "/r:System.Web.Extensions.dll", "/r:Microsoft.CSharp.dll",
         f"/r:{home / 'bin/Interop.VGDATASTORELib.dll'}",
         *map(str, sorted((repo / "native").glob("*.cs"))), str(helper)],
        capture_output=True, timeout=60, creationflags=flags,
    )
    assert compiled.returncode == 0, (compiled.stdout + compiled.stderr).decode(errors="replace")
    source = next(project_copy.with_suffix(".DATA").glob("*C1s*.VGD"), None)
    if source is None:
        source = next(project_copy.with_suffix(".DATA").glob("*.vgd"), None)
    if source is None:
        pytest.skip("A one-dimensional companion VGD is needed for this fixture")
    verified = subprocess.run(
        [str(executable), str(home), str(source)],
        capture_output=True, timeout=60, creationflags=flags,
    )
    assert verified.returncode == 0, (verified.stdout + verified.stderr).decode(errors="replace")
    assert b"native_post_assignment_rollback_verified" in verified.stdout
