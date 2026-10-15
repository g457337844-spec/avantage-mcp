using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using VGDATASTORELib;

namespace AvantageMcp {
public sealed partial class Engine {
    // Capture the current app document, persist in a privately owned native graph,
    // then reload the complete verified result through Avantage's UI dispatcher.
    object ApplyLiveModel(SpectrumSession session) {
        if(session.LiveLink==null)throw new Fault("LIVE_SESSION_REQUIRED","Attach an open document spectrum first.");
        if(session.LiveLink.Stale)throw new Fault("LIVE_DOCUMENT_CHANGED","The document was saved or reloaded. Reattach its current spectrum.");
        var link=session.LiveLink;var model=Model(session,true);
        if(NativeSnapshot.Fingerprint(session.Data)!=link.OriginalFingerprint)
            throw new Fault("LIVE_DATA_CHANGED","Live model writeback requires unchanged raw data and calibration.");
        var document=Documents.Find(link.DocumentId);var data=ProcessingCom.EnumerateData(document.Pointer);
        int spectrumCount=data.Count;
        NativeProcessingProject snapshot=null;
        string checkpoint=null,saved=null;bool displayRequested=false;
        string originalProject=session.SourceProjectId;int originalIndex=session.ProjectSpectrumIndex;
        var originalIds=session.ProjectOriginalPeakIds;
        try {
            if(link.Index<0||link.Index>=data.Count)throw new Fault("LIVE_DOCUMENT_CHANGED","The target spectrum is no longer present.");
            IntPtr identity=ProcessingCom.Query(data[link.Index],"00000000-0000-0000-C000-000000000046");
            try{if(identity!=link.DataAnchor)throw new Fault("LIVE_DOCUMENT_CHANGED","The target spectrum was replaced; reattach it.");}
            finally{Marshal.Release(identity);}
            var before=CaptureLiveDocument(document,data);
            if((string)before[link.Index]["data"]!=link.OriginalFingerprint||ObjectFingerprint(before[link.Index]["model"])!=link.OriginalModelFingerprint)
                throw new Fault("LIVE_MODEL_CHANGED","The Avantage model changed after attachment. Reattach before applying edits.");
            string directory=Path.Combine(session.DirectoryPath,"apply-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            checkpoint=Path.Combine(directory,"checkpoint.vgp");saved=Path.Combine(directory,"applied.vgp");
            // Save As replaces application data spaces. Do not pin obsolete
            // remote data objects across that native lifetime transition.
            foreach(var pointer in data)Marshal.Release(pointer);data.Clear();
            InvalidateLiveLinks(document.Id);
            Trace("live.checkpoint.begin");Documents.SaveCopy(document.Id,checkpoint);Trace("live.checkpoint.saved");
            // The app may keep its saved storage writable. Snapshot it using shared
            // readers before loading any exclusively owned native document.
            var opened=(Dictionary<string,object>)OpenProcessingProject(checkpoint);
            snapshot=ProcessingProject((string)opened["project_id"]);
            VerifyGridVisibility(ProjectGridValidation.Capture(snapshot.WorkingPath),before);
            Trace("live.checkpoint.loaded");
            if(snapshot.Spectra.Count!=spectrumCount)throw new Fault("LIVE_CHECKPOINT_MISMATCH","The checkpoint spectrum count changed.");
            for(int i=0;i<snapshot.Spectra.Count;i++) {
                var peaks=snapshot.Document.PeaksFor(snapshot.Spectra[i].Data);
                try {
                    var savedModel=NativePeakTablePersistence.ReadPeaks(peaks);string savedData=NativeSnapshot.Fingerprint(snapshot.Spectra[i].Data);
                    if(!NativePeakTablePersistence.Equivalent(before[i]["data"],savedData)||!NativePeakTablePersistence.PersistedEquivalent(before[i]["model"],savedModel)) {
                        File.WriteAllText(Path.Combine(directory,"checkpoint-difference.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(J.Obj("index",i,"live",before[i],"saved_data",savedData,"saved_model",savedModel)));
                        throw new Fault("LIVE_CHECKPOINT_MISMATCH","The checkpoint did not preserve spectrum "+i+" and its model.");
                    }
                }finally{ProcessingCom.ReleasePeaks(peaks);}
            }
            session.SourceProjectId=snapshot.Id;session.ProjectSpectrumIndex=link.Index;
            session.ProjectOriginalPeakIds=new List<string>(link.OriginalPeakIds);
            var persisted=(Dictionary<string,object>)SaveProjectModel(session,saved);
            Trace("live.result.saved");
            var pendingData=ProcessingCom.EnumerateData(document.Pointer);
            try{if(!NativePeakTablePersistence.PersistedEquivalent(before,CaptureLiveDocument(document,pendingData)))
                throw new Fault("LIVE_MODEL_CHANGED","The Avantage document changed while the private result was prepared. Reattach to preserve newer edits.");}
            finally{foreach(var pointer in pendingData)Marshal.Release(pointer);}
            displayRequested=true;
            // DeferredOpenDocument reuses an existing view whose data-space
            // objects can retain the previous storage paths. Load through the
            // application's normal document/template entry point instead.
            Documents.Open(saved);
            Trace("live.result.displayed");
            // Some builds open into a new processing document; find the actual
            // saved-path owner rather than assuming the old pointer was reused.
            document=Documents.FindPath(saved);
            var updated=ProcessingCom.EnumerateData(document.Pointer);
            try {
                if(updated.Count!=spectrumCount)throw new Fault("LIVE_DISPLAY_NOT_VERIFIED","Avantage did not load the entire saved project: "+saved);
                var actual=CaptureLiveDocument(document,updated);
                var expected=NativePeakTablePersistence.ReadPeaks(model.Peaks.Select(p=>p.Native).ToArray());
                for(int i=0;i<actual.Count;i++)
                    if(!NativePeakTablePersistence.Equivalent(before[i]["data"],actual[i]["data"])||
                       !Equals(before[i]["visible_in_grid"],actual[i]["visible_in_grid"])||
                       !NativePeakTablePersistence.PersistedEquivalent(i==link.Index?(object)expected:before[i]["model"],actual[i]["model"]))
                        throw new Fault("LIVE_DISPLAY_NOT_VERIFIED","Avantage readback differs from the verified saved result: "+saved);
                if(link.DataAnchor!=IntPtr.Zero)Marshal.Release(link.DataAnchor);
                link.DataAnchor=ProcessingCom.Query(updated[link.Index],"00000000-0000-0000-C000-000000000046");
                link.DataIdentity=updated[link.Index].ToInt64().ToString("X");link.DocumentId=document.Id;
                link.OriginalModelFingerprint=ObjectFingerprint(actual[link.Index]["model"]);
                link.OriginalPeakIds.Clear();link.OriginalPeakIds.AddRange(model.Peaks.Select(p=>p.Id));
                link.Stale=false;
            }finally{foreach(var pointer in updated)Marshal.Release(pointer);}
            link.Applied=true;session.Dirty=false;
            return J.Obj("session_id",session.Id,"document",document.Info(),"spectrum_index",link.Index,
                "peak_count",model.Peaks.Count,"live_readback_verified",true,"saved_project_verified",true,
                "unrelated_spectra_verified",true,"raw_data_verified",true,"grid_bindings_verified",true,"checkpoint_path",checkpoint,
                "applied_project_path",saved,"project_save",persisted,
                "note","Avantage displays the edited model in a complete saved project copy; the originally opened source is preserved.");
        }catch {
            if(displayRequested&&checkpoint!=null) {
                try{Documents.Open(checkpoint);}
                catch{Console.Error.WriteLine("Reopen the saved checkpoint to restore the document: "+checkpoint);}
            }
            throw;
        }finally {
            session.SourceProjectId=originalProject;session.ProjectSpectrumIndex=originalIndex;session.ProjectOriginalPeakIds=originalIds;
            if(snapshot!=null){snapshot.Dispose();projects.Remove(snapshot.Id);}
            foreach(var pointer in data)Marshal.Release(pointer);
        }
    }
    void InvalidateLiveLinks(string documentId) {
        foreach(var session in sessions.Values)if(session.LiveLink!=null&&session.LiveLink.DocumentId==documentId) {
            session.LiveLink.Dispose();session.LiveLink.Stale=true;
        }
    }
    static void VerifyGridVisibility(Dictionary<string,object> grid,IList<Dictionary<string,object>> document) {
        var pairs=(int[][])grid["grid_data_pairs"];
        if((int)grid["spectrum_count"]!=document.Count)throw new Fault("LIVE_GRID_MISMATCH","The saved grid has a different data list.");
        for(int index=0;index<document.Count;index++)if(pairs.Any(pair=>pair[1]==index)!=(bool)document[index]["visible_in_grid"])
            throw new Fault("LIVE_GRID_MISMATCH","A saved grid cell is not bound to its document spectrum. Reopen a healthy project before applying edits.");
    }
    static List<Dictionary<string,object>> CaptureLiveDocument(ProcessingDocument document,IList<IntPtr> data) {
        var result=new List<Dictionary<string,object>>();
        IntPtr grid=document.Grid();
        try {
        foreach(var pointer in data) {
            var source=(IDataSpace)Marshal.GetObjectForIUnknown(pointer);var peaks=ProcessingCom.Peaks(document.Pointer,pointer);
            try{IntPtr visual;ProcessingCom.At<ProcessingCom.FindVisual>(grid,38)(grid,pointer,out visual);bool visible=visual!=IntPtr.Zero;if(visual!=IntPtr.Zero)Marshal.Release(visual);result.Add(J.Obj("data",NativeSnapshot.Fingerprint(source),"model",NativePeakTablePersistence.ReadPeaks(peaks),"visible_in_grid",visible));}
            finally{ProcessingCom.ReleasePeaks(peaks);Marshal.ReleaseComObject(source);}
        }
        }finally{Marshal.Release(grid);}
        return result;
    }
}
}
