using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using VGDATASTORELib;

namespace AvantageMcp {
// Registered interface order verified against the installed NDR proxies and native vtables.
// Never pass worker-owned peak COM objects into the application: exchange native VGT files instead.
public static class ProcessingCom {
    public const string UiId="A7C48246-6C2F-11D3-BD10-00C04F8EBEF8",DocId="6E55B71A-944B-11D4-866C-00C04F68D1AC";
    [DllImport("ole32.dll")]static extern int CoCreateInstance(ref Guid cls,IntPtr outer,uint context,ref Guid iid,out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]public delegate int GetPointer(IntPtr self,out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]public delegate int NoArguments(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]public delegate int GetInt(IntPtr self,out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]public delegate int SetInt(IntPtr self,int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]public delegate int SetPointer(IntPtr self,IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]public delegate int TextArgument(IntPtr self,[MarshalAs(UnmanagedType.BStr)]string value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]public delegate int GetPeaks(IntPtr self,IntPtr data,out IntPtr array,out int count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]public delegate void FindVisual(IntPtr self,IntPtr data,out IntPtr visual);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]public delegate int RemoveDataPeaks(IntPtr self,int count,IntPtr dataArray);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]public delegate int LoadSelected(IntPtr self,long eventHandle,int count,IntPtr dataArray,IntPtr indexArray);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]public delegate int DeferredDocumentText(IntPtr self,long eventHandle,IntPtr document,[MarshalAs(UnmanagedType.BStr)]string path);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]public delegate int DeferredText(IntPtr self,long eventHandle,[MarshalAs(UnmanagedType.BStr)]string path);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]public delegate int DeferredEvent(IntPtr self,long eventHandle);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]public delegate void ReadResult(IntPtr self,out int result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]public delegate void VoidText(IntPtr self,[MarshalAs(UnmanagedType.BStr)]string text);
    public static void CheckDeferred(IntPtr grid){int result;At<ReadResult>(grid,51)(grid,out result);Marshal.ThrowExceptionForHR(result);}
    public static T At<T>(IntPtr pointer,int slot) where T:class {return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(pointer),slot*8),typeof(T))as T;}
    public static IntPtr Query(IntPtr value,string id){Guid iid=new Guid(id);IntPtr result;Marshal.ThrowExceptionForHR(Marshal.QueryInterface(value,ref iid,out result));return result;}
    public static IntPtr TryQuery(IntPtr value,string id){Guid iid=new Guid(id);IntPtr result;int hr=Marshal.QueryInterface(value,ref iid,out result);if(hr==unchecked((int)0x80004002))return IntPtr.Zero;Marshal.ThrowExceptionForHR(hr);return result;}
    public static string Text(IntPtr value,int slot){IntPtr text;Marshal.ThrowExceptionForHR(At<GetPointer>(value,slot)(value,out text));try{return text==IntPtr.Zero?"":Marshal.PtrToStringBSTR(text);}finally{if(text!=IntPtr.Zero)Marshal.FreeBSTR(text);}}
    public sealed class Connection : IDisposable {
        public IntPtr Ui;object application;
        public Connection(){
            NativeGridTables.VerifiedModule.ToInt64();
            string bin=Path.Combine(Program.InstallRoot,"bin");
            VerifyFile(Path.Combine(bin,"Avantage.exe"),"6985232B5D77D3493F272F9A482FB0159890F0F13D55DF29D43FEF319FF1C4AE");
            VerifyFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"mfc140u.dll"),"FC9B1F94752D7080436824B480E83BA1EC930E3F7BAA881F4E842B882E715CFC");
            foreach(var pair in new[]{new[]{"ProcessingManager.dll","7D9AA9623E818BCE0794030CD108CD20371CCB2D8AB686DFDADC54085B0E61A9"},new[]{"ProcessingInterfacesps.dll","35CB5A0E55CCE5584ADA0BD745CD3108087C1B4F7A5FFF3D7729F801DFF83D17"},new[]{"UserInterfaceIFps.dll","91CF9CB21F209F8BAFA3FD08557384CC68F01AEF5A3AEB1F59E4E3DE5A823D4C"}}){using(var sha=SHA256.Create())using(var file=File.OpenRead(Path.Combine(bin,pair[0])))if(BitConverter.ToString(sha.ComputeHash(file)).Replace("-","")!=pair[1])throw new Fault("UNSUPPORTED_BUILD","Interactive processing interface disabled for this "+pair[0]+" build.");}
            var running=System.Diagnostics.Process.GetProcessesByName("Avantage");
            try{if(running.Length==0)throw new Fault("APP_NOT_RUNNING","Start Avantage and open a processing document first.");if(running.Length!=1)throw new Fault("APP_SESSION_AMBIGUOUS","Keep exactly one Avantage application open.");if(!running[0].Responding)throw new Fault("APP_NOT_RESPONDING","Avantage is not responding.");}finally{foreach(var p in running)p.Dispose();}
            Guid cls=new Guid("052CC3C7-B7E2-40C3-92F5-3FA00FA81C1D"),iid=new Guid("00020400-0000-0000-C000-000000000046");IntPtr app=IntPtr.Zero;
            try{Marshal.ThrowExceptionForHR(CoCreateInstance(ref cls,IntPtr.Zero,4,ref iid,out app));
                // Verified vendor factory defect: COleSingleObjectFactory returns
                // the existing CMainFrame without AddRef. MFC CreateInstance QIs
                // then releases the presumed initial reference, consuming the GUI
                // owner's reference. Restore that one reference for EACH activation.
                // It is deliberately retained: releasing it would reproduce the
                // factory's missing reference and destroy the interactive window.
                Marshal.AddRef(app);
                application=Marshal.GetObjectForIUnknown(app);dynamic automation=application;
                // This server exposes interactive processing, so return control to
                // the user immediately after the vendor automation activation.
                // The same factory sets RemoteMode=true and ManualMode=false.
                // Return the normally opened processing application to interactive
                // mode, avoiding redundant setters once its flags are restored.
                if(!(bool)automation.ManualMode)automation.ManualMode=true;
                if((bool)automation.RemoteMode)automation.RemoteMode=false;
                if((bool)automation.RemoteMode)throw new Fault("INTERACTIVE_MODE_NOT_RESTORED","Avantage did not restore interactive processing mode.");
                Ui=Query(app,UiId);
            }catch{Dispose();throw;}finally{if(app!=IntPtr.Zero)Marshal.Release(app);}
        }
        static void VerifyFile(string path,string expected){using(var sha=SHA256.Create())using(var file=File.OpenRead(path))if(BitConverter.ToString(sha.ComputeHash(file)).Replace("-","")!=expected)throw new Fault("UNSUPPORTED_BUILD","Interactive connection is disabled for this native lifecycle build: "+Path.GetFileName(path));}
        public void Dispose(){if(Ui!=IntPtr.Zero){Marshal.Release(Ui);Ui=IntPtr.Zero;}if(application!=null){try{dynamic app=application;app.RemoteMode=false;}catch(COMException){}finally{Marshal.ReleaseComObject(application);application=null;}}}
    }
    public static List<IntPtr> EnumerateData(IntPtr doc){IntPtr en;Marshal.ThrowExceptionForHR(At<GetPointer>(doc,18)(doc,out en));var result=new List<IntPtr>();try{Marshal.ThrowExceptionForHR(At<NoArguments>(en,4)(en));for(int i=0;i<10000;i++){IntPtr data;int hr=At<GetPointer>(en,3)(en,out data);Marshal.ThrowExceptionForHR(hr);if(data==IntPtr.Zero)return result;result.Add(data);}throw new Fault("DOCUMENT_SIZE","Too many document spectra.");}catch{foreach(var data in result)Marshal.Release(data);throw;}finally{Marshal.Release(en);}}
    public static List<IAvFittedPeak> Peaks(IntPtr doc,IntPtr data){IntPtr array;int count;Marshal.ThrowExceptionForHR(At<GetPeaks>(doc,16)(doc,data,out array,out count));var result=new List<IAvFittedPeak>();try{if(count<0||count>10000)throw new Fault("MODEL_SIZE","Unexpected native peak count.");for(int i=0;i<count;i++){IntPtr peak=Marshal.ReadIntPtr(array,i*8);try{result.Add((IAvFittedPeak)Marshal.GetObjectForIUnknown(peak));}finally{Marshal.Release(peak);}}return result;}catch{foreach(var p in result)Marshal.ReleaseComObject(p);throw;}finally{if(array!=IntPtr.Zero)Marshal.FreeCoTaskMem(array);}}
    public static void ReleasePeaks(IEnumerable<IAvFittedPeak> peaks){foreach(var p in peaks)Marshal.ReleaseComObject(p);}
}
public sealed class LiveSpectrumLink : IDisposable {
    public string DocumentId,DataIdentity,OriginalFingerprint,OriginalModelFingerprint;public int Index;public bool Applied,Stale;
    public List<string> OriginalPeakIds=new List<string>();
    public IntPtr DataAnchor;
    public void Dispose(){if(DataAnchor!=IntPtr.Zero){Marshal.Release(DataAnchor);DataAnchor=IntPtr.Zero;}}
    public Dictionary<string,object> Info(){return J.Obj("document_id",DocumentId,"spectrum_index",Index,"applied",Applied,"stale",Stale);}
}
public sealed class ProcessingDocument : IDisposable {
    public string Id;public IntPtr Pointer;public string PathName {get{return ProcessingCom.Text(Pointer,14);}}
    public IntPtr Grid(){IntPtr p;Marshal.ThrowExceptionForHR(ProcessingCom.At<ProcessingCom.GetPointer>(Pointer,4)(Pointer,out p));return p;}
    public Dictionary<string,object> Info(){int modified;Marshal.ThrowExceptionForHR(ProcessingCom.At<ProcessingCom.GetInt>(Pointer,9)(Pointer,out modified));return J.Obj("document_id",Id,"title",ProcessingCom.Text(Pointer,11),"path",PathName,"modified",modified!=0);}
    public void Dispose(){if(Pointer!=IntPtr.Zero){Marshal.Release(Pointer);Pointer=IntPtr.Zero;}}
}
public sealed class ProcessingDocuments : IDisposable {
    ProcessingCom.Connection connection;
    IntPtr Ui {get{return (connection??(connection=new ProcessingCom.Connection())).Ui;}}
    readonly Dictionary<long,ProcessingDocument> docs=new Dictionary<long,ProcessingDocument>();
    public void Refresh(){IntPtr ui=Ui,en=IntPtr.Zero;var seen=new HashSet<long>();try{Marshal.ThrowExceptionForHR(ProcessingCom.At<ProcessingCom.GetPointer>(ui,12)(ui,out en));Marshal.ThrowExceptionForHR(ProcessingCom.At<ProcessingCom.NoArguments>(en,3)(en));for(int i=0;i<128;i++){IntPtr unknown;Marshal.ThrowExceptionForHR(ProcessingCom.At<ProcessingCom.GetPointer>(en,4)(en,out unknown));if(unknown==IntPtr.Zero)break;try{IntPtr p=ProcessingCom.TryQuery(unknown,ProcessingCom.DocId);if(p==IntPtr.Zero)continue;long identity=p.ToInt64();seen.Add(identity);if(docs.ContainsKey(identity))Marshal.Release(p);else docs.Add(identity,new ProcessingDocument{Id=Guid.NewGuid().ToString("N"),Pointer=p});}finally{Marshal.Release(unknown);}}foreach(long stale in docs.Keys.Where(k=>!seen.Contains(k)).ToArray()){docs[stale].Dispose();docs.Remove(stale);}}finally{if(en!=IntPtr.Zero)Marshal.Release(en);}}
    public object List(){Refresh();return J.Obj("documents",docs.Values.Select(d=>d.Info()).ToArray());}
    public ProcessingDocument FindPath(string path){Refresh();var document=docs.Values.FirstOrDefault(d=>String.Equals(d.PathName,path,StringComparison.OrdinalIgnoreCase));if(document==null)throw new Fault("LIVE_DISPLAY_NOT_VERIFIED","Avantage has not loaded the saved project: "+path);return document;}
    public ProcessingDocument Find(string id){Refresh();var doc=docs.Values.FirstOrDefault(d=>d.Id==id);if(doc==null)throw new Fault("DOCUMENT_NOT_FOUND","The document is no longer open, or its id belongs to another worker.");return doc;}
    public object Spectra(string id){var doc=Find(id);IntPtr grid=doc.Grid();var data=ProcessingCom.EnumerateData(doc.Pointer);var rows=new List<object>();try{for(int i=0;i<data.Count;i++){var ds=(IDataSpace)Marshal.GetObjectForIUnknown(data[i]);var axes=new DataAxesClass();try{ds.GetDataAxes(axes);IntPtr visual;ProcessingCom.At<ProcessingCom.FindVisual>(grid,38)(grid,data[i],out visual);bool visible=visual!=IntPtr.Zero;if(visual!=IntPtr.Zero)Marshal.Release(visual);var peaks=ProcessingCom.Peaks(doc.Pointer,data[i]);try{string source=Convert.ToString(ds.GetProperty(DSPropSet.STDPROPSET,DSPropID.DS_GEPROPID_SOURCE_FILE));rows.Add(J.Obj("spectrum_index",i,"spectrum_id",data[i].ToInt64().ToString("X"),"name",System.IO.Path.GetFileNameWithoutExtension(source),"source_path",source,"point_count",axes.GetSize(),"visible_in_grid",visible,"fitted_peak_count",peaks.Count));}finally{ProcessingCom.ReleasePeaks(peaks);}}finally{Marshal.ReleaseComObject(ds);J.Release(axes);}}return J.Obj("document",doc.Info(),"spectra",rows,"spectrum_count",data.Count);}finally{foreach(var p in data)Marshal.Release(p);Marshal.Release(grid);}}
    public object Open(string path){path=System.IO.Path.GetFullPath(path);if(!File.Exists(path)||(!String.Equals(System.IO.Path.GetExtension(path),".vgp",StringComparison.OrdinalIgnoreCase)&&!String.Equals(System.IO.Path.GetExtension(path),".vgd",StringComparison.OrdinalIgnoreCase)))throw new Fault("INVALID_PATH","Expected an existing .VGP or .VGD file.");Refresh();var existing=docs.Values.FirstOrDefault(d=>String.Equals(d.PathName,path,StringComparison.OrdinalIgnoreCase));if(existing!=null)return J.Obj("document",existing.Info(),"already_open",true);IntPtr ui=Ui;{Marshal.ThrowExceptionForHR(ProcessingCom.At<ProcessingCom.TextArgument>(ui,23)(ui,path));}Refresh();var opened=docs.Values.FirstOrDefault(d=>String.Equals(d.PathName,path,StringComparison.OrdinalIgnoreCase));if(opened==null)throw new Fault("OPEN_NOT_VERIFIED","Avantage did not expose the requested document.");var data=ProcessingCom.EnumerateData(opened.Pointer);try{if(data.Count==0)throw new Fault("EMPTY_DOCUMENT","Avantage opened the VGP without spectra. Its companion data or saved grid may be missing; use the original location or an already-open document.");}finally{foreach(var p in data)Marshal.Release(p);}return J.Obj("document",opened.Info(),"already_open",false);}
    public object SaveCopy(string id,string path){Engine.Trace("savecopy.find.begin");var doc=Find(id);Engine.Trace("savecopy.find.done");path=System.IO.Path.GetFullPath(path);if((!String.Equals(System.IO.Path.GetExtension(path),".vgp",StringComparison.OrdinalIgnoreCase)&&!String.Equals(System.IO.Path.GetExtension(path),".vgd",StringComparison.OrdinalIgnoreCase))||File.Exists(path)||Directory.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path),System.IO.Path.GetFileNameWithoutExtension(path)+".DATA")))throw new Fault("INVALID_PATH","Choose a new .VGP filename with no existing companion .DATA directory.");if(!Directory.Exists(System.IO.Path.GetDirectoryName(path)))throw new Fault("DIRECTORY_NOT_FOUND","Create the output directory first.");Marshal.ThrowExceptionForHR(ProcessingCom.At<ProcessingCom.SetInt>(doc.Pointer,8)(doc.Pointer,1));Engine.Trace("savecopy.modified");IntPtr grid=doc.Grid(),unknown=ProcessingCom.Query(doc.Pointer,"00000000-0000-0000-C000-000000000046");try{Engine.Trace("savecopy.invoke");Marshal.ThrowExceptionForHR(ProcessingCom.At<ProcessingCom.DeferredDocumentText>(grid,58)(grid,0,unknown,path));Engine.Trace("savecopy.returned");ProcessingCom.CheckDeferred(grid);}finally{Marshal.Release(unknown);Marshal.Release(grid);}if(!File.Exists(path)||new FileInfo(path).Length==0)throw new Fault("SAVE_NOT_VERIFIED","Avantage did not save the document.");return J.Obj("document",doc.Info(),"path",path,"bytes",new FileInfo(path).Length,"companion_data_directory",System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path),System.IO.Path.GetFileNameWithoutExtension(path)+".DATA"),"note","Avantage Save As changes the open document's path to this copy.");}
    public void Dispose(){foreach(var d in docs.Values)d.Dispose();docs.Clear();if(connection!=null){connection.Dispose();connection=null;}}
}
public sealed partial class Engine {
    ProcessingDocuments documents;ProcessingDocuments Documents{get{return documents??(documents=new ProcessingDocuments());}}
    static string ObjectFingerprint(object value){var json=new JavaScriptSerializer{MaxJsonLength=16*1024*1024};using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(json.Serialize(NativePeakTablePersistence.PersistedModelParameters(value))))).Replace("-","");}
    object AttachDocumentSpectrum(Dictionary<string,object> args){if(sessions.Count>=32)throw new Fault("SESSION_LIMIT","Close a session first.");var doc=Documents.Find(J.Str(args,"document_id"));int index=J.Int(args,"spectrum_index",-1);var all=ProcessingCom.EnumerateData(doc.Pointer);SpectrumSession session=null;try{if(index<0||index>=all.Count)throw new Fault("SPECTRUM_NOT_FOUND","Choose an index returned by list_document_spectra.");IntPtr raw=all[index];var data=(IDataSpace)Marshal.GetObjectForIUnknown(raw);try{string id=Guid.NewGuid().ToString("N"),dir=Path.Combine(workRoot,id);Directory.CreateDirectory(dir);string copy=Path.Combine(dir,"spectrum.vgd");NativeSnapshot.CopyDataSpaceToFile(data,copy);session=new SpectrumSession{Id=id,DirectoryPath=dir,WorkingPath=copy,SourcePath=doc.PathName,NativeSourcePath=Convert.ToString(data.GetProperty(DSPropSet.STDPROPSET,DSPropID.DS_GEPROPID_SOURCE_FILE)),SourceHash=String.IsNullOrWhiteSpace(doc.PathName)||!File.Exists(doc.PathName)?null:ProjectPersistence.HashShared(doc.PathName),Data=new DataSpaceClass()};((DataSpaceClass)session.Data).LoadPersistFile(copy,0,1,0);session.Refresh();var peaks=ProcessingCom.Peaks(doc.Pointer,raw);try{var snapshots=NativePeakTablePersistence.ReadPeaks(peaks);ImportNativeSnapshots(session,snapshots);session.LiveLink=new LiveSpectrumLink{DocumentId=doc.Id,Index=index,DataAnchor=ProcessingCom.Query(raw,"00000000-0000-0000-C000-000000000046"),DataIdentity=raw.ToInt64().ToString("X"),OriginalFingerprint=NativeSnapshot.Fingerprint(session.Data),OriginalModelFingerprint=ObjectFingerprint(snapshots)};if(session.FitState is NativePeakModel)session.LiveLink.OriginalPeakIds.AddRange(((NativePeakModel)session.FitState).Peaks.Select(p=>p.Id));}finally{ProcessingCom.ReleasePeaks(peaks);}sessions.Add(id,session);return J.Obj("session_id",session.Id,"session",session.Info(),"live_document",session.LiveLink.Info(),"model",session.FitState==null?null:Model(session).Info(),"background",session.BackgroundState==null?null:session.BackgroundState.Info());}finally{Marshal.ReleaseComObject(data);}}catch{if(session!=null)session.Dispose();throw;}finally{foreach(var p in all)Marshal.Release(p);}}
    object ApplyFitToDocument(SpectrumSession s){return ApplyLiveModel(s);}
}
}
