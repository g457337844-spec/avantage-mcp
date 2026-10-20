using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using VGDATASTORELib;

namespace AvantageMcp.InProcess
{
    // One short-lived undo journal per application/editor generation. The
    // bridge must verify UI readback, then commit BEFORE ordinary File Save.
    internal sealed class NativeModelTransaction : IDisposable
    {
        internal static NativeModelTransaction Pending;
        internal readonly string Token=Guid.NewGuid().ToString("N");
        internal string RequestId,Path,Raw;
        internal int Index;
        internal bool Applied,MutationStarted,BackgroundChanged;
        internal IntPtr NativeDocument,Document,ContextIdentity;
        List<IntPtr> data;
        IDataSpace target;
        List<IAvFittedPeak> original,created=new List<IAvFittedPeak>(),desiredObjects=new List<IAvFittedPeak>();
        List<Dictionary<string,object>> before,desired;
        List<NativeEditorObjects.Parent> parents,newParents=new List<NativeEditorObjects.Parent>();
        NativeEditorObjects.Parent[] originalParents;
        IntPtr[] originalBackgrounds;
        object[] sources;
        IAvantageBackground newBackground;
        IntPtr desiredBackground;
        bool ownsDesiredBackground;
        int wasModified;

        internal static Dictionary<string,object> Begin(IntPtr processing,IntPtr nativeDocument,IntPtr document,Dictionary<string,object> plan)
        {
            if(Pending!=null)throw new InvalidOperationException("LIVE_TRANSACTION_PENDING: finish the previous transaction");
            LiveModelPlanPolicy.Validate(plan);
            var journal=new NativeModelTransaction();
            try
            {
                journal.Capture(processing,nativeDocument,document,plan);
                if(NativeModelSnapshotCore.EditableEquivalent(journal.before,journal.desired))
                {var result=journal.Report();result["no_change"]=true;result["transaction_token"]=null;result["committed"]=true;journal.Dispose();return result;}
                Pending=journal;
                try
                {
                    journal.Apply();journal.Verify(journal.desired);journal.Applied=true;
                    NativeEditorObjects.Integer(journal.Document,8,1);
                    return journal.Report();
                }
                catch(Exception error)
                {
                    try{journal.Rollback();Pending=null;journal.Dispose();}
                    catch(Exception rollback){throw new InvalidOperationException("LIVE_ROLLBACK_FAILED: "+rollback.GetType().Name);}
                    throw new InvalidOperationException("LIVE_WRITE_ROLLED_BACK: "+error.Message);
                }
            }
            catch{if(Pending!=journal)journal.Dispose();throw;}
        }
        void Capture(IntPtr processing,IntPtr nativeDocument,IntPtr document,Dictionary<string,object> plan)
        {
            NativeDocument=nativeDocument;Document=NativeEditorObjects.Query(document,"6E55B71A-944B-11D4-866C-00C04F68D1AC");
            ContextIdentity=NativeEditorObjects.Query(processing,"00000000-0000-0000-C000-000000000046");
            Path=(string)plan["document_path"];RequestId=(string)plan["request_id"];Index=Convert.ToInt32(plan["spectrum_index"]);
            data=NativeDocumentAccess.DataSpaces(Document);if(Index>=data.Count)throw new InvalidOperationException("SPECTRUM_NOT_FOUND");
            target=(IDataSpace)Marshal.GetUniqueObjectForIUnknown(data[Index]);
            Raw=NativeRawFingerprint.Fingerprint(target);
            if(!string.Equals(Raw,(string)plan["expected_raw_sha256"],StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("LIVE_DATA_CHANGED");
            original=NativeDocumentAccess.Peaks(Document,data[Index]);before=NativeModelSnapshotCore.ReadPeaks(original);
            if(!NativeModelSnapshotCore.EditableEquivalent(before,plan["expected_peaks"]))throw new InvalidOperationException("LIVE_MODEL_CHANGED");
            desired=Records(plan["desired_peaks"]);sources=((IEnumerable)plan["source_peak_indices"]).Cast<object>().ToArray();
            if(desired.Count>0)NativeRawFingerprint.ValidateBackground(target,(IDictionary<string,object>)desired[0]["background"]);
            // Removing/updating a region must not invalidate cross-region links.
            for(int index=0;index<data.Count;index++)if(index!=Index)
            {
                var other=NativeDocumentAccess.Peaks(Document,data[index]);
                try{foreach(var peak in NativeModelSnapshotCore.ReadPeaks(other))foreach(IDictionary<string,object> c in (IEnumerable)peak["constraints"])if(c["reference_peak_index"]!=null&&Convert.ToInt32(c["reference_peak_index"])<0)throw new InvalidOperationException("LIVE_EXTERNAL_CONSTRAINTS_UNSUPPORTED");}
                finally{foreach(var p in other)Marshal.ReleaseComObject(p);}
            }
            parents=NativeEditorObjects.Parents(NativeDocument,data[Index]);originalParents=new NativeEditorObjects.Parent[original.Count];originalBackgrounds=new IntPtr[original.Count];
            for(int i=0;i<original.Count;i++)
            {
                IntPtr entry;original[i].GetPeakEntry(out entry);
                try{long identity=Identity(entry);originalParents[i]=parents.FirstOrDefault(p=>Identity(p.Pointer)==identity);}
                finally{if(entry!=IntPtr.Zero)Marshal.Release(entry);}
                if(originalParents[i]==null||originalParents[i].Background==IntPtr.Zero)throw new InvalidOperationException("LIVE_ORIGINAL_PARENT_REQUIRED");
                original[i].GetBackground(out originalBackgrounds[i]);
                if(originalBackgrounds[i]==IntPtr.Zero)throw new InvalidOperationException("LIVE_ORIGINAL_BACKGROUND_REQUIRED");
            }
            Marshal.ThrowExceptionForHR(NativeEditorObjects.Slot<NativeEditorObjects.GetInteger>(Document,9)(Document,out wasModified));
        }
        internal void RequireContext(IntPtr processing,IntPtr nativeDocument)
        {
            IntPtr identity=NativeEditorObjects.Query(processing,"00000000-0000-0000-C000-000000000046");
            try{if(identity!=ContextIdentity||nativeDocument!=NativeDocument)throw new InvalidOperationException("LIVE_APP_SESSION_CHANGED");}
            finally{Marshal.Release(identity);}
            var current=NativeDocumentAccess.DataSpaces(Document);
            try{if(Index>=current.Count||Identity(current[Index])!=Identity(data[Index]))throw new InvalidOperationException("LIVE_DATA_IDENTITY_CHANGED");}
            finally{foreach(var p in current)Marshal.Release(p);}
        }
        internal string CurrentHash()
        {
            var current=NativeDocumentAccess.Peaks(Document,data[Index]);
            try{return NativeModelSnapshotCore.Fingerprint(NativeModelSnapshotCore.ReadPeaks(current));}
            finally{foreach(var p in current)Marshal.ReleaseComObject(p);}
        }
        internal void RequireCurrentHash(string expected)
        {if(!string.Equals(CurrentHash(),expected,StringComparison.OrdinalIgnoreCase)||Raw!=NativeRawFingerprint.Fingerprint(target))throw new InvalidOperationException("LIVE_TRANSACTION_STATE_CHANGED");}

        void Apply()
        {
            if(desired.Count>0)
            {
                var bg=(IDictionary<string,object>)desired[0]["background"];
                BackgroundChanged=before.Count==0||before.Any(p=>!NativeModelSnapshotCore.EditableEquivalent(p["background"],bg));
                if(!BackgroundChanged)desiredBackground=originalBackgrounds[0];
                else{newBackground=NativeEditorObjects.NewBackground(bg);desiredBackground=Marshal.GetComInterfaceForObject(newBackground,typeof(IAvantageBackground));ownsDesiredBackground=true;}
                if(BackgroundChanged)foreach(var parent in parents)if(parent.Background!=IntPtr.Zero){MutationStarted=true;NativeEditorObjects.Pointer(parent.Pointer,9,desiredBackground);}
            }
            var usedParents=new HashSet<NativeEditorObjects.Parent>();
            for(int i=0;i<sources.Length;i++)if(sources[i]!=null)usedParents.Add(originalParents[Convert.ToInt32(sources[i])]);
            for(int i=0;i<desired.Count;i++)
            {
                IAvFittedPeak peak;
                if(sources[i]!=null)
                {
                    peak=original[Convert.ToInt32(sources[i])];MutationStarted=true;
                    if(BackgroundChanged)peak.SetBackground(desiredBackground);
                    NativeEditorObjects.Values(peak,desired[i]);
                }
                else
                {
                    var parent=parents.Where(p=>!usedParents.Contains(p)&&p.Background!=IntPtr.Zero).OrderBy(p=>p.Name==(string)desired[i]["label"]?0:1).ThenBy(p=>Math.Abs(p.Centre-NativeEditorObjects.Number(desired[i],"centre_kinetic_ev"))).FirstOrDefault();
                    if(parent==null)
                    {
                        var template=parents.Where(p=>p.Background!=IntPtr.Zero).OrderBy(p=>Math.Abs(p.Centre-NativeEditorObjects.Number(desired[i],"centre_kinetic_ev"))).FirstOrDefault();
                        parent=NativeEditorObjects.NewParent(data[Index],desiredBackground,desired[i],(IDictionary<string,object>)desired[i]["background"],template);newParents.Add(parent);
                        MutationStarted=true;
                        if(AddParent()(NativeDocument,parent.Pointer)!=1)throw new InvalidOperationException("LIVE_PRIMARY_INSERT_FAILED");parent.Added=true;
                    }
                    else{MutationStarted=true;NativeEditorObjects.Pointer(parent.Pointer,9,desiredBackground);}
                    usedParents.Add(parent);
                    peak=(IAvFittedPeak)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("B90B1A14-A54F-11D2-852A-00104B053B4C")));created.Add(peak);
                    peak.SetDataSpace(data[Index]);peak.SetBackground(desiredBackground);peak.SetPeakEntry(parent.Pointer);NativeEditorObjects.Values(peak,desired[i]);
                    if(Add(peak)!=1)throw new InvalidOperationException("LIVE_FITTED_INSERT_FAILED");
                }
                desiredObjects.Add(peak);
            }
            MutationStarted=true;NativeEditorObjects.Constraints(desiredObjects,desired);
            var kept=new HashSet<int>(sources.Where(x=>x!=null).Select(Convert.ToInt32));
            for(int i=0;i<original.Count;i++)if(!kept.Contains(i)&&Remove(original[i])!=1)throw new InvalidOperationException("LIVE_FITTED_REMOVE_FAILED");
        }
        internal void Rollback()
        {
            if(!MutationStarted)return;
            var current=NativeDocumentAccess.Peaks(Document,data[Index]);
            try{foreach(var peak in current)if(Remove(peak)!=1)throw new InvalidOperationException("rollback removal failed");}
            finally{foreach(var p in current)Marshal.ReleaseComObject(p);}
            foreach(var parent in parents)if(parent.Background!=IntPtr.Zero)NativeEditorObjects.Pointer(parent.Pointer,9,parent.Background);
            foreach(var parent in newParents.AsEnumerable().Reverse())if(parent.Added)
            {if(RemoveParent()(NativeDocument,parent.Pointer,0)!=1)throw new InvalidOperationException("rollback primary removal failed");parent.Added=false;}
            for(int i=0;i<original.Count;i++)
            {
                original[i].SetDataSpace(data[Index]);original[i].SetPeakEntry(originalParents[i].Pointer);original[i].SetBackground(originalBackgrounds[i]);
                NativeEditorObjects.Values(original[i],before[i]);if(Add(original[i])!=1)throw new InvalidOperationException("rollback original insertion failed");
            }
            for(int i=0;i<original.Count;i++)NativeEditorObjects.Values(original[i],before[i]);
            NativeEditorObjects.Constraints(original,before);Verify(before);NativeEditorObjects.Integer(Document,8,wasModified);
        }
        void Verify(object expected)
        {
            var actual=NativeDocumentAccess.Peaks(Document,data[Index]);
            try{if(Raw!=NativeRawFingerprint.Fingerprint(target)||!NativeModelSnapshotCore.EditableEquivalent(expected,NativeModelSnapshotCore.ReadPeaks(actual)))throw new InvalidOperationException("LIVE_MODEL_READBACK_MISMATCH");}
            finally{foreach(var p in actual)Marshal.ReleaseComObject(p);}
        }
        internal Dictionary<string,object> Report()
        {return new Dictionary<string,object>{{"operation","apply-region-model-v1"},{"request_id",RequestId},{"applied",true},{"raw_verified",true},{"model_verified",true},{"spectrum_index",Index},{"before_count",before.Count},{"peak_count",desired.Count},{"new_primary_count",newParents.Count},{"background_changed",BackgroundChanged},{"transaction_token",Token},{"committed",false}};}
        static List<Dictionary<string,object>> Records(object value){return ((IEnumerable)value).Cast<Dictionary<string,object>>().ToList();}
        static long Identity(IntPtr value){if(value==IntPtr.Zero)throw new InvalidOperationException("LIVE_NULL_NATIVE_OBJECT");IntPtr p=NativeEditorObjects.Query(value,"00000000-0000-0000-C000-000000000046");try{return p.ToInt64();}finally{Marshal.Release(p);}}
        static NativeEditorObjects.AddPrimary AddParent(){return NativeEditorObjects.Export<NativeEditorObjects.AddPrimary>("?AddPeak@CProcessGridDoc@@QEAAHPEAUIPeakTableEntry@@@Z",0x15ac10);}
        static NativeEditorObjects.RemoveItem RemoveParent(){return NativeEditorObjects.Export<NativeEditorObjects.RemoveItem>("?RemovePeak@CProcessGridDoc@@QEAAHPEAUIPeakTableEntry@@H@Z",0x167960);}
        int Add(IAvFittedPeak peak){IntPtr pointer=Marshal.GetComInterfaceForObject(peak,typeof(IAvFittedPeak));try{return NativeEditorObjects.Export<NativeEditorObjects.AddFitted>("?AddFittedPeak@CProcessGridDoc@@QEAAHPEAUIFittedPeak@@PEAUIDataSpace@@@Z",0x15a310)(NativeDocument,pointer,data[Index]);}finally{Marshal.Release(pointer);}}
        int Remove(IAvFittedPeak peak){IntPtr pointer=Marshal.GetComInterfaceForObject(peak,typeof(IAvFittedPeak));try{return NativeEditorObjects.Export<NativeEditorObjects.RemoveItem>("?RemoveFittedPeak@CProcessGridDoc@@QEAAHPEAUIFittedPeak@@H@Z",0x167660)(NativeDocument,pointer,0);}finally{Marshal.Release(pointer);}}
        public void Dispose()
        {
            if(ownsDesiredBackground&&desiredBackground!=IntPtr.Zero){Marshal.Release(desiredBackground);desiredBackground=IntPtr.Zero;}
            if(newBackground!=null){Marshal.ReleaseComObject(newBackground);newBackground=null;}
            foreach(var peak in created)Marshal.ReleaseComObject(peak);created.Clear();
            if(original!=null){foreach(var peak in original)Marshal.ReleaseComObject(peak);original=null;}
            if(originalBackgrounds!=null){foreach(var p in originalBackgrounds)if(p!=IntPtr.Zero)Marshal.Release(p);originalBackgrounds=null;}
            if(parents!=null){foreach(var parent in parents)parent.Dispose();parents=null;}
            foreach(var parent in newParents)parent.Dispose();newParents.Clear();
            if(target!=null){Marshal.ReleaseComObject(target);target=null;}
            if(data!=null){foreach(var p in data)Marshal.Release(p);data=null;}
            if(Document!=IntPtr.Zero){Marshal.Release(Document);Document=IntPtr.Zero;}
            if(ContextIdentity!=IntPtr.Zero){Marshal.Release(ContextIdentity);ContextIdentity=IntPtr.Zero;}
        }
    }
}
