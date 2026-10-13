using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using VGDATASTORELib;

namespace AvantageMcp
{
    /// <summary>Native .vgt serialization through an exclusively owned Avantage document.</summary>
    public static class NativePeakTablePersistence
    {
        [DllImport("kernel32.dll", CharSet=CharSet.Ansi, ExactSpelling=true)] static extern IntPtr GetProcAddress(IntPtr module,string name);
        [DllImport("ole32.dll")] static extern int OleInitialize(IntPtr reserved);
        [DllImport("ole32.dll")] static extern void OleUninitialize();
        [DllImport("ole32.dll",CharSet=CharSet.Unicode)] static extern int StgOpenStorage(string path,IntPtr priority,uint mode,IntPtr exclude,uint reserved,out IntPtr storage);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int OpenStorage(IntPtr self,[MarshalAs(UnmanagedType.LPWStr)] string name,IntPtr priority,uint mode,IntPtr exclude,uint reserved,out IntPtr child);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Factory();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int AddData(IntPtr self,IntPtr data,int state);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int AddPeak(IntPtr self,IntPtr peak,IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CountPeaks(IntPtr self,IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr FirstPeak(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr NextPeak(IntPtr self,ref IntPtr position);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void FileOperation(IntPtr self,[MarshalAs(UnmanagedType.LPWStr)] string path);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Destroy(IntPtr self,uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetInteger(IntPtr self,out int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDouble(IntPtr self,out double value);
        static T Bind<T>(string name) where T:class
        {
            IntPtr address=GetProcAddress(NativeGridTables.VerifiedModule,name);
            if(address==IntPtr.Zero)throw new EntryPointNotFoundException(name);
            return Marshal.GetDelegateForFunctionPointer(address,typeof(T))as T;
        }
        sealed class Document : IDisposable
        {
            public IntPtr Handle,Data;
            bool initialized;
            readonly int thread=Thread.CurrentThread.ManagedThreadId;
            public Document(IDataSpace source)
            {
                if(Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA)throw new InvalidOperationException("Native peak-table persistence requires STA.");
                Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));initialized=true;
                try
                {
                    Handle=Bind<Factory>("?CreateObject@CProcessGridDoc@@SAPEAVCObject@@XZ")();
                    if(Handle==IntPtr.Zero)throw new OutOfMemoryException();
                    Data=Marshal.GetComInterfaceForObject(source,typeof(IDataSpace));
                    if(Bind<AddData>("?AddDataSpace@CProcessGridDoc@@QEAAHPEAUIDataSpace@@W4eDataState@@@Z")(Handle,Data,0)!=1)
                        throw new InvalidOperationException("Avantage refused the document spectrum.");
                }
                catch{Dispose();throw;}
            }
            public int Count {get{return Bind<CountPeaks>("?NumFittedPeaks@CProcessGridDoc@@QEAAHPEAUIDataSpace@@@Z")(Handle,Data);}}
            public void Add(IAvFittedPeak peak)
            {
                IntPtr pointer=Marshal.GetComInterfaceForObject(peak,typeof(IAvFittedPeak));
                try{if(Bind<AddPeak>("?AddFittedPeak@CProcessGridDoc@@QEAAHPEAUIFittedPeak@@PEAUIDataSpace@@@Z")(Handle,pointer,Data)!=1)throw new InvalidOperationException("Avantage refused a fitted peak.");}
                finally{Marshal.Release(pointer);}
            }
            public void Save(string path){Bind<FileOperation>("?SavePeakTable@CProcessGridDoc@@UEAAXPEB_W@Z")(Handle,path);}
            public void Load(string path){Bind<FileOperation>("?LoadPeakTable@CProcessGridDoc@@UEAAXPEB_W@Z")(Handle,path);}
            public List<IAvFittedPeak> Peaks()
            {
                var result=new List<IAvFittedPeak>();
                IntPtr pos=Bind<FirstPeak>("?GetFirstFittedPeak@CProcessGridDoc@@QEBAPEAU__POSITION@@XZ")(Handle);
                var next=Bind<NextPeak>("?GetNextFittedPeak@CProcessGridDoc@@QEAAAEAVCFittedPeakItem@@AEAPEAU__POSITION@@@Z");
                while(pos!=IntPtr.Zero)
                {
                    if(result.Count>10000)throw new InvalidOperationException("Native peak table exceeds the supported size.");
                    IntPtr item=next(Handle,ref pos);
                    // CFittedPeakItem layout is covered by the ProcessGrid SHA256 gate.
                    IntPtr pointer=Marshal.ReadIntPtr(item,0x18);
                    result.Add((IAvFittedPeak)Marshal.GetObjectForIUnknown(pointer));
                }
                return result;
            }
            public void Dispose()
            {
                if(Thread.CurrentThread.ManagedThreadId!=thread)throw new InvalidOperationException("Dispose native document on its owner thread.");
                if(Handle!=IntPtr.Zero)
                {
                    var destroy=(Destroy)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(Handle),IntPtr.Size),typeof(Destroy));
                    destroy(Handle,1);Handle=IntPtr.Zero;
                }
                if(Data!=IntPtr.Zero){Marshal.Release(Data);Data=IntPtr.Zero;}
                if(initialized){OleUninitialize();initialized=false;}
            }
        }

        public static Dictionary<string,object> Export(string path,IDataSpace source,NativePeakModel model,NativeBackground background)
        {
            if(model==null||model.Peaks.Count==0)throw new InvalidOperationException("Define fitted peaks before exporting a native peak table.");
            if(background==null||background.Background==null)throw new InvalidOperationException("Define an explicit native background before exporting a native peak table.");
            // SetBackground resets native constraints, so never reattach existing backgrounds.
            IntPtr expectedBackground=Marshal.GetComInterfaceForObject(background.Background,typeof(IAvantageBackground));
            try
            {
                foreach(var peak in model.Peaks)
                {
                    IntPtr present;peak.Native.GetBackground(out present);
                    try
                    {
                        if(present==IntPtr.Zero)peak.Native.SetBackground(expectedBackground);
                        else if(present!=expectedBackground&&!NativePeakModel.EquivalentBackground(present,expectedBackground))throw new InvalidOperationException("The model contains a different background; update it explicitly before exporting.");
                    }
                    finally{if(present!=IntPtr.Zero)Marshal.Release(present);}
                }
            }
            finally{Marshal.Release(expectedBackground);}
            return Save(path,source,model.Peaks.Select(p=>p.Native).ToArray());
        }

        public static Dictionary<string,object> Save(string path,IDataSpace source,IList<IAvFittedPeak> peaks)
        {
            path=Path.GetFullPath(path);
            if(File.Exists(path))throw new IOException("Native peak-table output already exists.");
            if(source==null||peaks==null||peaks.Count==0)throw new ArgumentException("A spectrum and at least one fitted peak are required.");
            var expected=ReadPeaks(peaks);
            foreach(var item in expected)
                if(item["background"]==null)throw new InvalidOperationException("Every fitted peak requires an explicit native background.");
            using(var doc=new Document(source))
            {
                foreach(var peak in peaks)doc.Add(peak);
                if(doc.Count!=peaks.Count)throw new InvalidOperationException("Avantage did not retain all fitted peaks in its document.");
                doc.Save(path);
            }
            if(!File.Exists(path)||new FileInfo(path).Length==0)throw new IOException("Avantage did not create the peak-table file.");
            var verified=Inspect(path,source);
            if(!Equivalent(expected,verified["peaks"]))throw new InvalidOperationException("Native peak-table roundtrip did not preserve every peak, background and constraint.");
            verified["roundtrip_verified"]=true;
            return verified;
        }

        public static Dictionary<string,object> Inspect(string path,IDataSpace source)
        {
            path=Path.GetFullPath(path);
            if(!File.Exists(path))throw new FileNotFoundException("Native peak table was not found.",path);
            byte[] signature=new byte[8];
            using(var stream=File.OpenRead(path))
                if(stream.Read(signature,0,8)!=8||!signature.SequenceEqual(new byte[]{0xd0,0xcf,0x11,0xe0,0xa1,0xb1,0x1a,0xe1}))
                    throw new InvalidDataException("The file is not an Avantage structured-storage peak table.");
            // Reject truncated containers and unrelated Office/OLE files before the
            // native loader, which can otherwise display an invisible error dialog.
            IntPtr storage=IntPtr.Zero;
            try {
                int hr=StgOpenStorage(path,IntPtr.Zero,0x20,IntPtr.Zero,0,out storage);
                if(hr!=0)throw new Fault("INVALID_PEAK_TABLE","The VGT structured-storage container is damaged or cannot be opened.");
                var open=(OpenStorage)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(storage),6*IntPtr.Size),typeof(OpenStorage));
                foreach(string name in new[]{"Peaks","Backgrounds","Fitted Peaks"}){
                    IntPtr part=IntPtr.Zero;try{if(open(storage,name,IntPtr.Zero,0x10,IntPtr.Zero,0,out part)!=0)throw new Fault("INVALID_PEAK_TABLE","The file is missing Avantage peak-table storage: "+name);}finally{if(part!=IntPtr.Zero)Marshal.Release(part);}
                }
            }finally{if(storage!=IntPtr.Zero)Marshal.Release(storage);}
            using(var doc=new Document(source))
            {
                doc.Load(path);
                var peaks=doc.Peaks();
                try
                {
                    if(peaks.Count!=doc.Count)throw new InvalidOperationException("Native peak-table spectrum association is inconsistent.");
                    return new Dictionary<string,object>{{"path",path},{"peak_count",peaks.Count},{"peaks",ReadPeaks(peaks)},
                        {"bytes",new FileInfo(path).Length},{"backend","Avantage CProcessGridDoc.SavePeakTable/LoadPeakTable"}};
                }
                finally{foreach(var peak in peaks)Marshal.ReleaseComObject(peak);}
            }
        }

        internal static NativePeakModel LoadModel(string path,SpectrumSession s,out NativeBackground background)
        {
            // Validate the container signature before calling the vendor loader.
            Inspect(path,s.Data);
            background=null;NativePeakModel model=null;IAvantageBackground nativeBackground=null;
            using(var doc=new Document(s.Data))
            {
                doc.Load(path);var peaks=doc.Peaks();
                try
                {
                    if(peaks.Count<1||peaks.Count>100)throw new Fault("MODEL_SIZE","Imported tables must contain 1–100 peaks.");
                    var saved=ReadPeaks(peaks);var first=saved[0]["background"] as Dictionary<string,object>;
                    if(first==null)throw new Fault("BACKGROUND_REQUIRED","Imported peaks require an explicit background.");
                    if(saved.Any(p=>!Equivalent(first,p["background"])))throw new Fault("MIXED_BACKGROUNDS","This MCP session supports one common background. The table contains different peak backgrounds.");
                    IntPtr raw;peaks[0].GetBackground(out raw);
                    try{nativeBackground=(IAvantageBackground)Marshal.GetObjectForIUnknown(raw);}finally{Marshal.Release(raw);}
                    background=NativeBackground.Adopt(s,nativeBackground,first);nativeBackground=null;
                    model=NativePeakModel.AdoptNative(s.Data,peaks,saved);
                    model.Info();model.Evaluate(s.X,false);
                    peaks.Clear();return model;
                }
                catch{if(background!=null){background.Dispose();background=null;}if(nativeBackground!=null)Marshal.ReleaseComObject(nativeBackground);throw;}
                finally{foreach(var peak in peaks)Marshal.ReleaseComObject(peak);}
            }
        }

        static List<Dictionary<string,object>> ReadPeaks(IList<IAvFittedPeak> peaks)
        {
            var pointers=new List<IntPtr>();
            try
            {
                foreach(var peak in peaks)pointers.Add(Marshal.GetComInterfaceForObject(peak,typeof(IAvFittedPeak)));
                var result=new List<Dictionary<string,object>>();
                for(int index=0;index<peaks.Count;index++)
                {
                    var peak=peaks[index];string name;double a,ar,c,h,hr,w,g,tm,th,te;int method;
                    peak.GetName(out name);peak.GetAsymParams(out a,out ar,out c,out h,out hr,out w,out g,out tm,out th,out te);peak.GetGLCombineMethod(out method);
                    var constraints=new List<Dictionary<string,object>>();
                    for(int attr=1;attr<=8;attr++)
                    {
                        IntPtr raw;peak.GetPeakFitConstraint(attr,out raw);if(raw==IntPtr.Zero)continue;
                        IAvPeakConstraint constraint=null;
                        try
                        {
                            constraint=(IAvPeakConstraint)Marshal.GetObjectForIUnknown(raw);
                            int type,op,active;double v1,v2,v3;IntPtr reference;
                            constraint.GetParameters(out type,out v1,out v2,out v3,out reference,out op);constraint.IsConstrained(out active);
                            try{if(active!=0)constraints.Add(new Dictionary<string,object>{{"attribute",attr},{"type",type},{"value1",v1},{"value2",v2},{"value3",v3},{"operator",op},{"reference_peak_index",reference==IntPtr.Zero?(object)null:pointers.IndexOf(reference)}});}
                            finally{if(reference!=IntPtr.Zero)Marshal.Release(reference);}
                        }
                        finally{if(constraint!=null)Marshal.ReleaseComObject(constraint);Marshal.Release(raw);}
                    }
                    object background=null;IntPtr bgRaw;peak.GetBackground(out bgRaw);
                    if(bgRaw!=IntPtr.Zero)
                    {
                        IAvantageBackground bg=null;
                        try
                        {
                            bg=(IAvantageBackground)Marshal.GetObjectForIUnknown(bgRaw);
                            double start,end,step;int mode,average,type;
                            bg.GetStart(out start);bg.GetEnd(out end);bg.GeteVgranularity(out step);bg.GetMode(out mode);bg.GetPointsToAverage(out average);bg.GetBackgroundType(out type);
                            string properties;bg.PropertiesToString(out properties);
                            background=new Dictionary<string,object>{{"type",type},{"start_kinetic_ev",start},{"end_kinetic_ev",end},{"mode",mode},{"points_to_average",average},{"step_ev",step},{"properties",properties},{"extensions",BackgroundExtensions(bgRaw)}};
                        }
                        finally{if(bg!=null)Marshal.ReleaseComObject(bg);Marshal.Release(bgRaw);}
                    }
                    result.Add(new Dictionary<string,object>{{"index",index},{"label",name},{"area",a},{"area_ratio",ar},{"centre_kinetic_ev",c},
                        {"height",h},{"height_ratio",hr},{"fwhm_ev",w},{"gaussian_lorentz",g},{"tail_mix",tm},{"tail_height",th},{"tail_exponential",te},
                        {"native_gl_combine_method",method},{"constraints",constraints},{"background",background}});
                }
                return result;
            }
            finally{foreach(var pointer in pointers)Marshal.Release(pointer);}
        }
        static Dictionary<string,object> BackgroundExtensions(IntPtr background)
        {
            var result=new Dictionary<string,object>();IntPtr extension;
            Guid iid=new Guid("C9D7ED54-5854-11D2-B66E-006097BE683A");
            if(Marshal.QueryInterface(background,ref iid,out extension)==0)
            {
                try{result["shirley_iterations"]=ReadInteger(extension,22);result["shirley_convergence"]=ReadDouble(extension,23);result["shirley_smooth"]=ReadInteger(extension,27);}
                finally{Marshal.Release(extension);}
            }
            iid=new Guid("C9D7ED56-5854-11D2-B66E-006097BE683A");
            if(Marshal.QueryInterface(background,ref iid,out extension)==0)
            {
                try{result["tougaard_y_offset"]=ReadDouble(extension,22);result["tougaard_type"]=ReadInteger(extension,23);result["tougaard_b"]=ReadDouble(extension,24);result["tougaard_c"]=ReadDouble(extension,25);result["tougaard_d"]=ReadDouble(extension,26);result["tougaard_energy_correction"]=ReadInteger(extension,27);}
                finally{Marshal.Release(extension);}
            }
            return result;
        }
        static int ReadInteger(IntPtr instance,int slot)
        {
            int value;var call=(GetInteger)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance),slot*IntPtr.Size),typeof(GetInteger));
            Marshal.ThrowExceptionForHR(call(instance,out value));return value;
        }
        static double ReadDouble(IntPtr instance,int slot)
        {
            double value;var call=(GetDouble)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance),slot*IntPtr.Size),typeof(GetDouble));
            Marshal.ThrowExceptionForHR(call(instance,out value));return value;
        }
        static bool Equivalent(object a,object b)
        {
            if(a==null||b==null)return a==b;
            var ad=a as IDictionary<string,object>;var bd=b as IDictionary<string,object>;
            if(ad!=null&&bd!=null)return ad.Count==bd.Count&&ad.All(pair=>bd.ContainsKey(pair.Key)&&Equivalent(pair.Value,bd[pair.Key]));
            var ae=a as System.Collections.IEnumerable;var be=b as System.Collections.IEnumerable;
            if(!(a is string)&&ae!=null&&be!=null)
            {
                var aa=ae.Cast<object>().ToArray();var bb=be.Cast<object>().ToArray();
                return aa.Length==bb.Length&&aa.Where((item,index)=>!Equivalent(item,bb[index])).Count()==0;
            }
            if(a is double&&b is double)return Math.Abs((double)a-(double)b)<=1e-9*Math.Max(1,Math.Abs((double)a));
            return a.Equals(b);
        }
    }
}
