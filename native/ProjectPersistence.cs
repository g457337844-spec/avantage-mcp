using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VGDATASTORELib;

namespace AvantageMcp
{
    // A VGP is an OLE processing document plus a companion directory. Saving a
    // fitted-only VGT, or calling LoadPeakTable followed by SavePeakTable, loses
    // native entries not currently represented in the grid. These helpers own a
    // complete native graph and preserve the original document/layout container.
    internal static class ProjectPersistence
    {
        internal const bool GridReferencesVerified=true;
        [DllImport("kernel32.dll", CharSet=CharSet.Ansi, ExactSpelling=true)]
        static extern IntPtr GetProcAddress(IntPtr module,string name);
        [DllImport("ole32.dll", CharSet=CharSet.Unicode)]
        static extern int StgOpenStorage(string path,IntPtr priority,uint mode,IntPtr exclude,uint reserved,out IntPtr storage);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int OpenStorage(IntPtr self,[MarshalAs(UnmanagedType.LPWStr)] string name,IntPtr priority,uint mode,IntPtr exclude,uint reserved,out IntPtr child);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CommitStorage(IntPtr self,uint flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void CollectionOperation(IntPtr collection);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void CollectionIo(IntPtr self,IntPtr collection,IntPtr storage);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr FirstItem(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr NextItem(IntPtr self,ref IntPtr position);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int LoadData(IntPtr self,IntPtr item,int repair,int overwrite);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr AssignString(IntPtr self,[MarshalAs(UnmanagedType.LPWStr)] string value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int AddPrimary(IntPtr self,IntPtr peak);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int RemovePrimary(IntPtr self,IntPtr peak,int updateDisplays);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int AddFitted(IntPtr self,IntPtr peak,IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int PointerGetter(IntPtr self,out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int PointerSetter(IntPtr self,IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int OpenStream(IntPtr self,[MarshalAs(UnmanagedType.LPWStr)] string name,IntPtr reserved,uint mode,uint flags,out IntPtr stream);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int CreateStream(IntPtr self,[MarshalAs(UnmanagedType.LPWStr)] string name,uint mode,uint reserved1,uint reserved2,out IntPtr stream);

        static readonly string[] CollectionNames={"Data Spaces","Backgrounds","Peaks","Fitted Peaks","Chem States","Procedural Peaks"};
        static readonly int[] CollectionOffsets={0x240,0x318,0x3c0,0x498,0x608,0x650};
        static object ownedDataCache;
        static IntPtr ownedCacheInterface;
        internal static void EnsureIndependentCache()
        {
            if(ownedCacheInterface!=IntPtr.Zero)return;
            VerifiedBuild.RequireProcessing();
            IntPtr module=NativeGridTables.VerifiedModule;
            if(ownedCacheInterface!=IntPtr.Zero)return;
            if(Marshal.ReadIntPtr(module,0x30d148)!=IntPtr.Zero)
                throw new Fault("NATIVE_CACHE_IN_USE","The private native host already has an external data cache.");
            object cache=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("F9403556-9311-11D1-BCE0-006097D69A8B")));
            IntPtr unknown=IntPtr.Zero,own=IntPtr.Zero;
            try {
                unknown=Marshal.GetIUnknownForObject(cache);
                Guid iid=new Guid("CC4A41E1-9319-11D1-BCE0-006097D69A8B");
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown,ref iid,out own));
                // ProcessGrid's RAII cache accessor otherwise obtains Avantage's
                // live ROT cache. Pin a fresh in-process cache in this worker's
                // private module state, so owned documents never share app data.
                Marshal.WriteIntPtr(module,0x30d148,own);
                Marshal.WriteInt32(module,0x30d150,1);
                ownedDataCache=cache;ownedCacheInterface=own;cache=null;own=IntPtr.Zero;
            } finally {if(unknown!=IntPtr.Zero)Marshal.Release(unknown);if(own!=IntPtr.Zero)Marshal.Release(own);J.Release(cache);}
        }
        internal static void InitializeDocumentCache(IntPtr document)
        {
            EnsureIndependentCache();
            IntPtr existing=Marshal.ReadIntPtr(document,0x238);
            Marshal.AddRef(ownedCacheInterface);
            Marshal.WriteIntPtr(document,0x238,ownedCacheInterface);
            if(existing!=IntPtr.Zero)Marshal.Release(existing);
        }
        internal static string HashShared(string path)
        {
            using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
            using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(file)).Replace("-","");
        }
        internal static void CopyShared(string source,string target)
        {
            using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
            using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None))input.CopyTo(output);
        }
        static T Bind<T>(string name) where T:class
        {
            IntPtr address=GetProcAddress(NativeGridTables.VerifiedModule,name);
            if(address==IntPtr.Zero)throw new EntryPointNotFoundException(name);
            return Marshal.GetDelegateForFunctionPointer(address,typeof(T)) as T;
        }
        static T Slot<T>(IntPtr value,int index) where T:class
        {
            return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(value),index*IntPtr.Size),typeof(T)) as T;
        }
        static List<IntPtr> DataItems(IntPtr document)
        {
            var result=new List<IntPtr>();
            IntPtr position=Bind<FirstItem>("?GetFirstDataSpace@CProcessGridDoc@@QEBAPEAU__POSITION@@XZ")(document);
            var next=Bind<NextItem>("?GetNextDataSpace@CProcessGridDoc@@QEAAAEAVCDataSpaceItem@@AEAPEAU__POSITION@@@Z");
            while(position!=IntPtr.Zero)
            {
                if(result.Count>=4096)throw new Fault("PROJECT_SIZE","A project may contain at most 4096 spectra.");
                result.Add(next(document,ref position));
            }
            return result;
        }
        internal static void LoadComplete(NativePeakTablePersistence.Document document,string path)
        {
            NativeProcessingProject.Validate(path);
            IntPtr module=NativeGridTables.VerifiedModule,root=IntPtr.Zero;
            string previousDirectory=Directory.GetCurrentDirectory();
            try
            {
                // Relative native spectrum references resolve against the owned
                // project directory. Restore the worker's directory on every exit.
                Directory.SetCurrentDirectory(Path.GetDirectoryName(path));
                Marshal.ThrowExceptionForHR(StgOpenStorage(path,IntPtr.Zero,0x20,IntPtr.Zero,0,out root));
                var load=Bind<CollectionIo>("?LoadCollection@CProcessGridDoc@@IEAAXPEAVCCollection@@PEAUIStorage@@@Z");
                // Constructor-wired cross references are resolved only after all
                // collections and spectrum data have been loaded.
                foreach(int index in new[]{1,2,3,5,0,4})
                {
                    IntPtr child=IntPtr.Zero;
                    try
                    {
                        Marshal.ThrowExceptionForHR(Slot<OpenStorage>(root,6)(root,CollectionNames[index],IntPtr.Zero,0x10,IntPtr.Zero,0,out child));
                        load(document.Handle,IntPtr.Add(document.Handle,CollectionOffsets[index]),child);
                    }
                    finally{if(child!=IntPtr.Zero)Marshal.Release(child);}
                }
                foreach(IntPtr item in DataItems(document.Handle)) {
                    string recorded=Marshal.PtrToStringUni(Marshal.ReadIntPtr(item,0xb0));
                    string file=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path),recorded));
                    string directory=Path.GetFullPath(Path.GetDirectoryName(path)).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
                    if(!file.StartsWith(directory,StringComparison.OrdinalIgnoreCase))
                        throw new Fault("EXTERNAL_PROJECT_DATA","A project spectrum references data outside its private project directory.");
                    string relative=file.Substring(directory.Length),section=relative.Split(Path.DirectorySeparatorChar)[0];
                    if(!section.EndsWith(".DATA",StringComparison.OrdinalIgnoreCase)||!File.Exists(file))
                        throw new Fault("COMPANION_DATA_MISSING","A referenced project spectrum is absent from its private .DATA directory.");
                    var data=new DataSpaceClass();
                    try {
                        data.LoadPersistFile(file,0,1,0);
                        IntPtr own=Marshal.GetComInterfaceForObject(data,typeof(IDataSpace));
                        Marshal.WriteIntPtr(item,0xc0,own);
                    } finally {J.Release(data);}
                }
                foreach(int offset in CollectionOffsets)
                {
                    IntPtr collection=IntPtr.Add(document.Handle,offset);
                    Slot<CollectionOperation>(collection,18)(collection);
                }
            }
            finally
            {
                if(root!=IntPtr.Zero)Marshal.Release(root);
                Directory.SetCurrentDirectory(previousDirectory);
            }
        }
        internal static void SaveComplete(NativePeakTablePersistence.Document document,string path)
        {
            // The ABI hash gate covers the collection offsets and vtable slots.
            IntPtr module=NativeGridTables.VerifiedModule,root=IntPtr.Zero;
            byte[] previousDataReferences=ReadStream(path,"Data Spaces","XRefs");
            byte[] previousChemicalContents=ReadStream(path,"Chem States","Contents");
            byte[] previousChemicalReferences=ReadStream(path,"Chem States","XRefs");
            int originalGridGroup=ReferenceGroupSize(previousDataReferences);
            foreach(int offset in CollectionOffsets)
            {
                IntPtr collection=IntPtr.Add(document.Handle,offset);
                Slot<CollectionOperation>(collection,17)(collection);
            }
            try
            {
                Marshal.ThrowExceptionForHR(StgOpenStorage(path,IntPtr.Zero,0x12,IntPtr.Zero,0,out root));
                var save=Bind<CollectionIo>("?SaveCollection@CProcessGridDoc@@IEAAXPEAVCCollection@@PEAUIStorage@@@Z");
                for(int index=0;index<CollectionNames.Length;index++)
                {
                    IntPtr child=IntPtr.Zero;
                    try
                    {
                        Marshal.ThrowExceptionForHR(Slot<OpenStorage>(root,6)(root,CollectionNames[index],IntPtr.Zero,0x12,IntPtr.Zero,0,out child));
                        save(document.Handle,IntPtr.Add(document.Handle,CollectionOffsets[index]),child);
                        Marshal.ThrowExceptionForHR(Slot<CommitStorage>(child,9)(child,0));
                    }
                    finally{if(child!=IntPtr.Zero)Marshal.Release(child);}
                }
                Marshal.ThrowExceptionForHR(Slot<CommitStorage>(root,9)(root,0));
            }
            finally{if(root!=IntPtr.Zero)Marshal.Release(root);}
            // Grid cell objects require a live UI and are deliberately not created
            // here. Keep their original reverse-reference group while taking the
            // vendor's newly rebuilt peak/background reference groups. Spectrum
            // indices remain unchanged because this operation edits one model.
            byte[] updated=ReadStream(path,"Data Spaces","XRefs");int updatedGridGroup=ReferenceGroupSize(updated);
            var merged=new byte[originalGridGroup+updated.Length-updatedGridGroup];
            Buffer.BlockCopy(previousDataReferences,0,merged,0,originalGridGroup);
            Buffer.BlockCopy(updated,updatedGridGroup,merged,originalGridGroup,updated.Length-updatedGridGroup);
            WriteStream(path,merged,"Data Spaces","XRefs");
            if(previousChemicalContents.Length>=8&&BitConverter.ToInt32(previousChemicalContents,4)==0)
                WriteStream(path,previousChemicalReferences,"Chem States","XRefs");
        }
        static int ReferenceGroupSize(byte[] data)
        {
            if(data.Length<8||BitConverter.ToInt32(data,0)!=0)throw new Fault("UNSUPPORTED_PROJECT","The native data/grid cross-reference format is unsupported.");
            int count=BitConverter.ToInt32(data,4);
            if(count<0||count>4096||8L+8L*count>data.Length)throw new Fault("INVALID_PROJECT","The native grid reference group is malformed.");
            return 8+8*count;
        }
        internal static void RebaseCompanion(NativePeakTablePersistence.Document document,string oldCompanion,string newName)
        {
            IntPtr module=NativeGridTables.VerifiedModule;
            // ProcessGrid imports MFC CString<wchar_t>::operator=(wchar_t*) at
            // IAT RVA 0x259000 (ordinal 1503). This is gated by the exact DLL hash.
            var assign=(AssignString)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(module,0x259000),typeof(AssignString));
            string oldRoot=Path.GetFullPath(oldCompanion).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            string parent=Path.GetDirectoryName(oldCompanion);
            foreach(IntPtr item in DataItems(document.Handle))
            {
                string recorded=Marshal.PtrToStringUni(Marshal.ReadIntPtr(item,0xb0));
                if(String.IsNullOrWhiteSpace(recorded))throw new Fault("INVALID_PROJECT","A native spectrum reference is empty.");
                string resolved=Path.GetFullPath(Path.Combine(parent,recorded));
                if(!resolved.StartsWith(oldRoot,StringComparison.OrdinalIgnoreCase)||!File.Exists(resolved))
                    throw new Fault("EXTERNAL_PROJECT_DATA","Save currently requires all spectrum references to remain inside the matching .DATA directory.");
                string relative=resolved.Substring(oldRoot.Length);
                assign(IntPtr.Add(item,0xb0),".\\"+newName+"\\"+relative);
            }
        }
        internal static IntPtr Identity(IntPtr value)
        {
            IntPtr result;Guid iid=new Guid("00000000-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(value,ref iid,out result));return result;
        }
        internal static List<IntPtr> PrimaryEntries(NativePeakTablePersistence.Document document)
        {
            var result=new List<IntPtr>();
            IntPtr position=Bind<FirstItem>("?GetFirstPeak@CProcessGridDoc@@QEBAPEAU__POSITION@@XZ")(document.Handle);
            var next=Bind<NextItem>("?GetNextPeak@CProcessGridDoc@@QEAAAEAVCPeakItem@@AEAPEAU__POSITION@@@Z");
            while(position!=IntPtr.Zero)
            {
                if(result.Count>=10000)throw new Fault("MODEL_SIZE","A project contains too many primary peak entries.");
                IntPtr item=next(document.Handle,ref position),entry=Marshal.ReadIntPtr(item,0x60);
                Marshal.AddRef(entry);result.Add(entry);
            }
            return result;
        }
        internal static Dictionary<string,object> Capture(IDataSpace data)
        {
            var axes=new DataAxesClass();var space=new SpaceAxesClass();
            try
            {
                data.GetDataAxes(axes);data.GetSpaceAxes(space);int count=axes.GetSize();
                if(count<1||count>10000000)throw new Fault("DATA_SIZE","A spectrum has an unsupported point count.");
                double[] intensity=new double[count];data.GetData(axes,out intensity[0],ref count);
                if(count!=intensity.Length)throw new Fault("DATA_SIZE","A native spectrum read returned an unexpected size.");
                int dimensions;space.GetNAxes(out dimensions);var descriptions=new List<Dictionary<string,object>>();var coordinates=new List<double[]>();
                for(int axis=0;axis<dimensions;axis++)
                {
                    string label,symbol,unit;sbyte linear;double start,step;int size;DSAxisType type;
                    space.GetAxisInfo(axis,out label,out symbol,out unit,out linear,out start,out step,out size,out type);
                    if(size<1||size>10000000)throw new Fault("DATA_SIZE","A spectrum axis has an unsupported size.");
                    descriptions.Add(J.Obj("label",label,"symbol",symbol,"unit",unit,"linear",linear,"start",start,"increment",step,"size",size,"type",(int)type));
                    var values=new double[size];for(int point=0;point<size;point++)space.GetAxisPoint(axis,point,out values[point]);coordinates.Add(values);
                }
                double sourceEnergy=0;try{sourceEnergy=Convert.ToDouble(data.GetProperty(DSPropSet.STDPROPSET,DSPropID.DS_SOPROPID_ENERGY));}catch(COMException){}
                return J.Obj("intensity",intensity,"axes",descriptions,"coordinates",coordinates,"source_energy",sourceEnergy,
                    "binding_energy",dimensions==1&&Convert.ToString(descriptions[0]["unit"])=="eV"&&sourceEnergy>0?EnergyConversions.KineticToBinding(data,coordinates[0]):null);
            }
            finally{J.Release(axes);J.Release(space);}
        }
        internal static byte[] ReadStream(string path,params string[] names)
        {
            var storages=new List<IntPtr>();IntPtr root=IntPtr.Zero,stream=IntPtr.Zero;object streamObject=null;
            try
            {
                Marshal.ThrowExceptionForHR(StgOpenStorage(path,IntPtr.Zero,0x20,IntPtr.Zero,0,out root));storages.Add(root);
                IntPtr current=root;
                for(int i=0;i<names.Length-1;i++)
                {
                    IntPtr child;Marshal.ThrowExceptionForHR(Slot<OpenStorage>(current,6)(current,names[i],IntPtr.Zero,0x10,IntPtr.Zero,0,out child));
                    storages.Add(child);current=child;
                }
                Marshal.ThrowExceptionForHR(Slot<OpenStream>(current,4)(current,names[names.Length-1],IntPtr.Zero,0x10,0,out stream));
                streamObject=Marshal.GetObjectForIUnknown(stream);var readable=(System.Runtime.InteropServices.ComTypes.IStream)streamObject;
                System.Runtime.InteropServices.ComTypes.STATSTG stat;readable.Stat(out stat,1);
                if(stat.cbSize<0||stat.cbSize>64*1024*1024)throw new Fault("PROJECT_SIZE","A project metadata stream exceeds 64 MiB.");
                byte[] result=new byte[(int)stat.cbSize];IntPtr count=Marshal.AllocCoTaskMem(4);
                try
                {
                    readable.Read(result,result.Length,count);
                    if(Marshal.ReadInt32(count)!=result.Length)throw new Fault("INVALID_PROJECT","A project metadata stream is truncated.");
                    return result;
                }
                finally{Marshal.FreeCoTaskMem(count);}
            }
            finally
            {
                if(streamObject!=null)Marshal.ReleaseComObject(streamObject);if(stream!=IntPtr.Zero)Marshal.Release(stream);
                foreach(IntPtr storage in storages.AsEnumerable().Reverse())Marshal.Release(storage);
            }
        }
        static void WriteStream(string path,byte[] data,params string[] names)
        {
            var storages=new List<IntPtr>();IntPtr root=IntPtr.Zero,stream=IntPtr.Zero;object streamObject=null;
            try
            {
                Marshal.ThrowExceptionForHR(StgOpenStorage(path,IntPtr.Zero,0x12,IntPtr.Zero,0,out root));storages.Add(root);
                IntPtr current=root;
                for(int i=0;i<names.Length-1;i++)
                {
                    IntPtr child;Marshal.ThrowExceptionForHR(Slot<OpenStorage>(current,6)(current,names[i],IntPtr.Zero,0x12,IntPtr.Zero,0,out child));
                    storages.Add(child);current=child;
                }
                Marshal.ThrowExceptionForHR(Slot<CreateStream>(current,3)(current,names[names.Length-1],0x1012,0,0,out stream));
                streamObject=Marshal.GetObjectForIUnknown(stream);var writable=(System.Runtime.InteropServices.ComTypes.IStream)streamObject;
                IntPtr count=Marshal.AllocCoTaskMem(4);
                try{writable.Write(data,data.Length,count);if(Marshal.ReadInt32(count)!=data.Length)throw new IOException("Native storage stream write was incomplete.");}
                finally{Marshal.FreeCoTaskMem(count);}
                writable.Commit(0);
                foreach(IntPtr storage in storages.AsEnumerable().Reverse())Marshal.ThrowExceptionForHR(Slot<CommitStorage>(storage,9)(storage,0));
            }
            finally
            {
                if(streamObject!=null)Marshal.ReleaseComObject(streamObject);if(stream!=IntPtr.Zero)Marshal.Release(stream);
                foreach(IntPtr storage in storages.AsEnumerable().Reverse())Marshal.Release(storage);
            }
        }
        internal static Dictionary<int,List<Dictionary<string,object>>> PrimarySnapshots(NativeProcessingProject project)
        {
            var result=new Dictionary<int,List<Dictionary<string,object>>>();var identities=new List<IntPtr>();var entries=PrimaryEntries(project.Document);
            try
            {
                foreach(var spectrum in project.Spectra)
                {
                    IntPtr raw=Marshal.GetComInterfaceForObject(spectrum.Data,typeof(IDataSpace));
                    try{identities.Add(Identity(raw));}finally{Marshal.Release(raw);}
                }
                foreach(IntPtr entry in entries)
                {
                    IntPtr data=IntPtr.Zero,identity=IntPtr.Zero;
                    try
                    {
                        Marshal.ThrowExceptionForHR(Slot<PointerGetter>(entry,12)(entry,out data));
                        int index=-1;if(data!=IntPtr.Zero){identity=Identity(data);index=identities.IndexOf(identity);}
                        if(!result.ContainsKey(index))result[index]=new List<Dictionary<string,object>>();
                        result[index].Add(LivePeakTable.PrimarySnapshot(entry));
                    }
                    finally{if(identity!=IntPtr.Zero)Marshal.Release(identity);if(data!=IntPtr.Zero)Marshal.Release(data);}
                }
                return result;
            }
            finally{foreach(IntPtr identity in identities)Marshal.Release(identity);foreach(IntPtr entry in entries)Marshal.Release(entry);}
        }
        internal static void ReplaceModel(NativeProcessingProject project,int index,string table)
        {
            IDataSpace data=project.Spectra[index].Data;
            var oldPeaks=project.Document.PeaksFor(data);var parents=new List<IntPtr>();var identities=new HashSet<long>();
            try
            {
                if(table==null) {
                    // A visual spectrum still owns its primary chemical entries
                    // when its fitted components are cleared. Removing those
                    // entries as well leaves the live grid with dangling model
                    // associations. Use the vendor's fitted-only removal here.
                    var removeFitted=Bind<RemovePrimary>("?RemoveFittedPeak@CProcessGridDoc@@QEAAHPEAUIFittedPeak@@H@Z");
                    foreach(var peak in oldPeaks) {
                        IntPtr pointer=Marshal.GetComInterfaceForObject(peak,typeof(IAvFittedPeak));
                        try{if(removeFitted(project.Document.Handle,pointer,0)!=1)throw new Fault("NATIVE_PROJECT_FAILED","Avantage could not clear an owned fitted component.");}
                        finally{Marshal.Release(pointer);}
                    }
                    return;
                }
                foreach(var peak in oldPeaks)
                {
                    IntPtr parent=IntPtr.Zero,identity=IntPtr.Zero;
                    try
                    {
                        peak.GetPeakEntry(out parent);
                        if(parent==IntPtr.Zero)throw new Fault("INVALID_PROJECT","An existing fitted peak has no primary entry.");
                        identity=Identity(parent);
                        if(identities.Add(identity.ToInt64())){parents.Add(parent);parent=IntPtr.Zero;}
                    }
                    finally{if(identity!=IntPtr.Zero)Marshal.Release(identity);if(parent!=IntPtr.Zero)Marshal.Release(parent);}
                }
                var remove=Bind<RemovePrimary>("?RemovePeak@CProcessGridDoc@@QEAAHPEAUIPeakTableEntry@@H@Z");
                foreach(IntPtr parent in parents)
                    if(remove(project.Document.Handle,parent,0)!=1)throw new Fault("NATIVE_PROJECT_FAILED","Avantage could not remove an owned fitted peak entry.");
            }
            finally{foreach(IntPtr parent in parents)Marshal.Release(parent);foreach(var peak in oldPeaks)Marshal.ReleaseComObject(peak);}
            if(table==null)return;
            List<Dictionary<string,object>> importedSnapshot=null;
            using(var imported=new NativePeakTablePersistence.Document(data))
            {
                imported.Load(table);var peaks=imported.Peaks();var primary=PrimaryEntries(imported);
                var snapshot=NativePeakTablePersistence.ReadPeaks(peaks);
                importedSnapshot=snapshot;
                IntPtr target=Marshal.GetComInterfaceForObject(data,typeof(IDataSpace));
                try
                {
                    var addPrimary=Bind<AddPrimary>("?AddPeak@CProcessGridDoc@@QEAAHPEAUIPeakTableEntry@@@Z");
                    var addFitted=Bind<AddFitted>("?AddFittedPeak@CProcessGridDoc@@QEAAHPEAUIFittedPeak@@PEAUIDataSpace@@@Z");
                    foreach(IntPtr entry in primary)
                    {
                        Marshal.ThrowExceptionForHR(Slot<PointerSetter>(entry,11)(entry,target));
                        if(addPrimary(project.Document.Handle,entry)!=1)throw new Fault("NATIVE_PROJECT_FAILED","Avantage could not add an owned primary peak entry.");
                    }
                    foreach(var peak in peaks)
                    {
                        peak.SetDataSpace(target);IntPtr fitted=Marshal.GetComInterfaceForObject(peak,typeof(IAvFittedPeak));
                        try{if(addFitted(project.Document.Handle,fitted,target)!=1)throw new Fault("NATIVE_PROJECT_FAILED","Avantage could not add an owned fitted peak.");}
                        finally{Marshal.Release(fitted);}
                    }
                }
                finally{Marshal.Release(target);foreach(IntPtr entry in primary)Marshal.Release(entry);foreach(var peak in peaks)Marshal.ReleaseComObject(peak);}
            }
            var attached=project.Document.PeaksFor(data);
            // Native SetDataSpace and temporary document destruction clear fit
            // constraints. Restore them after both operations have completed.
            try {RestoreConstraints(attached,importedSnapshot);}
            finally {foreach(var peak in attached)Marshal.ReleaseComObject(peak);}
        }
        static void RestoreConstraints(IList<IAvFittedPeak> peaks,IList<Dictionary<string,object>> snapshot)
        {
            var pointers=new List<IntPtr>();
            try {
                foreach(var peak in peaks)pointers.Add(Marshal.GetComInterfaceForObject(peak,typeof(IAvFittedPeak)));
                for(int i=0;i<peaks.Count;i++)foreach(var saved in (IEnumerable<Dictionary<string,object>>)snapshot[i]["constraints"]) {
                    var constraint=(IAvPeakConstraint)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("68F7DAF5-B53B-11D2-988E-00C04F8EBE0F")));
                    IntPtr raw=IntPtr.Zero;
                    try {
                        IntPtr reference=IntPtr.Zero;
                        if(saved["reference_peak_index"]!=null) {
                            int target=Convert.ToInt32(saved["reference_peak_index"]);
                            if(target<0||target>=peaks.Count)throw new Fault("EXTERNAL_CONSTRAINT","A fitted component references a peak outside its target region.");
                            reference=pointers[target];
                        }
                        constraint.SetParameters(J.Int(saved,"type",0),J.Num(saved,"value1"),J.Num(saved,"value2"),J.Num(saved,"value3"),reference,J.Int(saved,"operator",0));
                        raw=Marshal.GetComInterfaceForObject(constraint,typeof(IAvPeakConstraint));
                        peaks[i].CreateInitialPeakFitConstraint(J.Int(saved,"attribute",0),raw);
                    } finally {if(raw!=IntPtr.Zero)Marshal.Release(raw);J.Release(constraint);}
                }
            } finally {foreach(IntPtr pointer in pointers)Marshal.Release(pointer);}
        }
    }
}
