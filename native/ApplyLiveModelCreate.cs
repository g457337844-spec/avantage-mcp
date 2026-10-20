using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using VGDATASTORELib;

namespace AvantageMcp {
/// <summary>
/// Structural peak editing on an already-open application document.
///
/// Avantage keeps a primary peak-table entry and a fitted peak per component.
/// The parameter-only live path can never create either, so a spectrum with no
/// model cannot receive a fit. This path uses the vendor's own
/// CProcessGridDoc entry points and leaves every inserted object owned by the
/// application document, never by this worker.
/// </summary>
public sealed partial class Engine {
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int AddPrimaryEntry(IntPtr self,IntPtr entry);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int AddFittedPeak(IntPtr self,IntPtr peak,IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int RemoveFittedPeakFn(IntPtr self,IntPtr peak,int updateDisplays);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EntryDataSetter(IntPtr self,IntPtr value);
    [DllImport("kernel32.dll",CharSet=CharSet.Ansi,ExactSpelling=true)]
    static extern IntPtr GetProcAddress(IntPtr module,string name);
    static T NativeExport<T>(string name) where T:class {
        IntPtr address=GetProcAddress(NativeGridTables.VerifiedModule,name);
        if(address==IntPtr.Zero)throw new Fault("UNSUPPORTED_BUILD","Native entry point is unavailable: "+name);
        return Marshal.GetDelegateForFunctionPointer(address,typeof(T))as T;
    }
    static IntPtr DataInterface(IntPtr pointer) {
        return Marshal.GetComInterfaceForObject((IDataSpace)Marshal.GetObjectForIUnknown(pointer),typeof(IDataSpace));
    }

    int DocumentPeakCount(ProcessingDocument document,IntPtr dataPointer) {
        var peaks=ProcessingCom.Peaks(document.Pointer,dataPointer);
        try{return peaks.Count;}finally{ProcessingCom.ReleasePeaks(peaks);}
    }

    /// <summary>Create every private peak in an empty application spectrum.</summary>
    object ApplyLiveModelCreating(SpectrumSession session) {
        if(session.LiveLink==null)throw new Fault("LIVE_SESSION_REQUIRED","Attach the original document spectrum first.");
        var link=session.LiveLink;
        if(link.Stale)throw new Fault("LIVE_DOCUMENT_CHANGED","Reattach the current original spectrum before applying.");
        var model=Model(session,true);
        if(model.Peaks.Count==0)throw new Fault("PEAKS_REQUIRED","Add at least one peak before applying.");
        if(session.BackgroundState==null||session.BackgroundState.Background==null)
            throw new Fault("BACKGROUND_REQUIRED","Set a background before creating peaks in the document.");
        if(NativeSnapshot.Fingerprint(session.Data)!=link.OriginalFingerprint)
            throw new Fault("LIVE_DATA_CHANGED","Raw data and energy axes must remain unchanged.");

        var doc=Documents.Find(link.DocumentId);
        Documents.Activate(doc);
        var data=ProcessingCom.EnumerateData(doc.Pointer);
        string table=Path.Combine(session.DirectoryPath,"apply-"+Guid.NewGuid().ToString("N")+".vgt");
        try {
            if(link.Index<0||link.Index>=data.Count)throw new Fault("LIVE_DOCUMENT_CHANGED","The spectrum index changed.");
            var targetPointer=data[link.Index];
            var before=CaptureLiveDocument(doc,data);
            if((string)before[link.Index]["data"]!=link.OriginalFingerprint)
                throw new Fault("LIVE_DATA_CHANGED","The original spectrum data changed after attachment.");

            NativePeakTablePersistence.Save(table,session.Data,model.Peaks.Select(p=>p.Native).ToArray());
            IntPtr targetInterface=DataInterface(targetPointer);
            var handedOver=new List<IntPtr>();
            try {
                using(var imported=new NativePeakTablePersistence.Document(session.Data)) {
                    imported.Load(table);
                    var peaks=imported.Peaks();
                    var primary=ProjectPersistence.PrimaryEntries(imported);
                    try {
                        var addPrimary=NativeExport<AddPrimaryEntry>("?AddPeak@CProcessGridDoc@@QEAAHPEAUIPeakTableEntry@@@Z");
                        var addFitted=NativeExport<AddFittedPeak>("?AddFittedPeak@CProcessGridDoc@@QEAAHPEAUIFittedPeak@@PEAUIDataSpace@@@Z");
                        // When the staged table carries no primary entries, add
                        // the fitted peaks alone; the document creates its own
                        // entries for them.
                        foreach(IntPtr entry in primary) {
                            Marshal.ThrowExceptionForHR(ProcessingCom.At<EntryDataSetter>(entry,11)(entry,targetInterface));
                            if(addPrimary(doc.Pointer,entry)!=1)
                                throw new Fault("NATIVE_PROJECT_FAILED","Avantage refused the primary peak entry.");
                            handedOver.Add(entry);
                        }
                        foreach(var peak in peaks) {
                            peak.SetDataSpace(targetInterface);
                            IntPtr fitted=Marshal.GetComInterfaceForObject(peak,typeof(IAvFittedPeak));
                            try {
                                if(addFitted(doc.Pointer,fitted,targetInterface)!=1)
                                    throw new Fault("NATIVE_PROJECT_FAILED","Avantage refused the fitted peak.");
                            } finally { Marshal.Release(fitted); }
                        }
                    } finally {
                        foreach(IntPtr entry in primary)if(!handedOver.Contains(entry))Marshal.Release(entry);
                        foreach(var peak in peaks)Marshal.ReleaseComObject(peak);
                    }
                }
            } finally { Marshal.Release(targetInterface); }

            var after=CaptureLiveDocument(doc,data);
            var actual=(IList<Dictionary<string,object>>)after[link.Index]["model"];
            if(actual.Count!=model.Peaks.Count) {
                try{RemoveDocumentPeaks(doc,targetPointer);}catch(Exception error){Engine.Trace("rollback failed: "+error.Message);}
                throw new Fault("LIVE_APPLY_NOT_VERIFIED",
                    "The document retained "+actual.Count+" of "+model.Peaks.Count+" peaks; created peaks were removed again.");
            }
            link.Applied=true;session.Dirty=false;
            link.OriginalModelFingerprint=ObjectFingerprint(actual);
            return J.Obj(
                "session_id",session.Id,"document",doc.Info(),"spectrum_index",link.Index,
                "peak_count",actual.Count,"live_readback_verified",true,
                "created_in_document",true,"raw_data_verified",true,
                "no_extra_windows_verified",true,
                "note","Peaks created in the original window. Save the project to persist them.");
        } finally {
            foreach(var pointer in data)Marshal.Release(pointer);
            try{if(File.Exists(table))File.Delete(table);}catch(IOException){}
        }
    }

    /// <summary>Remove every peak the application owns for one spectrum.</summary>
    int RemoveDocumentPeaks(ProcessingDocument document,IntPtr dataPointer) {
        var removeFitted=NativeExport<RemoveFittedPeakFn>("?RemoveFittedPeak@CProcessGridDoc@@QEAAHPEAUIFittedPeak@@H@Z");
        int removed=0;
        for(int guard=0;guard<200;guard++) {
            var peaks=ProcessingCom.Peaks(document.Pointer,dataPointer);
            if(peaks.Count==0){ProcessingCom.ReleasePeaks(peaks);break;}
            try {
                IntPtr pointer=Marshal.GetComInterfaceForObject(peaks[0],typeof(IAvFittedPeak));
                try {
                    if(removeFitted(document.Pointer,pointer,1)!=1)throw new Fault("NATIVE_PROJECT_FAILED","Avantage refused to remove a fitted peak.");
                    removed++;
                } finally { Marshal.Release(pointer); }
            } finally { ProcessingCom.ReleasePeaks(peaks); }
        }
        return removed;
    }
}
}
