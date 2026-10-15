using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using VGDATASTORELib;

namespace AvantageMcp
{
    /// <summary>
    /// Exports a native region entry and its fitted children. Avantage's live table
    /// importer selects region entries, so an ordinary fitted-only VGT is insufficient.
    /// All objects added to the export document are private clones; caller objects
    /// and their links remain unchanged.
    /// </summary>
    public static partial class LivePeakTable
    {
        static readonly Guid EntryIid = new Guid("010DE959-68E5-11D2-851A-006097C81436");
        [DllImport("kernel32.dll", CharSet=CharSet.Ansi, ExactSpelling=true)] static extern IntPtr GetProcAddress(IntPtr module,string name);
        [DllImport("ole32.dll",CharSet=CharSet.Unicode)] static extern int StgOpenStorage(string path,IntPtr priority,uint mode,IntPtr exclude,uint reserved,out IntPtr storage);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int OpenStorage(IntPtr self,[MarshalAs(UnmanagedType.LPWStr)] string name,IntPtr priority,uint mode,IntPtr exclude,uint reserved,out IntPtr child);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CommitStorage(IntPtr self,uint flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void CollectionOperation(IntPtr collection);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SaveCollection(IntPtr self,IntPtr collection,IntPtr storage);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int AddPrimary(IntPtr self,IntPtr peak);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CountPrimary(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr FirstPrimary(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr NextPrimary(IntPtr self,ref IntPtr position);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int PointerSetter(IntPtr self,IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int PointerGetter(IntPtr self,out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int StringSetter(IntPtr self,[MarshalAs(UnmanagedType.BStr)] string value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int StringGetter(IntPtr self,[MarshalAs(UnmanagedType.BStr)] out string value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int IntegerSetter(IntPtr self,int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int IntegerGetter(IntPtr self,out int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ByteSetter(IntPtr self,sbyte value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ByteGetter(IntPtr self,out sbyte value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DoubleSetter(IntPtr self,double value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DoubleGetter(IntPtr self,out double value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int AttributesSetter(IntPtr self,double start,double centre,double end,double fwhm);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int AttributesGetter(IntPtr self,out double start,out double centre,out double end,out double fwhm);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SubParametersSetter(IntPtr self,double height,double area,double tx,double normalised);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SubParametersGetter(IntPtr self,out double height,out double area,out double tx,out double normalised);

        static T Bind<T>(string name) where T:class
        {
            IntPtr address=GetProcAddress(NativeGridTables.VerifiedModule,name);
            if(address==IntPtr.Zero)throw new EntryPointNotFoundException(name);
            return Marshal.GetDelegateForFunctionPointer(address,typeof(T))as T;
        }
        static T Slot<T>(IntPtr value,int index) where T:class
        {
            if(value==IntPtr.Zero)throw new ArgumentNullException("value");
            return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(value),index*IntPtr.Size),typeof(T))as T;
        }
        static void SetPointer(IntPtr value,int slot,IntPtr pointer){Marshal.ThrowExceptionForHR(Slot<PointerSetter>(value,slot)(value,pointer));}
        static void SetInteger(IntPtr value,int slot,int parameter){Marshal.ThrowExceptionForHR(Slot<IntegerSetter>(value,slot)(value,parameter));}
        static int ReadInteger(IntPtr value,int slot){int result;Marshal.ThrowExceptionForHR(Slot<IntegerGetter>(value,slot)(value,out result));return result;}
        static double ReadDouble(IntPtr value,int slot){double result;Marshal.ThrowExceptionForHR(Slot<DoubleGetter>(value,slot)(value,out result));return result;}
        static string ReadString(IntPtr value,int slot){string result;Marshal.ThrowExceptionForHR(Slot<StringGetter>(value,slot)(value,out result));return result;}
        static sbyte ReadByte(IntPtr value,int slot){sbyte result;Marshal.ThrowExceptionForHR(Slot<ByteGetter>(value,slot)(value,out result));return result;}
        static IntPtr EntryPointer(object value)
        {
            IntPtr unknown=Marshal.GetIUnknownForObject(value),entry=IntPtr.Zero;
            try{Guid iid=EntryIid;Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown,ref iid,out entry));return entry;}
            finally{Marshal.Release(unknown);}
        }
        static IntPtr Identity(IntPtr value)
        {
            IntPtr result;Guid iid=new Guid("00000000-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(value,ref iid,out result));return result;
        }
        static void SaveCompleteCollections(IntPtr document,string path)
        {
            // SavePeakTable's UI route filters primary entries by visible grid
            // cells. This owned document has no UI. Serialize its complete native
            // collections using the same vendor routines and constructor-wired
            // cross-reference links, instead of inventing cells or OLE records.
            int[] offsets={0x240,0x318,0x3c0,0x498,0x590,0x650};
            foreach(int offset in offsets)
            {
                IntPtr collection=IntPtr.Add(document,offset);
                Slot<CollectionOperation>(collection,17)(collection);
            }
            int[] builders={0x8fc70,0x8f8f0,0x90950,0x90250,0x907b0,0x90e40};
            for(int i=0;i<offsets.Length;i++)
            {
                var build=(CollectionOperation)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(NativeGridTables.VerifiedModule,builders[i]),typeof(CollectionOperation));
                build(IntPtr.Add(document,offsets[i]));
            }
            string[] names={"Backgrounds","Peaks","Fitted Peaks","Procedural Peaks"};
            int[] savedOffsets={0x318,0x3c0,0x498,0x650};
            IntPtr root=IntPtr.Zero;
            try
            {
                Marshal.ThrowExceptionForHR(StgOpenStorage(path,IntPtr.Zero,0x12,IntPtr.Zero,0,out root));
                var open=Slot<OpenStorage>(root,6);var save=Bind<SaveCollection>("?SaveCollection@CProcessGridDoc@@IEAAXPEAVCCollection@@PEAUIStorage@@@Z");
                for(int i=0;i<names.Length;i++)
                {
                    IntPtr child=IntPtr.Zero;
                    try{Marshal.ThrowExceptionForHR(open(root,names[i],IntPtr.Zero,0x12,IntPtr.Zero,0,out child));save(document,IntPtr.Add(document,savedOffsets[i]),child);Marshal.ThrowExceptionForHR(Slot<CommitStorage>(child,9)(child,0));}
                    finally{if(child!=IntPtr.Zero)Marshal.Release(child);}
                }
                Marshal.ThrowExceptionForHR(Slot<CommitStorage>(root,9)(root,0));
            }
            finally{if(root!=IntPtr.Zero)Marshal.Release(root);}
        }
        internal static Dictionary<string,object> PrimarySnapshot(IntPtr entry)
        {
            double start,centre,end,width,height,area,tx,normalised;
            Marshal.ThrowExceptionForHR(Slot<AttributesGetter>(entry,6)(entry,out start,out centre,out end,out width));
            Marshal.ThrowExceptionForHR(Slot<SubParametersGetter>(entry,8)(entry,out height,out area,out tx,out normalised));
            var result=J.Obj("name",ReadString(entry,14),"start_kinetic_ev",start,"centre_kinetic_ev",centre,"end_kinetic_ev",end,"fwhm_ev",width,
                "height",height,"area",area,"transmission_function",tx,"normalised_area",normalised,"native_peak_type",ReadInteger(entry,34),
                "quantify",ReadByte(entry,36)!=0,"sensitivity_factor",ReadDouble(entry,38),"energy_compensated_sf",ReadDouble(entry,40),
                "atomic_percent",ReadDouble(entry,48),"atomic_weight_percent",ReadDouble(entry,50),"atomic_weight",null,"atomic_weight_persisted",false,
                "ptop_height",ReadDouble(entry,26),"ptop_normalised_height",ReadDouble(entry,28),"ptop_atomic_percent",ReadDouble(entry,30),
                "is_differentiated",ReadByte(entry,32)!=0,"library_name",ReadString(entry,60),"sensitivity_factor_edited",ReadInteger(entry,62),
                "name_edited",ReadInteger(entry,64),"data_valid",ReadInteger(entry,66),"simple_background_height_ratio",ReadDouble(entry,71),
                "simple_background_area_ratio",ReadDouble(entry,73),"atomic_std_deviation",ReadDouble(entry,76),"area_std_deviation",ReadDouble(entry,78));
            // This native build's IPersistStream.Save/Load omits the atomic-weight
            // field. CloneFrom also uses that stream and leaves it uninitialized
            // on reused allocations. GetAtomicWeight has the correct double ABI,
            // but its value is not reliable for a file snapshot; do not compare
            // heap bytes or invent an atomic mass.
            // Avantage uses negative infinity for unavailable uncertainties. JSON
            // has no non-finite numbers; preserve the native value and report null.
            foreach(string key in result.Keys.ToArray())
                if(result[key] is double&&(Double.IsNaN((double)result[key])||Double.IsInfinity((double)result[key])))result[key]=null;
            return result;
        }

        public static Dictionary<string,object> Save(string path,SpectrumSession session,IntPtr templatePrimary)
        {
            if(session==null)throw new ArgumentNullException("session");
            var model=session.FitState as NativePeakModel;
            if(model==null)throw new Fault("MODEL_NOT_DEFINED","Define a fitted peak model before publishing it to Avantage.");
            return Save(path,session.Data,model,session.BackgroundState,templatePrimary);
        }
        public static Dictionary<string,object> Save(string path,IDataSpace source,NativePeakModel model,NativeBackground background,IntPtr templatePrimary)
        {
            path=Path.GetFullPath(path);
            if(File.Exists(path))throw new IOException("Native live peak-table output already exists.");
            if(source==null||model==null||model.Peaks.Count==0||model.Peaks.Count>100)throw new Fault("MODEL_SIZE","A live region requires a spectrum and 1–100 fitted peaks.");
            if(background==null||background.Background==null)throw new Fault("BACKGROUND_REQUIRED","A live region requires an explicit native background.");
            // Enforces the private document ABI hash before allocating any document.
            IntPtr verified=NativeGridTables.VerifiedModule;
            var original=model.Peaks.Select(p=>p.Native).ToArray();
            var expected=NativePeakTablePersistence.ReadPeaks(original);
            foreach(var saved in expected)
                if(saved["background"]==null||!NativePeakTablePersistence.Equivalent(expected[0]["background"],saved["background"]))
                    throw new Fault("MIXED_BACKGROUNDS","A live region must have one explicit shared background.");
            object primaryObject=null;IntPtr primary=IntPtr.Zero,data=IntPtr.Zero,bgPointer=IntPtr.Zero;
            Dictionary<string,object> templateBefore=null;
            IAvantageBackground clonedBackground=null;var clones=new List<IAvFittedPeak>();var clonePointers=new List<IntPtr>();
            try
            {
                primaryObject=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("010DE95A-68E5-11D2-851A-006097C81436")));
                primary=EntryPointer(primaryObject);
                if(templatePrimary!=IntPtr.Zero)
                {
                    Guid iid=EntryIid;IntPtr valid=IntPtr.Zero;
                    try{Marshal.ThrowExceptionForHR(Marshal.QueryInterface(templatePrimary,ref iid,out valid));templateBefore=PrimarySnapshot(valid);SetPointer(primary,67,valid);}
                    finally{if(valid!=IntPtr.Zero)Marshal.Release(valid);}
                }
                // The vendor clone also copies the source's table-owner pointer.
                // Keep this export independent of the original document/table.
                SetPointer(primary,57,IntPtr.Zero);
                background.Background.CloneBackground(out clonedBackground);
                bgPointer=Marshal.GetComInterfaceForObject(clonedBackground,typeof(IAvantageBackground));
                data=Marshal.GetComInterfaceForObject(source,typeof(IDataSpace));
                SetPointer(primary,11,data);SetPointer(primary,9,bgPointer);SetInteger(primary,33,1);
                double start,end;clonedBackground.GetStart(out start);clonedBackground.GetEnd(out end);
                if(templatePrimary==IntPtr.Zero)
                {
                    string label;
                    try{label=Convert.ToString(source.GetProperty(DSPropSet.STDPROPSET,DSPropID.DS_GEPROPID_SOURCE_FILE));label=Path.GetFileNameWithoutExtension(label);}
                    catch{label="MCP fitted region";}
                    if(String.IsNullOrWhiteSpace(label))label="MCP fitted region";
                    Marshal.ThrowExceptionForHR(Slot<StringSetter>(primary,13)(primary,label));
                    Marshal.ThrowExceptionForHR(Slot<ByteSetter>(primary,35)(primary,0));
                    Marshal.ThrowExceptionForHR(Slot<DoubleSetter>(primary,51)(primary,0));
                    var strongest=expected.OrderByDescending(p=>Convert.ToDouble(p["height"])).First();
                    Marshal.ThrowExceptionForHR(Slot<AttributesSetter>(primary,5)(primary,start,Convert.ToDouble(strongest["centre_kinetic_ev"]),end,Convert.ToDouble(strongest["fwhm_ev"])));
                    Marshal.ThrowExceptionForHR(Slot<SubParametersSetter>(primary,7)(primary,Convert.ToDouble(strongest["height"]),expected.Sum(p=>Convert.ToDouble(p["area"])),0,0));
                }
                else
                {
                    // Keep the template's chemical identity, RSF and quantification
                    // settings while updating its integration bounds to the model.
                    Marshal.ThrowExceptionForHR(Slot<DoubleSetter>(primary,15)(primary,start));
                    Marshal.ThrowExceptionForHR(Slot<DoubleSetter>(primary,19)(primary,end));
                }
                foreach(var saved in expected)
                {
                    var clone=(IAvFittedPeak)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("B90B1A14-A54F-11D2-852A-00104B053B4C")));
                    clones.Add(clone);clone.SetDataSpace(data);clone.SetBackground(bgPointer);clone.SetPeakEntry(primary);
                    clone.SetAsymParams(J.Num(saved,"area"),J.Num(saved,"area_ratio"),J.Num(saved,"centre_kinetic_ev"),J.Num(saved,"height"),J.Num(saved,"height_ratio"),J.Num(saved,"fwhm_ev"),J.Num(saved,"gaussian_lorentz"),J.Num(saved,"tail_mix"),J.Num(saved,"tail_height"),J.Num(saved,"tail_exponential"));
                    clone.SetName(J.Str(saved,"label"));clone.SetGLCombineMethod(J.Int(saved,"native_gl_combine_method",0));
                    clonePointers.Add(Marshal.GetComInterfaceForObject(clone,typeof(IAvFittedPeak)));
                }
                for(int index=0;index<expected.Count;index++)
                    foreach(var saved in (IEnumerable<Dictionary<string,object>>)expected[index]["constraints"])
                    {
                        var constraint=(IAvPeakConstraint)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("68F7DAF5-B53B-11D2-988E-00C04F8EBE0F")));
                        IntPtr raw=IntPtr.Zero;
                        try
                        {
                            IntPtr reference=IntPtr.Zero;
                            if(saved["reference_peak_index"]!=null)
                            {
                                int target=Convert.ToInt32(saved["reference_peak_index"]);
                                if(target<0||target>=clonePointers.Count)throw new Fault("UNSUPPORTED_CONSTRAINT","A live region constraint references a peak outside this model.");
                                reference=clonePointers[target];
                            }
                            constraint.SetParameters(J.Int(saved,"type",0),J.Num(saved,"value1"),J.Num(saved,"value2"),J.Num(saved,"value3"),reference,J.Int(saved,"operator",0));
                            raw=Marshal.GetComInterfaceForObject(constraint,typeof(IAvPeakConstraint));
                            clones[index].CreateInitialPeakFitConstraint(J.Int(saved,"attribute",0),raw);
                        }
                        finally{if(raw!=IntPtr.Zero)Marshal.Release(raw);J.Release(constraint);}
                    }
                if(!NativePeakTablePersistence.Equivalent(expected,NativePeakTablePersistence.ReadPeaks(clones)))
                    throw new Fault("CLONE_MISMATCH","Native region cloning changed a peak, background or constraint.");
                var primaryExpected=PrimarySnapshot(primary);
                using(var document=new NativePeakTablePersistence.Document(source))
                {
                    if(Bind<AddPrimary>("?AddPeak@CProcessGridDoc@@QEAAHPEAUIPeakTableEntry@@@Z")(document.Handle,primary)!=1)
                        throw new Fault("NATIVE_TABLE_FAILED","Avantage refused the primary region entry.");
                    foreach(var clone in clones)document.Add(clone);
                    if(document.Count!=clones.Count||Bind<CountPrimary>("?GetNumPeaks@CProcessGridDoc@@QEAAHXZ")(document.Handle)!=1)
                        throw new Fault("NATIVE_TABLE_FAILED","Avantage did not retain the complete region and fitted peaks.");
                    document.Save(path);
                    SaveCompleteCollections(document.Handle,path);
                }
                var result=Inspect(path,source);
                if(!NativePeakTablePersistence.PersistedEquivalent(expected,result["peaks"])||!NativePeakTablePersistence.Equivalent(primaryExpected,result["primary"]))
                    throw new Fault("ROUNDTRIP_MISMATCH","The native grouped table did not preserve peak parameters, constraints or region metadata.");
                if(!NativePeakTablePersistence.Equivalent(expected,NativePeakTablePersistence.ReadPeaks(original)))
                    throw new Fault("SOURCE_CHANGED","Native table export unexpectedly changed the caller's model.");
                if(templatePrimary!=IntPtr.Zero)
                {
                    Guid iid=EntryIid;IntPtr valid=IntPtr.Zero;
                    try{Marshal.ThrowExceptionForHR(Marshal.QueryInterface(templatePrimary,ref iid,out valid));if(!NativePeakTablePersistence.Equivalent(templateBefore,PrimarySnapshot(valid)))throw new Fault("SOURCE_CHANGED","Native table export unexpectedly changed the caller's region metadata.");}
                    finally{if(valid!=IntPtr.Zero)Marshal.Release(valid);}
                }
                result["roundtrip_verified"]=true;result["runtime_display_ratios_persisted"]=false;result["template_metadata_preserved"]=templatePrimary!=IntPtr.Zero;return result;
            }
            finally
            {
                foreach(var raw in clonePointers)Marshal.Release(raw);
                foreach(var clone in clones)J.Release(clone);
                if(bgPointer!=IntPtr.Zero)Marshal.Release(bgPointer);if(clonedBackground!=null)J.Release(clonedBackground);
                if(data!=IntPtr.Zero)Marshal.Release(data);if(primary!=IntPtr.Zero)Marshal.Release(primary);if(primaryObject!=null)J.Release(primaryObject);
            }
        }
        public static Dictionary<string,object> Inspect(string path,IDataSpace source)
        {
            // Existing validation checks the compound-storage signature and required
            // Avantage sections before the native loader is invoked.
            NativePeakTablePersistence.Inspect(path,source);
            using(var document=new NativePeakTablePersistence.Document(source))
            {
                document.Load(path);
                int count=Bind<CountPrimary>("?GetNumPeaks@CProcessGridDoc@@QEAAHXZ")(document.Handle);
                if(count!=1)throw new Fault("INVALID_LIVE_TABLE","A live table must contain exactly one primary region entry; Avantage loaded "+count+".");
                IntPtr position=Bind<FirstPrimary>("?GetFirstPeak@CProcessGridDoc@@QEBAPEAU__POSITION@@XZ")(document.Handle);
                IntPtr item=Bind<NextPrimary>("?GetNextPeak@CProcessGridDoc@@QEAAAEAVCPeakItem@@AEAPEAU__POSITION@@@Z")(document.Handle,ref position);
                // CPeakItem's primary entry member is verified for the ProcessGrid hash.
                IntPtr primary=Marshal.ReadIntPtr(item,0x60),identity=Identity(primary);
                var peaks=document.Peaks();
                try
                {
                    if(peaks.Count!=document.Count||peaks.Count<1)throw new Fault("INVALID_LIVE_TABLE","The live table's fitted spectrum association is incomplete.");
                    foreach(var peak in peaks)
                    {
                        IntPtr parent=IntPtr.Zero,parentIdentity=IntPtr.Zero;
                        try
                        {
                            peak.GetPeakEntry(out parent);if(parent==IntPtr.Zero)throw new Fault("INVALID_LIVE_TABLE","A fitted peak has no primary region entry.");
                            parentIdentity=Identity(parent);if(parentIdentity!=identity)throw new Fault("INVALID_LIVE_TABLE","A fitted peak links to a different region entry.");
                        }
                        finally{if(parentIdentity!=IntPtr.Zero)Marshal.Release(parentIdentity);if(parent!=IntPtr.Zero)Marshal.Release(parent);}
                    }
                    return J.Obj("path",Path.GetFullPath(path),"primary_count",1,"primary",PrimarySnapshot(primary),"peak_count",peaks.Count,
                        "peaks",NativePeakTablePersistence.ReadPeaks(peaks),"bytes",new FileInfo(path).Length,"backend","Avantage grouped native VGT");
                }
                finally{Marshal.Release(identity);foreach(var peak in peaks)Marshal.ReleaseComObject(peak);}
            }
        }
    }
}
