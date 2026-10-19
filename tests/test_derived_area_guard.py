"""Run the native live-area guard without connecting to Avantage or opening data."""
import os
from pathlib import Path
import subprocess

import pytest


@pytest.fixture(scope="module")
def guard(tmp_path_factory):
    if os.name != "nt":
        pytest.skip("Windows .NET compiler required")
    root = Path(__file__).resolve().parents[1]
    work = tmp_path_factory.mktemp("derived-area-guard")
    bridge = work / "GuardBridge.exe"
    built = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(root / "scripts/build-native.ps1"),
                            "-OutputPath", str(bridge)], capture_output=True, text=True, timeout=40)
    assert built.returncode == 0, built.stdout + built.stderr
    source = work / "GuardProbe.cs"
    source.write_text(r'''
using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
class GuardProbe {
 static int Main(string[] args) {
  AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=> {
   string p=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
    "Thermo Scientific","Avantage","bin",new AssemblyName(e.Name).Name+".dll");
   return File.Exists(p)?Assembly.LoadFrom(p):null;
  };
  var spec=new Dictionary<string,object>{{"attribute",1},{"type",6},{"value1",1.0},{"value2",2000.0}};
  var before=new Dictionary<string,object>{{"area",1000.0},{"height",500.0},
   {"centre_kinetic_ev",500.0},{"native_gl_combine_method",0},
   {"constraints",new List<Dictionary<string,object>>{spec}}};
  var after=new Dictionary<string,object>(before);after["area"]=1000.01;
  switch(args[1]) {
   case "fixed":spec["type"]=1;break;
   case "large":after["area"]=1001.0;break;
   case "shape":after["height"]=500.001;break;
   case "nan":after["area"]=Double.NaN;break;
   case "infinite":after["area"]=Double.PositiveInfinity;break;
   case "range":spec["value2"]=1000.005;break;
   case "zero":before["area"]=0.0;break;
   case "missing":((List<Dictionary<string,object>>)before["constraints"]).Clear();break;
  }
  var method=Assembly.LoadFrom(args[0]).GetType("AvantageMcp.Engine")
   .GetMethod("CanCanonicalizeFreeArea",BindingFlags.NonPublic|BindingFlags.Static);
  Console.WriteLine(method.Invoke(null,new object[]{before,after}));return 0;
 }
}
''', encoding="utf-8")
    probe = work / "GuardProbe.exe"
    built = subprocess.run([r"C:\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe", "/nologo", "/platform:x64",
                            f"/out:{probe}", str(source)], capture_output=True, text=True, timeout=30)
    assert built.returncode == 0, built.stdout + built.stderr

    def run(case):
        result = subprocess.run([str(probe), str(bridge), case], capture_output=True, text=True, timeout=10)
        assert result.returncode == 0, result.stdout + result.stderr
        return result.stdout.strip() == "True"
    return run


@pytest.mark.parametrize("case", ["fixed", "large", "shape", "nan", "infinite", "range", "zero", "missing"])
def test_rejects_noncanonical_area_changes(guard, case):
    assert not guard(case)


def test_accepts_only_small_free_derived_area_change(guard):
    assert guard("small")
