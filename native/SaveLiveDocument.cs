
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Linq;
namespace AvantageMcp {
public sealed partial class Engine {
    static bool SameDocumentSet(Dictionary<string,object> before,Dictionary<string,object> after){
        Func<Dictionary<string,object>,string[]> keys = value => ((System.Collections.IEnumerable)value["documents"]).Cast<Dictionary<string,object>>().Select(d=>J.Str(d,"document_id")+"|"+J.Str(d,"path")).OrderBy(k=>k).ToArray();
        return keys(before).SequenceEqual(keys(after));
    }
    static void VerifyCompleteSnapshot(NativeProcessingProject project,IList<Dictionary<string,object>> expected){
        if(project.Spectra.Count!=expected.Count)throw new Fault("SAVE_NOT_VERIFIED","Saved spectrum count differs.");
        for(int i=0;i<expected.Count;i++){
            var peaks=project.Document.PeaksFor(project.Spectra[i].Data);
            try{if(!NativePeakTablePersistence.Equivalent(expected[i]["data"],NativeSnapshot.Fingerprint(project.Spectra[i].Data))||!NativePeakTablePersistence.PersistedEquivalent(expected[i]["model"],NativePeakTablePersistence.ReadPeaks(peaks)))throw new Fault("SAVE_NOT_VERIFIED","Saved raw/model differs at spectrum "+i);}
            finally{ProcessingCom.ReleasePeaks(peaks);}
        }
    }
    [DllImport("user32.dll",SetLastError=true)]static extern bool PostMessage(IntPtr window,uint message,IntPtr wparam,IntPtr lparam);
    object SaveProcessingDocumentExisting(Dictionary<string,object> args){
        var doc=Documents.Find(J.Str(args,"document_id"));
        string fixture=doc.PathName, requested=System.IO.Path.GetFullPath(J.Str(args,"path"));
        if(String.IsNullOrWhiteSpace(fixture)||!System.IO.File.Exists(fixture)||!String.Equals(System.IO.Path.GetFullPath(fixture),requested,StringComparison.OrdinalIgnoreCase))throw new Fault("ORIGINAL_PATH_REQUIRED","Save only this existing document's own original path. No command sent.");
        var initialDocuments=(Dictionary<string,object>)Documents.List();
        Documents.Activate(doc);
        var processes=Process.GetProcessesByName("Avantage");
        IntPtr hwnd=IntPtr.Zero;int appId;
        try{if(processes.Length!=1||!processes[0].Responding)throw new Fault("APP_NOT_READY","One responding application is required.");hwnd=processes[0].MainWindowHandle;appId=processes[0].Id;}finally{foreach(var p in processes)p.Dispose();}
        if(hwnd==IntPtr.Zero)throw new Fault("NO_MAIN_WINDOW","No application command was sent.");
        var data=ProcessingCom.EnumerateData(doc.Pointer);
        try{
            var before=CaptureLiveDocument(doc,data);foreach(var pointer in data)Marshal.Release(pointer);data.Clear();
            int initialModified;Marshal.ThrowExceptionForHR(ProcessingCom.At<ProcessingCom.GetInt>(doc.Pointer,9)(doc.Pointer,out initialModified));
            // Asynchronous normal File > Save. Never call IProcessingDocument.SaveDocument:
            // that route clears the MFC path and takes a different storage lifecycle.
            // No UI activation, keyboard injection, nested SendMessage or automatic retry.
            // An already saved document needs full disk verification, not another
            // command. A dirty-to-clean transition is valid even if serialization
            // produces identical bytes (e.g. a user reverted their last edit).
            if(initialModified!=0&&!PostMessage(hwnd,0x111,new IntPtr(0xE103),IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error());
            var timer=Stopwatch.StartNew();bool completed=initialModified==0;
            while(!completed&&timer.ElapsedMilliseconds<30000){
                Thread.Sleep(150);
                if(!String.Equals(doc.PathName,fixture,StringComparison.OrdinalIgnoreCase))throw new Fault("SAVE_PATH_CHANGED","The document changed path; inspect the test window before retrying.");
                int modified;Marshal.ThrowExceptionForHR(ProcessingCom.At<ProcessingCom.GetInt>(doc.Pointer,9)(doc.Pointer,out modified));
                if(modified==0){completed=true;break;}
            }
            if(!completed)throw new Fault("MENU_SAVE_TIMEOUT","Ordinary Save did not verify within 30 seconds. No retry was sent.");
            data=ProcessingCom.EnumerateData(doc.Pointer);var after=CaptureLiveDocument(doc,data);
            if(!NativePeakTablePersistence.PersistedEquivalent(before,after))throw new Fault("LIVE_MODEL_CHANGED_DURING_SAVE","Live data/model changed during ordinary Save.");
            string privateId=null;
            try{
                var opened=(Dictionary<string,object>)OpenProcessingProject(fixture);privateId=J.Str(opened,"project_id");var project=ProcessingProject(privateId);
                VerifyCompleteSnapshot(project,before);VerifyGridVisibility(ProjectGridValidation.Capture(project.WorkingPath),before);
            }finally{if(privateId!=null)CloseProcessingProject(privateId);}
            if(!SameDocumentSet(initialDocuments,(Dictionary<string,object>)Documents.List()))throw new Fault("DOCUMENT_SET_CHANGED","The open document set or modified state changed unexpectedly during Save.");
            InvalidateLiveLinks(doc.Id);
            return J.Obj("document",doc.Info(),"roundtrip_verified",true,"raw_data_verified",true,"all_spectrum_models_verified",true,"grid_bindings_verified",true,"no_extra_windows_verified",true,"same_window_retained",true,"saved_state_verified",true,"live_readback_verified",true,"normal_menu_save_sent",initialModified!=0,"already_saved",initialModified==0,"application_process_id",appId,"saved_file_sha256",ProjectPersistence.HashShared(fixture),"note","Complete saved project raw/model/grid readback verified in an isolated project snapshot.");
        }finally{foreach(var p in data)Marshal.Release(p);}
    }
}
}
