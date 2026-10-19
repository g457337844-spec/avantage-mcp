using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace AvantageMcp {
public sealed partial class Engine {
    static void ValidateLiveParameterUpdate(IList<Dictionary<string,object>> oldModel,IList<Dictionary<string,object>> next) {
        if(oldModel.Count!=next.Count||next.Count==0)throw new Fault("LIVE_STRUCTURE_CHANGE_UNSUPPORTED","Same-window apply currently requires the same existing peak count; use private project processing for topology changes. No live change was sent.");
        for(int i=0;i<next.Count;i++){
            if(!NativePeakTablePersistence.Equivalent(oldModel[i]["background"],next[i]["background"]))throw new Fault("LIVE_BACKGROUND_CHANGE_UNSUPPORTED","Same-window apply requires the existing background. No live change was sent.");
            var previous=(IEnumerable<Dictionary<string,object>>)oldModel[i]["constraints"];
            var desired=(IEnumerable<Dictionary<string,object>>)next[i]["constraints"];
            if(!previous.Select(c=>J.Int(c,"attribute",0)).OrderBy(x=>x).SequenceEqual(desired.Select(c=>J.Int(c,"attribute",0)).OrderBy(x=>x)))throw new Fault("LIVE_CONSTRAINT_STRUCTURE_UNSUPPORTED","Same-window apply requires the existing constraint attributes. Their parameters and links can change. No live change was sent.");
        }
    }
    static void WriteLiveParameterValues(IList<IAvFittedPeak> peaks,IList<Dictionary<string,object>> values) {
        for(int i=0;i<peaks.Count;i++){
            var p=values[i];var peak=peaks[i];
            peak.SetAsymParams(J.Num(p,"area"),J.Num(p,"area_ratio"),J.Num(p,"centre_kinetic_ev"),J.Num(p,"height"),J.Num(p,"height_ratio"),J.Num(p,"fwhm_ev"),J.Num(p,"gaussian_lorentz"),J.Num(p,"tail_mix"),J.Num(p,"tail_height"),J.Num(p,"tail_exponential"));
            peak.SetName(J.Str(p,"label"));peak.SetGLCombineMethod(J.Int(p,"native_gl_combine_method",0));
        }
        for(int i=0;i<peaks.Count;i++)foreach(var spec in (IEnumerable<Dictionary<string,object>>)values[i]["constraints"]){
            IntPtr raw=IntPtr.Zero,reference=IntPtr.Zero;IAvPeakConstraint constraint=null;
            try{
                peaks[i].GetPeakFitConstraint(J.Int(spec,"attribute",0),out raw);
                if(raw==IntPtr.Zero)throw new Fault("LIVE_CONSTRAINT_CHANGED","A live constraint was removed during apply.");
                constraint=(IAvPeakConstraint)Marshal.GetObjectForIUnknown(raw);
                if(spec["reference_peak_index"]!=null){int index=Convert.ToInt32(spec["reference_peak_index"]);if(index<0||index>=peaks.Count)throw new Fault("UNSUPPORTED_CONSTRAINT","A linked constraint refers outside this spectrum.");reference=Marshal.GetComInterfaceForObject(peaks[index],typeof(IAvFittedPeak));}
                // Both the constraint and any linked peak are existing objects
                // from the application. No worker-owned COM object is inserted.
                constraint.SetParameters(J.Int(spec,"type",0),J.Num(spec,"value1"),J.Num(spec,"value2"),J.Num(spec,"value3"),reference,J.Int(spec,"operator",0));
            }finally{if(reference!=IntPtr.Zero)Marshal.Release(reference);if(constraint!=null)Marshal.ReleaseComObject(constraint);if(raw!=IntPtr.Zero)Marshal.Release(raw);}
        }
    }
    static void RefreshLiveParameters(ProcessingDocument doc) {
        IntPtr grid=doc.Grid();try{Marshal.ThrowExceptionForHR(ProcessingCom.At<ProcessingCom.DeferredEvent>(grid,76)(grid,0));ProcessingCom.CheckDeferred(grid);}finally{Marshal.Release(grid);}
    }
    object ApplyLiveParameters(SpectrumSession session) {
        if(session.LiveLink==null)throw new Fault("LIVE_SESSION_REQUIRED","Attach the original document spectrum first.");
        var link=session.LiveLink;if(link.Stale)throw new Fault("LIVE_DOCUMENT_CHANGED","Reattach the current original spectrum before applying.");
        var model=Model(session,true);if(NativeSnapshot.Fingerprint(session.Data)!=link.OriginalFingerprint)throw new Fault("LIVE_DATA_CHANGED","Raw data and energy axes must remain unchanged.");
        var doc=Documents.Find(link.DocumentId);Documents.Activate(doc);var data=ProcessingCom.EnumerateData(doc.Pointer);List<IAvFittedPeak> remote=null;
        try{
            if(link.Index<0||link.Index>=data.Count)throw new Fault("LIVE_DOCUMENT_CHANGED","The spectrum index changed.");
            // A marshalled interface pointer is not a durable spectrum identity.
            // The exact document, index, complete raw/axis fingerprint and full
            // original model fingerprint below are the optimistic concurrency guard.
            var before=CaptureLiveDocument(doc,data);
            if((string)before[link.Index]["data"]!=link.OriginalFingerprint||ObjectFingerprint(before[link.Index]["model"])!=link.OriginalModelFingerprint)throw new Fault("LIVE_MODEL_CHANGED","The original model changed after attachment.");
            remote=ProcessingCom.Peaks(doc.Pointer,data[link.Index]);
            var previous=NativePeakTablePersistence.ReadPeaks(remote);
            var expected=NativePeakTablePersistence.ReadPeaks(model.Peaks.Select(p=>p.Native).ToArray());
            ValidateLiveParameterUpdate(previous,expected);
            int wasModified;Marshal.ThrowExceptionForHR(ProcessingCom.At<ProcessingCom.GetInt>(doc.Pointer,9)(doc.Pointer,out wasModified));
            try{
                WriteLiveParameterValues(remote,expected);
                Marshal.ThrowExceptionForHR(ProcessingCom.At<ProcessingCom.SetInt>(doc.Pointer,8)(doc.Pointer,1));RefreshLiveParameters(doc);
                var actual=CaptureLiveDocument(doc,data);
                for(int i=0;i<actual.Count;i++)if(!NativePeakTablePersistence.Equivalent(before[i]["data"],actual[i]["data"])||!Equals(before[i]["visible_in_grid"],actual[i]["visible_in_grid"])||!NativePeakTablePersistence.PersistedEquivalent(i==link.Index?(object)expected:before[i]["model"],actual[i]["model"]))throw new Fault("LIVE_APPLY_NOT_VERIFIED","The original window did not retain the intended parameters.");
                link.OriginalModelFingerprint=ObjectFingerprint(actual[link.Index]["model"]);
            }catch{
                WriteLiveParameterValues(remote,previous);Marshal.ThrowExceptionForHR(ProcessingCom.At<ProcessingCom.SetInt>(doc.Pointer,8)(doc.Pointer,wasModified));RefreshLiveParameters(doc);
                if(!NativePeakTablePersistence.PersistedEquivalent(before,CaptureLiveDocument(doc,data)))throw new Fault("LIVE_ROLLBACK_FAILED","Inspect the original window; parameter restoration did not verify.");
                throw;
            }
            link.Applied=true;session.Dirty=false;
            return J.Obj("session_id",session.Id,"document",doc.Info(),"spectrum_index",link.Index,"peak_count",remote.Count,"live_readback_verified",true,"saved_project_verified",false,"raw_data_verified",true,"unrelated_spectra_verified",true,"grid_bindings_verified",true,"same_window_updated",true,"no_extra_windows_verified",true,"note","Existing application-owned peak and constraint parameters updated in the original window; save explicitly or use the default backed-up original save.");
        }finally{if(remote!=null)ProcessingCom.ReleasePeaks(remote);foreach(var pointer in data)Marshal.Release(pointer);}
    }
}
}
