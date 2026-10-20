using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace AvantageMcp {
public sealed partial class Engine {
    static void ValidateLiveParameterUpdate(IList<Dictionary<string,object>> oldModel,IList<Dictionary<string,object>> next) {
        // Peak count is a pure topology question, so the rule lives in the
        // COM-free LiveParameterPolicy and is exercised offline. Background and
        // constraint shape stay here because they compare native snapshots.
        LiveParameterPolicy.ValidatePeakTopology(oldModel.Count,next.Count);
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
            // Select the method before restoring the complete parameter tuple.
            int method;peak.GetGLCombineMethod(out method);int desired=J.Int(p,"native_gl_combine_method",0);
            if(method!=desired)peak.SetGLCombineMethod(desired);
            peak.SetAsymParams(J.Num(p,"area"),J.Num(p,"area_ratio"),J.Num(p,"centre_kinetic_ev"),J.Num(p,"height"),J.Num(p,"height_ratio"),J.Num(p,"fwhm_ev"),J.Num(p,"gaussian_lorentz"),J.Num(p,"tail_mix"),J.Num(p,"tail_height"),J.Num(p,"tail_exponential"));
            peak.SetName(J.Str(p,"label"));
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
    static bool CanCanonicalizeFreeArea(Dictionary<string,object> expected,Dictionary<string,object> actual) {
        var constraints=(IEnumerable<Dictionary<string,object>>)expected["constraints"];
        var area=constraints.FirstOrDefault(c=>J.Int(c,"attribute",0)==1);
        // The native simplex and grid use slightly different numerical area
        // integration. Only a free, range-constrained derived area may be
        // canonicalized. Every curve parameter and constraint stays exact.
        if(area==null||J.Int(area,"type",0)!=6)return false;
        double oldArea=J.Num(expected,"area"),newArea=Convert.ToDouble(actual["area"]);
        if(Double.IsNaN(newArea)||Double.IsInfinity(newArea)||oldArea<=0||newArea<J.Num(area,"value1")||newArea>J.Num(area,"value2")||Math.Abs(newArea-oldArea)>5e-5*oldArea)return false;
        var comparison=new Dictionary<string,object>(actual);comparison["area"]=oldArea;
        return NativePeakTablePersistence.PersistedEquivalent(expected,comparison);
    }
    static int CanonicalizeFreeAreas(NativePeakModel model,IList<Dictionary<string,object>> expected,IList<Dictionary<string,object>> actual) {
        if(expected.Count!=actual.Count)return 0;
        var updates=new List<int>();
        for(int i=0;i<expected.Count;i++){
            if(NativePeakTablePersistence.PersistedEquivalent(expected[i],actual[i]))continue;
            if(!CanCanonicalizeFreeArea(expected[i],actual[i]))return 0;
            updates.Add(i);
        }
        foreach(int i in updates){
            var p=actual[i];
            model.Peaks[i].Native.SetAsymParams(J.Num(p,"area"),J.Num(p,"area_ratio"),J.Num(p,"centre_kinetic_ev"),J.Num(p,"height"),J.Num(p,"height_ratio"),J.Num(p,"fwhm_ev"),J.Num(p,"gaussian_lorentz"),J.Num(p,"tail_mix"),J.Num(p,"tail_height"),J.Num(p,"tail_exponential"));
            expected[i]["area"]=p["area"];
        }
        return updates.Count;
    }
    object ApplyLiveParameters(SpectrumSession session) {
        if(session.LiveLink==null)throw new Fault("LIVE_SESSION_REQUIRED","Attach the original document spectrum first.");
        var link=session.LiveLink;if(link.Stale)throw new Fault("LIVE_DOCUMENT_CHANGED","Reattach the current original spectrum before applying.");
        var model=Model(session,true);if(NativeSnapshot.Fingerprint(session.Data)!=link.OriginalFingerprint)throw new Fault("LIVE_DATA_CHANGED","Raw data and energy axes must remain unchanged.");
        var doc=Documents.Find(link.DocumentId);Documents.Activate(doc);var data=ProcessingCom.EnumerateData(doc.Pointer);List<IAvFittedPeak> remote=null;
        try{
            Engine.Trace("apply:index="+link.Index+" data="+data.Count);
            if(link.Index<0||link.Index>=data.Count)throw new Fault("LIVE_DOCUMENT_CHANGED","The spectrum index changed.");
            var before=CaptureLiveDocument(doc,data);
            Engine.Trace("apply:captured");
            remote=ProcessingCom.Peaks(doc.Pointer,data[link.Index]);
            Engine.Trace("apply:remote="+remote.Count);
            var previous=NativePeakTablePersistence.ReadPeaks(remote);
            Engine.Trace("apply:previous="+previous.Count);
            Engine.Trace("apply:modelPeaks="+model.Peaks.Count);
            foreach(var pk in model.Peaks)Engine.Trace("apply:peak id="+pk.Id+" native="+(pk.Native==null?"NULL":"ok"));
            var expected=NativePeakTablePersistence.ReadPeaks(new List<IAvFittedPeak>(model.Peaks.Select(p=>p.Native)));
            Engine.Trace("apply:expected="+expected.Count);
            // Validate structure before the fingerprint comparison: an empty
            // original model has no serialized peak parameters, so the
            // fingerprint guard would fault with a null reference instead of
            // reporting the real reason (a topology change).
            ValidateLiveParameterUpdate(previous,expected);
            Engine.Trace("apply:structure-ok");
            if((string)before[link.Index]["data"]!=link.OriginalFingerprint||ObjectFingerprint(before[link.Index]["model"])!=link.OriginalModelFingerprint)throw new Fault("LIVE_MODEL_CHANGED","The original model changed after attachment.");
            int wasModified;Marshal.ThrowExceptionForHR(ProcessingCom.At<ProcessingCom.GetInt>(doc.Pointer,9)(doc.Pointer,out wasModified));
            int canonicalized=0;
            try{
                WriteLiveParameterValues(remote,expected);
                Marshal.ThrowExceptionForHR(ProcessingCom.At<ProcessingCom.SetInt>(doc.Pointer,8)(doc.Pointer,1));RefreshLiveParameters(doc);
                var actual=CaptureLiveDocument(doc,data);
                canonicalized=CanonicalizeFreeAreas(model,expected,(IList<Dictionary<string,object>>)actual[link.Index]["model"]);
                if(canonicalized>0){
                    // A second refresh must be stable at the original strict
                    // tolerance; canonicalization cannot mask continuing drift.
                    RefreshLiveParameters(doc);actual=CaptureLiveDocument(doc,data);
                    if(!NativePeakTablePersistence.PersistedEquivalent(expected,NativePeakTablePersistence.ReadPeaks(model.Peaks.Select(p=>p.Native).ToArray())))throw new Fault("LIVE_AREA_CANONICALIZATION_FAILED","The private candidate did not retain the grid's derived areas.");
                }
                for(int i=0;i<actual.Count;i++)if(!NativePeakTablePersistence.Equivalent(before[i]["data"],actual[i]["data"])||!Equals(before[i]["visible_in_grid"],actual[i]["visible_in_grid"])||!NativePeakTablePersistence.PersistedEquivalent(i==link.Index?(object)expected:before[i]["model"],actual[i]["model"])){
                    string diagnostic=System.IO.Path.Combine(session.DirectoryPath,"live-apply-readback.json");
                    var json=new System.Web.Script.Serialization.JavaScriptSerializer{MaxJsonLength=16*1024*1024,RecursionLimit=64};
                    System.IO.File.WriteAllText(diagnostic,json.Serialize(J.Obj("spectrum_index",i,"expected",i==link.Index?(object)expected:before[i]["model"],"actual",actual[i]["model"])),new System.Text.UTF8Encoding(false));
                    throw new Fault("LIVE_APPLY_NOT_VERIFIED","The original window did not retain the intended parameters. Readback diagnostic: "+diagnostic);
                }
                link.OriginalModelFingerprint=ObjectFingerprint(actual[link.Index]["model"]);
            }catch{
                WriteLiveParameterValues(remote,previous);Marshal.ThrowExceptionForHR(ProcessingCom.At<ProcessingCom.SetInt>(doc.Pointer,8)(doc.Pointer,wasModified));RefreshLiveParameters(doc);
                if(!NativePeakTablePersistence.PersistedEquivalent(before,CaptureLiveDocument(doc,data)))throw new Fault("LIVE_ROLLBACK_FAILED","Inspect the original window; parameter restoration did not verify.");
                throw;
            }
            link.Applied=true;session.Dirty=false;
            return J.Obj("session_id",session.Id,"document",doc.Info(),"spectrum_index",link.Index,"peak_count",remote.Count,"live_readback_verified",true,"saved_project_verified",false,"raw_data_verified",true,"unrelated_spectra_verified",true,"grid_bindings_verified",true,"same_window_updated",true,"no_extra_windows_verified",true,"derived_free_areas_canonicalized",canonicalized,"derived_area_relative_limit",5e-5,"note","Existing application-owned peak and constraint parameters updated in the original window; save explicitly or use the default backed-up original save.");
        }finally{if(remote!=null)ProcessingCom.ReleasePeaks(remote);foreach(var pointer in data)Marshal.Release(pointer);}
    }
}
}
