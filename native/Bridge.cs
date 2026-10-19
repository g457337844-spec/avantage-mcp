using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using VGDATASTORELib;

namespace AvantageMcp {
public static class Program {
    public static string InstallRoot;
    [DllImport("kernel32", CharSet=CharSet.Unicode)] static extern bool SetDllDirectory(string path);
    [STAThread] public static int Main(string[] arguments) {
        Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
        InstallRoot = Environment.GetEnvironmentVariable("AVANTAGE_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Thermo Scientific", "Avantage");
        string bin = Path.Combine(InstallRoot, "bin");
        SetDllDirectory(bin);
        AppDomain.CurrentDomain.AssemblyResolve += (s,e) => {
            string name = new AssemblyName(e.Name).Name;
            string path = Path.Combine(bin, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        try {
            if(arguments.Length==3&&arguments[0]=="--live-anchor")return ApplicationAnchor.Run(int.Parse(arguments[1]),long.Parse(arguments[2]));
            return arguments.Length==2 && arguments[0]=="--fit-job" ? Engine.RunFitChild(arguments[1]) : Run();
        } catch(Exception e) { Engine.FailFitChild(e);Console.Error.WriteLine(e.ToString()); return 1; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int Run() {
        var json = new JavaScriptSerializer { MaxJsonLength=64*1024*1024, RecursionLimit=64 };
        using(var engine = new Engine()) {
            string line;
            while((line=Console.ReadLine())!=null) {
                object id=null;
                try {
                    var req = json.Deserialize<Dictionary<string,object>>(line);
                    if(req==null || !req.ContainsKey("id")) throw new Fault("INVALID_REQUEST","Request id is required.");
                    id=req["id"];
                    var args=req.ContainsKey("arguments") ? req["arguments"] as Dictionary<string,object> : null;
                    object result=engine.Execute(J.Str(req,"operation"),args ?? new Dictionary<string,object>());
                    Console.WriteLine(json.Serialize(J.Obj("id",id,"ok",true,"result",result)));
                } catch(Exception ex) {
                    var e=ex is TargetInvocationException && ex.InnerException!=null ? ex.InnerException : ex;
                    var f=e as Fault;
                    Console.Error.WriteLine(DateTime.UtcNow.ToString("O")+" "+e.ToString());
                    Console.WriteLine(json.Serialize(J.Obj("id",id,"ok",false,"error",J.Obj("code",f==null ? (e is COMException ? "COM_ERROR" : "OPERATION_FAILED") : f.Code,"message",e.Message,"details",J.Obj("hresult","0x"+e.HResult.ToString("X8"))))));
                }
                Console.Out.Flush();
            }
        }
        return 0;
    }
}
public class Fault : Exception { public string Code; public Fault(string code,string message):base(message){Code=code;} }
public static class J {
    public static Dictionary<string,object> Obj(params object[] a){var d=new Dictionary<string,object>();for(int i=0;i<a.Length;i+=2)d[(string)a[i]]=a[i+1];return d;}
    public static string Str(IDictionary<string,object> a,string key,string fallback=null){object v; if(a.TryGetValue(key,out v)&&v!=null)return Convert.ToString(v,CultureInfo.InvariantCulture); if(fallback!=null)return fallback;throw new Fault("INVALID_ARGUMENT","Missing argument: "+key);}
    public static double Num(IDictionary<string,object> a,string key,double fallback=double.NaN){object v;double n;if(a.TryGetValue(key,out v)&&v!=null)n=Convert.ToDouble(v,CultureInfo.InvariantCulture);else n=fallback;if(double.IsNaN(n)||double.IsInfinity(n))throw new Fault("INVALID_ARGUMENT","Expected finite number: "+key);return n;}
    public static int Int(IDictionary<string,object> a,string key,int fallback){double n=Num(a,key,fallback);if(n!=Math.Truncate(n)||n<int.MinValue||n>int.MaxValue)throw new Fault("INVALID_ARGUMENT","Expected integer: "+key);return (int)n;}
    public static bool Bool(IDictionary<string,object> a,string key,bool fallback=false){object v;return a.TryGetValue(key,out v)&&v!=null ? Convert.ToBoolean(v) : fallback;}
    public static void Release(object o){if(o!=null&&Marshal.IsComObject(o))Marshal.FinalReleaseComObject(o);}
}
public sealed class SpectrumSession : IDisposable {
    public string Id, SourcePath, WorkingPath, SourceHash, DirectoryPath;
    public string NativeSourcePath;
    public DataSpaceClass Data;
    public LiveSpectrumLink LiveLink;
    public string SourceProjectId;
    public int ProjectSpectrumIndex = -1;
    public List<string> ProjectOriginalPeakIds = new List<string>();
    public double[] X, Y, BindingEnergy;
    public List<Dictionary<string,object>> Axes;
    public double SourceEnergy;
    public bool Dirty;
    public object FitState;
    public NativeBackground BackgroundState;
    public string EnergyScale;
    public void Refresh() {
        var da=new DataAxesClass(); var sa=new SpaceAxesClass();
        try {
            Data.GetDataAxes(da); Data.GetSpaceAxes(sa);
            int total=da.GetSize();
            if(total<1 || total>10000000)throw new Fault("DATA_SIZE","Data point count is outside supported range (1–10,000,000).");
            int n=0;sa.GetNAxes(out n); Axes=new List<Dictionary<string,object>>();
            for(int i=0;i<n;i++) {
                string label,symbol,unit; sbyte linear;double start,step;int size; DSAxisType type;
                sa.GetAxisInfo(i,out label,out symbol,out unit,out linear,out start,out step,out size,out type);
                Axes.Add(J.Obj("index",i,"label",label,"symbol",symbol,"unit",unit,"linear",linear!=0,"start",start,"increment",step,"size",size,"type",(int)type));
            }
            // Native storage can contain multidimensional scans; fit only selected 1D spectra.
            X=new double[total];Y=new double[total];int count=total;
            Data.GetData(da,out Y[0],ref count);
            if(count!=total)throw new Fault("DATA_SIZE","Native read returned an unexpected point count.");
            bool oneDimensional=n==1 && Convert.ToInt32(Axes[0]["size"])==total;
            if(oneDimensional) {for(int i=0;i<total;i++)sa.GetAxisPoint(0,i,out X[i]);}
            else {for(int i=0;i<total;i++)X[i]=i;}
            SourceEnergy=0;
            try {SourceEnergy=Convert.ToDouble(Data.GetProperty(DSPropSet.STDPROPSET,DSPropID.DS_SOPROPID_ENERGY));} catch(COMException){}
            EnergyScale="index";BindingEnergy=null;
            if(oneDimensional && String.Equals(Convert.ToString(Axes[0]["unit"]),"eV",StringComparison.OrdinalIgnoreCase)) {
                // Avantage stores the energy axis as kinetic energy; its display scale is separate.
                EnergyScale="kinetic";
                if(SourceEnergy>0){BindingEnergy=EnergyConversions.KineticToBinding(Data,X);}
            }
        } finally {J.Release(da);J.Release(sa);}
    }
    public Dictionary<string,object> Info(){return J.Obj("session_id",Id,"source_path",SourcePath,"source_sha256",SourceHash,"working_path",WorkingPath,"point_count",Y.Length,"axes",Axes,"raw_energy_scale",EnergyScale,"source_energy_ev",SourceEnergy,"binding_energy_available",BindingEnergy!=null,"dirty",Dirty,"has_fit_model",FitState!=null,"live_document",LiveLink==null?null:LiveLink.Info());}
    public void Dispose(){var fitted=FitState as IDisposable;if(fitted!=null)fitted.Dispose();FitState=null;if(BackgroundState!=null)BackgroundState.Dispose();BackgroundState=null;J.Release(Data);Data=null;if(LiveLink!=null){LiveLink.Dispose();LiveLink=null;}}
}
public sealed partial class Engine : IDisposable {
    internal static void Trace(string step){if(Environment.GetEnvironmentVariable("AVANTAGE_TRACE")=="1"){string message=DateTime.UtcNow.ToString("O")+" "+step;Console.Error.WriteLine(message);string path=Environment.GetEnvironmentVariable("AVANTAGE_TRACE_PATH");if(!String.IsNullOrWhiteSpace(path))File.AppendAllText(path,message+Environment.NewLine);}}
    readonly Dictionary<string,SpectrumSession> sessions=new Dictionary<string,SpectrumSession>();
    readonly string workRoot;
    public Engine(){workRoot=Path.GetFullPath(Environment.GetEnvironmentVariable("AVANTAGE_WORK_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AvantageMCP","sessions"));Directory.CreateDirectory(workRoot);}
    public SpectrumSession Session(Dictionary<string,object>a){string id=J.Str(a,"session_id");SpectrumSession s;if(!sessions.TryGetValue(id,out s))throw new Fault("SESSION_NOT_FOUND","Session is not open in this worker. Reopen the source after worker restart.");return s;}
    public object Execute(string operation,Dictionary<string,object>a){
        switch(operation) {
            case "status": return Status();
            case "application_status": return AppControl.Status();
            case "open_processing_project": return OpenProcessingProject(J.Str(a,"path"));
            case "list_project_spectra": return ListProjectSpectra(J.Str(a,"project_id"));
            case "open_project_spectrum": return OpenProjectSpectrum(J.Str(a,"project_id"),J.Int(a,"spectrum_index",-1));
            case "save_project_model": return SaveProjectModel(Session(a),J.Str(a,"path"));
            case "close_processing_project": return CloseProcessingProject(J.Str(a,"project_id"));
            case "list_processing_documents": return Documents.List();
            case "list_document_spectra": return Documents.Spectra(J.Str(a,"document_id"));
            case "open_processing_document": throw new Fault("LIVE_OPEN_NOT_VERIFIED","Automatic document opening is disabled after an application lifecycle failure. Open the original manually; no application call was sent.");
            case "attach_document_spectrum": return AttachDocumentSpectrum(a);
            case "apply_fit_to_document": if(!ApplicationAnchor.Enabled)throw new Fault("LIVE_APPLY_DISABLED","Live writes were disabled by AVANTAGE_DISABLE_LIVE.");return ApplyFitToDocument(Session(a));
            case "save_processing_document":
            case "save_processing_document_existing": if(!ApplicationAnchor.Enabled)throw new Fault("LIVE_SAVE_DISABLED","Live saves were disabled by AVANTAGE_DISABLE_LIVE.");return SaveProcessingDocumentExisting(a);
            case "show_data_file": return AppControl.ShowDataFile(J.Str(a,"path"));
            case "charge_shift": return ChargeShift(Session(a),J.Num(a,"delta_ev"));
            case "set_background": return SetBackground(Session(a),a);
            case "clear_background": {var s=Session(a);if(s.FitState!=null)throw new Fault("MODEL_ALREADY_DEFINED","Clear the peak model before clearing its background.");if(s.BackgroundState!=null)s.BackgroundState.Dispose();s.BackgroundState=null;return J.Obj("session_id",s.Id,"cleared",true);}
            case "get_background": return GetBackground(Session(a));
            case "get_background_data": return GetBackgroundData(Session(a),a);
            case "list_sample_files": return ListSamples();
            case "open_spectrum": return OpenSpectrum(J.Str(a,"path"));
            case "list_sessions": return J.Obj("sessions",sessions.Values.Select(s=>s.Info()).ToArray());
            case "close_session": {var s=Session(a);s.Dispose();sessions.Remove(s.Id);return J.Obj("closed",true,"session_id",s.Id,"working_path",s.WorkingPath);}
            case "get_spectrum_info": return Session(a).Info();
            case "get_spectrum_data": return GetData(Session(a),a);
            case "save_spectrum": return SaveSpectrum(Session(a),J.Str(a,"path"),J.Bool(a,"overwrite"));
            case "export_spectrum": return ExportSpectrum(Session(a),J.Str(a,"path"),J.Str(a,"format","csv"),J.Bool(a,"overwrite"));
            default: return ExecuteExtended(operation,a);
        }
    }
    object Status(){
        bool tables=VerifiedBuild.ProcessingSupported();
        bool projectSave=tables&&ProjectPersistence.GridReferencesVerified;
        bool live=projectSave&&VerifiedBuild.InteractiveSupported()&&ApplicationAnchor.Enabled;
        bool solver=tables&&Hash(Path.Combine(Program.InstallRoot,"bin","PeakFitting.dll"))==NativeSolver.SupportedSha256;
        var capabilities=new List<string>{"native_vgd_read","native_vgd_save_copy","csv_json_export","charge_shift","application_status","show_data_file_request","native_backgrounds","native_peak_parameters","native_peak_constraints","native_model_curves","fit_model_json_roundtrip","model_curve_export"};
        if(tables)capabilities.AddRange(new[]{"native_vgt_export","native_vgt_inspect","native_vgt_import"});
        if(solver)capabilities.Add("native_peak_fit");
        if(tables)capabilities.Add("native_vgp_read");
        if(projectSave)capabilities.Add("native_vgp_save_model");
        if(live)capabilities.AddRange(new[]{"live_document_read","live_document_apply_existing_parameters","live_document_save_original"});
        return J.Obj("server_version","1.3.0","architecture",Environment.Is64BitProcess?"x64":"x86","avantage_home",Program.InstallRoot,"avantage_version",FileVersionInfo.GetVersionInfo(Path.Combine(Program.InstallRoot,"bin","Avantage.exe")).FileVersion,"open_sessions",sessions.Count,"application",AppControl.Status(),"backend","Avantage native COM","native_fit_solver_verified",solver,"private_table_abi_supported",tables,"native_project_save_verified",projectSave,"native_live_document_bridge_verified",live,"native_document_lifecycle_verified",live,"fit_host","one isolated native child per fit","capabilities",capabilities);
    }
    object ChargeShift(SpectrumSession s,double delta){
        if(s.FitState!=null||s.BackgroundState!=null)throw new Fault("MODEL_ALREADY_DEFINED","Apply energy calibration before defining a background or fit model, or reopen a spectrum.");
        if(s.BindingEnergy==null || s.Axes.Count!=1)throw new Fault("UNSUPPORTED_DATA","Charge shifting requires a one-dimensional energy spectrum with source energy metadata.");
        var original=new SpaceAxesClass();var modified=new SpaceAxesClass();
        try {
            s.Data.GetSpaceAxes(original);s.Data.GetSpaceAxes(modified);
            string label,symbol,unit;sbyte linear;double start,step;int size;DSAxisType type;
            modified.GetAxisInfo(0,out label,out symbol,out unit,out linear,out start,out step,out size,out type);
            if(linear!=0)modified.SetAxisInfo(0,label,symbol,unit,linear,start-delta,step,size,type);
            else for(int i=0;i<s.X.Length;i++)modified.SetAxisPoint(0,i,s.X[i]-delta);
            try{s.Data.SetSpaceAxes(modified);s.Refresh();}catch{s.Data.SetSpaceAxes(original);s.Refresh();throw;}
            s.Dirty=true;
            return J.Obj("session_id",s.Id,"binding_energy_shift_ev",delta,"kinetic_energy_shift_ev",-delta,"point_count",s.Y.Length,"dirty",true);
        }finally{J.Release(original);J.Release(modified);}
    }
    object ListSamples(){string root=Path.Combine(Program.InstallRoot,"Sample Data");return J.Obj("files",Directory.EnumerateFiles(root,"*.vgd",SearchOption.AllDirectories).Select(p=>J.Obj("path",p,"name",Path.GetFileName(p),"bytes",new FileInfo(p).Length)).ToArray());}
    object OpenSpectrum(string path){
        path=Path.GetFullPath(path);
        if(!File.Exists(path))throw new Fault("FILE_NOT_FOUND",path);
        if(!String.Equals(Path.GetExtension(path),".vgd",StringComparison.OrdinalIgnoreCase))throw new Fault("UNSUPPORTED_FORMAT","Native spectrum input must be an Avantage .VGD file.");
        if(sessions.Count>=32)throw new Fault("SESSION_LIMIT","Close an existing session before opening more than 32 spectra.");
        string id=Guid.NewGuid().ToString("N");string dir=Path.Combine(workRoot,id);Directory.CreateDirectory(dir);string copy=Path.Combine(dir,Path.GetFileName(path));
        File.Copy(path,copy,false);File.SetAttributes(copy,FileAttributes.Normal);
        var s=new SpectrumSession{Id=id,SourcePath=path,WorkingPath=copy,DirectoryPath=dir,SourceHash=Hash(path)};
        try {s.Data=new DataSpaceClass();s.Data.LoadPersistFile(copy,0,1,0);s.Refresh();sessions.Add(id,s);return s.Info();}catch{s.Dispose();throw;}
    }
    object GetData(SpectrumSession s,Dictionary<string,object>a){int offset=J.Int(a,"offset",0),limit=J.Int(a,"limit",1000);if(offset<0||offset>s.Y.Length||limit<1||limit>100000)throw new Fault("INVALID_ARGUMENT","offset must be within spectrum and limit must be 1–100000.");int count=Math.Min(limit,s.Y.Length-offset);return J.Obj("session_id",s.Id,"offset",offset,"count",count,"total",s.Y.Length,"x",s.X.Skip(offset).Take(count).ToArray(),"intensity",s.Y.Skip(offset).Take(count).ToArray(),"raw_energy_scale",s.EnergyScale,"binding_energy",s.BindingEnergy==null?null:s.BindingEnergy.Skip(offset).Take(count).ToArray(),"next_offset",offset+count<s.Y.Length?(object)(offset+count):null);}
    static string Hash(string path){using(var fs=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(fs)).Replace("-","");}
    string Target(SpectrumSession s,string path,string extension,bool overwrite){
        if(!Path.IsPathRooted(path))throw new Fault("INVALID_PATH","Output path must be absolute.");path=Path.GetFullPath(path);
        if(!String.Equals(Path.GetExtension(path),extension,StringComparison.OrdinalIgnoreCase))throw new Fault("INVALID_PATH","Output extension must be "+extension);
        if(IsProtectedProjectPath(path))throw new Fault("SOURCE_PROTECTED","Choose a separate output file; an open project source or snapshot cannot be overwritten.");
        foreach(var opened in sessions.Values)
            if(FileSafety.SameFile(path,opened.SourcePath)||FileSafety.SameFile(path,opened.WorkingPath)||(!String.IsNullOrWhiteSpace(opened.NativeSourcePath)&&FileSafety.SameFile(path,opened.NativeSourcePath)))
                throw new Fault("SOURCE_PROTECTED","Choose a separate output file; source and working files of any open session cannot be overwritten, including aliases.");
        if(File.Exists(path)&&!overwrite)throw new Fault("FILE_EXISTS","Output already exists. Choose a new name or explicitly set overwrite=true.");
        string parent=Path.GetDirectoryName(path);if(!Directory.Exists(parent))throw new Fault("DIRECTORY_NOT_FOUND",parent);return path;
    }
    static void Publish(string temp,string dest,bool overwrite){if(File.Exists(dest)){if(!overwrite)throw new Fault("FILE_EXISTS",dest);File.Replace(temp,dest,null);}else File.Move(temp,dest);}
    object SaveSpectrum(SpectrumSession s,string path,bool overwrite){
        path=Target(s,path,".vgd",overwrite);string temp=Path.Combine(Path.GetDirectoryName(path),"."+Guid.NewGuid().ToString("N")+".vgd");
        try{
            var pf=(System.Runtime.InteropServices.ComTypes.IPersistFile)s.Data;pf.Save(temp,false);pf.SaveCompleted(null);
            string savedHash=Hash(temp);object sourceUnchanged=null;string sourceCheckError=null;
            try{sourceUnchanged=Hash(s.SourcePath)==s.SourceHash;}catch(IOException e){sourceCheckError=e.Message;}catch(UnauthorizedAccessException e){sourceCheckError=e.Message;}
            Publish(temp,path,overwrite);
            return J.Obj("path",path,"sha256",savedHash,"source_unchanged",sourceUnchanged,"source_check_error",sourceCheckError);
        }finally{if(File.Exists(temp))File.Delete(temp);}
    }
    object ExportSpectrum(SpectrumSession s,string path,string format,bool overwrite){if(format!="csv"&&format!="json")throw new Fault("INVALID_ARGUMENT","format must be csv or json.");path=Target(s,path,"."+format,overwrite);string temp=Path.Combine(Path.GetDirectoryName(path),"."+Guid.NewGuid().ToString("N")+".tmp");try{if(format=="json"){var json=new JavaScriptSerializer{MaxJsonLength=64*1024*1024};File.WriteAllText(temp,json.Serialize(J.Obj("metadata",s.Info(),"x",s.X,"intensity",s.Y,"binding_energy",s.BindingEnergy)),new UTF8Encoding(false));}else{using(var w=new StreamWriter(temp,false,new UTF8Encoding(true))){w.WriteLine("raw_axis,kinetic_energy_eV,binding_energy_eV,intensity");for(int i=0;i<s.Y.Length;i++)w.WriteLine(string.Join(",",new[]{s.X[i].ToString("R",CultureInfo.InvariantCulture),s.EnergyScale=="kinetic"?s.X[i].ToString("R",CultureInfo.InvariantCulture):"",s.BindingEnergy==null?"":s.BindingEnergy[i].ToString("R",CultureInfo.InvariantCulture),s.Y[i].ToString("R",CultureInfo.InvariantCulture)}));}}Publish(temp,path,overwrite);return J.Obj("path",path,"format",format,"point_count",s.Y.Length);}finally{if(File.Exists(temp))File.Delete(temp);}}
    public void Dispose(){foreach(var s in sessions.Values)s.Dispose();sessions.Clear();if(documents!=null)documents.Dispose();DisposeProjects();}
    NativePeakModel Model(SpectrumSession s,bool create=false) {
        if(s.FitState==null&&create) {
            if(s.BindingEnergy==null||s.Axes.Count!=1)throw new Fault("UNSUPPORTED_DATA","Peak models require a one-dimensional energy spectrum.");
            s.FitState=new NativePeakModel(s.Data);
        }
        var model=s.FitState as NativePeakModel;
        if(model==null)throw new Fault("MODEL_NOT_DEFINED","Add a peak first.");
        return model;
    }
    static Dictionary<string,object> ArgumentObject(Dictionary<string,object>a,string key) {
        object value;Dictionary<string,object> result;
        if(!a.TryGetValue(key,out value)||(result=value as Dictionary<string,object>)==null)
            throw new Fault("INVALID_ARGUMENT",key+" must be an object.");
        return result;
    }
    object ExecuteExtended(string operation,Dictionary<string,object>a){
        if(operation=="run_fit")return RunFitIsolated(Session(a),a);
        if(operation=="export_peak_table")return ExportPeakTable(Session(a),J.Str(a,"path"),J.Bool(a,"overwrite"));
        if(operation=="inspect_peak_table")return InspectPeakTable(Session(a),J.Str(a,"path"));
        if(operation=="import_peak_table")return ImportPeakTable(Session(a),J.Str(a,"path"));
        if(operation=="get_model_curve")return GetModelCurve(Session(a),a);
        if(operation=="export_model_curves")return ExportModelCurves(Session(a),a);
        if(operation=="export_fit_model")return ExportFitModel(Session(a),J.Str(a,"path"),J.Bool(a,"overwrite"));
        if(operation=="import_fit_model")return ImportFitModel(Session(a),J.Str(a,"path"),J.Bool(a,"allow_different_spectrum"));
        if(operation=="add_peak") {
            var s=Session(a);bool created=s.FitState==null;var m=Model(s,true);
            try {
                if(m.Peaks.Count>=100)throw new Fault("MODEL_SIZE","A model may contain at most 100 peaks.");var peak=m.Add(ArgumentObject(a,"peak"));
                if(s.BackgroundState!=null)m.AttachBackground(s.BackgroundState.Background,typeof(IAvantageBackground));
                return J.Obj("session_id",s.Id,"peak",peak);
            }catch{if(created){m.Dispose();s.FitState=null;}throw;}
        }
        if(operation=="update_peak") {var s=Session(a);return J.Obj("session_id",s.Id,"peak",Model(s).Update(J.Str(a,"peak_id"),ArgumentObject(a,"patch")));}
        if(operation=="remove_peak") {var s=Session(a);return J.Obj("session_id",s.Id,"result",Model(s).Remove(J.Str(a,"peak_id")));}
        if(operation=="get_fit_model") {var s=Session(a);return J.Obj("session_id",s.Id,"background",s.BackgroundState==null?null:s.BackgroundState.Info(),"model",s.FitState==null?null:Model(s).Info());}
        if(operation=="clear_fit_model") {var s=Session(a);if(s.FitState is IDisposable)((IDisposable)s.FitState).Dispose();s.FitState=null;return J.Obj("session_id",s.Id,"cleared",true,"background_retained",s.BackgroundState!=null);}
        if(operation=="set_peak_constraint") {var s=Session(a);return J.Obj("session_id",s.Id,"peak",Model(s).Constraint(J.Str(a,"peak_id"),J.Str(a,"attribute"),ArgumentObject(a,"constraint")));}
        if(operation=="remove_peak_constraint") {var s=Session(a);return J.Obj("session_id",s.Id,"peak",Model(s).RemoveConstraint(J.Str(a,"peak_id"),J.Str(a,"attribute")));}
        throw new Fault("UNKNOWN_OPERATION","Unknown operation: "+operation);
    }
}
}
