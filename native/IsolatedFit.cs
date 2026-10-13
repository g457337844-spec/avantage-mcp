using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace AvantageMcp {
public sealed partial class Engine {
    [ThreadStatic] static string childResultPath;
    [DllImport("kernel32.dll")] static extern void ExitProcess(uint exitCode);
    [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
    static JavaScriptSerializer JobJson(){return new JavaScriptSerializer{MaxJsonLength=16*1024*1024,RecursionLimit=64};}
    static void WriteJob(string path,object value){File.WriteAllText(path,JobJson().Serialize(value),new UTF8Encoding(false));}
    static Dictionary<string,object> ReadJob(string path){
        if(!File.Exists(path)||new FileInfo(path).Length>16*1024*1024)throw new Fault("NATIVE_FIT_RESULT","Isolated fit result is missing or too large.");
        return JobJson().Deserialize<Dictionary<string,object>>(File.ReadAllText(path,Encoding.UTF8));
    }
    object RunFitIsolated(SpectrumSession s,Dictionary<string,object> args){
        var original=Model(s);bool asymmetric=J.Bool(args,"asymmetric",false);
        int maximum=J.Int(args,"max_iterations",1000),timeout=J.Int(args,"timeout_seconds",90);
        double tolerance=J.Num(args,"tolerance",0.0001);
        if(maximum<1||maximum>100000||tolerance<=0||timeout<1||timeout>600)throw new Fault("INVALID_ARGUMENT","Fit limits: iterations 1–100000, positive tolerance, timeout 1–600 seconds.");
        if(s.BackgroundState==null)throw new Fault("BACKGROUND_REQUIRED","Set a background before fitting.");
        if(original.Peaks.Count==0)throw new Fault("PEAKS_REQUIRED","Add at least one peak before fitting.");
        EvaluateModel(s,asymmetric);
        string directory=Path.GetFullPath(Path.Combine(s.DirectoryPath,"fit-"+Guid.NewGuid().ToString("N")));
        string parent=Path.GetFullPath(s.DirectoryPath)+Path.DirectorySeparatorChar;
        if(!directory.StartsWith(parent,StringComparison.OrdinalIgnoreCase))throw new Fault("INVALID_PATH","Fit job must stay in its private session directory.");
        Directory.CreateDirectory(directory);
        string input=Path.Combine(directory,"input.vgd"),recipe=Path.Combine(directory,"input.json"),result=Path.Combine(directory,"result.json"),fitted=Path.Combine(directory,"fitted.json"),job=Path.Combine(directory,"job.json");
        Process child=null;var diagnostics=new StringBuilder();object logGate=new object();
        try {
            SaveSpectrum(s,input,false);ExportFitModel(s,recipe,false);
            using(var owner=Process.GetCurrentProcess())WriteJob(job,J.Obj("input",input,"recipe",recipe,"result",result,"fitted",fitted,"max_iterations",maximum,"tolerance",tolerance,"asymmetric",asymmetric,"parent_pid",owner.Id,"parent_start_ticks",owner.StartTime.ToUniversalTime().Ticks));
            var start=new ProcessStartInfo{FileName=Assembly.GetExecutingAssembly().Location,Arguments="--fit-job \""+job+"\"",UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
            start.EnvironmentVariables["AVANTAGE_WORK_DIR"]=Path.Combine(directory,"sessions");
            child=new Process{StartInfo=start};
            DataReceivedEventHandler receive=(sender,e)=>{if(e.Data!=null)lock(logGate){if(diagnostics.Length<32768)diagnostics.AppendLine(e.Data.Substring(0,Math.Min(e.Data.Length,32768-diagnostics.Length)));}};
            child.ErrorDataReceived+=receive;child.OutputDataReceived+=receive;
            if(!child.Start())throw new Fault("NATIVE_FIT_START","Could not start the isolated native fit host.");
            child.BeginErrorReadLine();child.BeginOutputReadLine();
            if(!child.WaitForExit(timeout*1000)){child.Kill();child.WaitForExit();throw new Fault("NATIVE_FIT_TIMEOUT","Native fit timed out; current spectrum and model were retained.");}
            child.WaitForExit();
            if(child.ExitCode!=0){
                if(child.ExitCode==2&&File.Exists(result)){var error=ArgumentObject(ReadJob(result),"error");throw new Fault(J.Str(error,"code"),J.Str(error,"message")+" Current spectrum and model were retained.");}
                throw new Fault("NATIVE_FIT_CRASH","Isolated native fit failed (exit "+child.ExitCode+"); current spectrum and model were retained. "+diagnostics.ToString());
            }
            var outcome=ReadJob(result);
            var report=ArgumentObject(outcome,"fit_report");
            var candidate=ReadJob(fitted);var candidateModel=ArgumentObject(candidate,"model");var candidatePeaks=candidateModel["peaks"] as IList;
            var expected=new HashSet<string>(original.Peaks.Select(p=>p.Id));
            if(candidatePeaks==null||candidatePeaks.Count!=expected.Count||!expected.SetEquals(candidatePeaks.Cast<Dictionary<string,object>>().Select(p=>J.Str(p,"peak_id"))))throw new Fault("NATIVE_FIT_RESULT","Fit result peak identities differ; current model was retained.");
            // Import builds and validates a complete replacement before releasing the old model.
            ImportFitModel(s,fitted,false,true);Model(s).MarkFitResult(report);
            report["process_isolated"]=true;
            return J.Obj("session_id",s.Id,"fit_report",report,"model",Model(s).Info());
        } finally {
            if(child!=null){try{if(!child.HasExited){child.Kill();child.WaitForExit();}}catch(InvalidOperationException){}finally{child.Dispose();}}
            // directory was checked against the exclusively owned session path above.
            if(Directory.Exists(directory))try{Directory.Delete(directory,true);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }
    internal static int RunFitChild(string jobPath){
        SetErrorMode(0x0001|0x0002|0x8000);
        var job=ReadJob(Path.GetFullPath(jobPath));
        childResultPath=J.Str(job,"result");
        int parentPid=J.Int(job,"parent_pid",0);long parentTicks=Convert.ToInt64(job["parent_start_ticks"]);
        // If the MCP bridge is stopped, this child's native state must also be reclaimed.
        var watchdog=new Timer(state=>{try{using(var owner=Process.GetProcessById(parentPid))if(owner.HasExited||owner.StartTime.ToUniversalTime().Ticks!=parentTicks)ExitProcess(4);}catch{ExitProcess(4);}},null,1000,1000);
        var engine=new Engine();
        var info=(Dictionary<string,object>)engine.OpenSpectrum(J.Str(job,"input"));var s=engine.Session(J.Obj("session_id",info["session_id"]));
        engine.ImportFitModel(s,J.Str(job,"recipe"),false,true);
        NativeSolver.Run(s,engine.Model(s),J.Int(job,"max_iterations",1000),J.Num(job,"tolerance",0.0001),J.Bool(job,"asymmetric",true),report=>{
            // Read back all native parameters and persist them while the native objects
            // are alive. Successful fitting does not enter the non-reentrant destructor.
            engine.ExportFitModel(s,J.Str(job,"fitted"),false);
            WriteJob(J.Str(job,"result"),J.Obj("fit_report",report));
            GC.KeepAlive(watchdog);Console.Out.Flush();Console.Error.Flush();ExitProcess(0);
        });
        return 1;
    }
    internal static void FailFitChild(Exception error){
        if(childResultPath==null)return;
        var fault=error as Fault;
        try{WriteJob(childResultPath,J.Obj("error",J.Obj("code",fault==null?"NATIVE_FIT_FAILED":fault.Code,"message",error.Message)));}finally{ExitProcess(2);}
    }
}
}
