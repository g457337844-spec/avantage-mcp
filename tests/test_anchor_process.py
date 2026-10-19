"""Exercise anchor launch pipe ownership without any vendor application or COM call."""
import os
from pathlib import Path
import subprocess

import pytest


def test_detached_anchor_does_not_hold_protocol_pipes(tmp_path):
    if os.name!="nt":pytest.skip("Windows process handle ownership test")
    candidates=[Path(r"C:\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"),
                Path(os.environ.get("WINDIR",r"C:\Windows"))/"Microsoft.NET/Framework64/v4.0.30319/csc.exe"]
    compiler=next((p for p in candidates if p.is_file()),None)
    if compiler is None:pytest.skip("Requires a local .NET Framework C# compiler")
    source=tmp_path/"AnchorHarness.cs"
    source.write_text(r'''
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
namespace AvantageMcp {
class Fault:Exception {public Fault(string code,string message):base(message){}}
static class ProcessingCom {
 public const string UiId="00000000-0000-0000-0000-000000000000";
 public static IntPtr Query(IntPtr value,string id){throw new Exception("COM must not run in this test");}
}
class Harness {
 static void Main(string[] args){
  string root=Environment.GetEnvironmentVariable("AVANTAGE_ANCHOR_TEST_ROOT");
  if(args.Length>0){
   File.WriteAllText(Path.Combine(root,"child-started"),Process.GetCurrentProcess().Id.ToString());
   var timer=Stopwatch.StartNew();
   while(!File.Exists(Path.Combine(root,"stop"))&&timer.ElapsedMilliseconds<30000)Thread.Sleep(50);
   return;
  }
  typeof(ApplicationAnchor).GetMethod("StartDetachedAnchor",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{Process.GetCurrentProcess()});
  Console.WriteLine("worker completed");
 }
}
}
''',encoding="utf-8")
    executable=tmp_path/"AnchorHarness.exe"
    anchor=Path(__file__).resolve().parents[1]/"native/ApplicationAnchor.cs"
    subprocess.run([str(compiler),"/nologo","/target:exe","/r:Microsoft.CSharp.dll",
                    "/out:"+str(executable),str(source),str(anchor)],check=True,capture_output=True)
    try:
        result=subprocess.run([str(executable)],env={**os.environ,"AVANTAGE_ANCHOR_TEST_ROOT":str(tmp_path)},
                              capture_output=True,text=True,timeout=8)
        assert result.returncode==0,result.stderr
        assert result.stdout.strip()=="worker completed"
        # The worker's captured stdout/stderr must reach EOF while its retained
        # child is still running. Otherwise MCP shutdown can block indefinitely.
        import time
        deadline=time.monotonic()+3
        while not (tmp_path/"child-started").exists() and time.monotonic()<deadline:time.sleep(.05)
        assert (tmp_path/"child-started").is_file()
    finally:
        (tmp_path/"stop").touch()
