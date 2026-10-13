using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using VGDATASTORELib;

namespace AvantageMcp {
[ComImport,Guid("3FB981F0-56AB-11D1-8AD5-006097D69B02"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAvFitProcess {
    [PreserveSig]int QuerySummaryAttributes(IntPtr store);
    [PreserveSig]int QueryProcessAttributes(IntPtr store,int count,IntPtr data,IntPtr axes,out int outputs);
    [PreserveSig]int ExecuteProcess(IntPtr store,int count,IntPtr data,IntPtr axes,int outputs,IntPtr outData);
    [PreserveSig]int PropertiesToString([MarshalAs(UnmanagedType.BStr)]out string value);
    [PreserveSig]int SetProcessAttributes(IntPtr store);
    [PreserveSig]int ProcessControlComplete(int hr);
}

/// <summary>Runs Avantage's native simplex fitting engine against exclusively owned data and peak objects.</summary>
public static class NativeSolver {
    public const string SupportedSha256="AADDFE7A956FC4EBADB08E74934035F71D3E6AC9818F8A04CBE9B8C01F8DC311";
    static readonly object Gate=new object();
    static IntPtr module,utilities;
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr LoadLibraryEx(string path,IntPtr file,uint flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Ansi,ExactSpelling=true)]static extern IntPtr GetProcAddress(IntPtr module,string name);
    [DllImport("ole32.dll",CharSet=CharSet.Unicode)]static extern int StgCreateDocfile(string name,uint mode,uint reserved,out IntPtr store);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate IntPtr Allocate(UIntPtr size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate IntPtr Construct(IntPtr memory,IntPtr parent);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate IntPtr ConstructDialog(IntPtr memory,IntPtr window,IntPtr parent);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate void Destruct(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate void SizedDelete(IntPtr self,UIntPtr size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate IntPtr DestroyDialog(IntPtr self,uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int Initialise(IntPtr self,IntPtr data,IntPtr axes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int InitialiseFit(IntPtr self,IntPtr table,IntPtr data,IntPtr spaceAxes,IntPtr output);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int Simplex(IntPtr self,IntPtr matrix,IntPtr values,int dimensions,double tolerance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int SetPeaks(IntPtr self,IntPtr spaceAxes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int CreateData(out IntPtr data,int createStream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int SetContext(IntPtr self,IntPtr storage,IntPtr input,IntPtr axes,IntPtr output);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int RegisterCookie(IntPtr unknown,ref Guid iid,out uint cookie);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int RemoveCookie(uint cookie);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int OpenProps(IntPtr storage,ref Guid iid,out IntPtr props);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int WriteProps(IntPtr self,uint count,IntPtr specs,IntPtr values,uint id);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int CommitProps(IntPtr self,uint flags);
    static T At<T>(int rva)where T:class{return (T)(object)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(module,rva),typeof(T));}
    static T Export<T>(string name)where T:class{IntPtr p=GetProcAddress(utilities,name);if(p==IntPtr.Zero)throw new EntryPointNotFoundException(name);return (T)(object)Marshal.GetDelegateForFunctionPointer(p,typeof(T));}
    static void Check(int hr){Marshal.ThrowExceptionForHR(hr);}
    static void Release(ref IntPtr pointer){if(pointer!=IntPtr.Zero){Marshal.Release(pointer);pointer=IntPtr.Zero;}}
    static double ReadDouble(IntPtr p,int offset){var value=new double[1];Marshal.Copy(IntPtr.Add(p,offset),value,0,1);return value[0];}
    static void RetainAt(IntPtr owner,int offset,IntPtr value){Marshal.AddRef(value);Marshal.WriteIntPtr(owner,offset,value);}
    static void EnsureModule(){
        if(module!=IntPtr.Zero)return;
        if(IntPtr.Size!=8)throw new PlatformNotSupportedException("Avantage native fitting requires an x64 worker.");
        string install=Environment.GetEnvironmentVariable("AVANTAGE_HOME")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Thermo Scientific","Avantage");
        string path=Path.Combine(install,"bin","PeakFitting.dll"),hash;
        using(var sha=SHA256.Create())using(var file=File.OpenRead(path))hash=BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");
        if(hash!=SupportedSha256)throw new Fault("UNSUPPORTED_NATIVE_BUILD","Native fitting is disabled for this PeakFitting.dll build (SHA256 "+hash+").");
        module=LoadLibraryEx(path,IntPtr.Zero,0x1100);if(module==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
        utilities=LoadLibraryEx(Path.Combine(install,"bin","ProcessingUtilities.dll"),IntPtr.Zero,0x1100);if(utilities==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    static void TableProperties(IntPtr storage,uint peakCookie,uint fitCookie){
        IntPtr props=IntPtr.Zero,specs=Marshal.AllocHGlobal(32),values=Marshal.AllocHGlobal(48);
        try{
            Guid id=new Guid("B8BC0471-5A88-11D1-8AD6-006097D69B02");Check(Export<OpenProps>("?OpenOrCreatePropSet@@YAJPEAUIStorage@@AEBU_GUID@@PEAPEAUIPropertyStorage@@@Z")(storage,ref id,out props));
            for(int i=0;i<32;i++)Marshal.WriteByte(specs,i,0);for(int i=0;i<48;i++)Marshal.WriteByte(values,i,0);
            for(int i=0;i<2;i++){Marshal.WriteInt32(specs,i*16,1);Marshal.WriteInt32(specs,i*16+8,i+3);Marshal.WriteInt16(values,i*24,19);Marshal.WriteInt32(values,i*24+8,unchecked((int)(i==0?peakCookie:fitCookie)));}
            var write=(WriteProps)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(props),32),typeof(WriteProps));Check(write(props,2,specs,values,2));
            var commit=(CommitProps)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(props),72),typeof(CommitProps));Check(commit(props,0));
        }finally{Release(ref props);Marshal.FreeHGlobal(specs);Marshal.FreeHGlobal(values);}
    }
    static double Residual(SpectrumSession session,NativePeakModel model,bool asymmetric){
        var evaluated=(Dictionary<string,object>)model.Evaluate(session.X,asymmetric);var sum=(double[])evaluated["peak_sum"];double sse=0;
        for(int i=session.BackgroundState.FirstIndex;i<=session.BackgroundState.LastIndex;i++){double d=session.Y[i]-session.BackgroundState.Curve[i]-sum[i];sse+=d*d;}
        if(Double.IsNaN(sse)||Double.IsInfinity(sse))throw new Fault("NUMERICAL_OVERFLOW","Native fit residual exceeds the supported numerical range.");
        return sse;
    }
    internal static Dictionary<string,object> Run(SpectrumSession session,NativePeakModel model,int maxIterations,double tolerance,bool asymmetric,Action<Dictionary<string,object>> completed){
        if(maxIterations<1||maxIterations>100000)throw new Fault("INVALID_ARGUMENT","max_iterations must be between 1 and 100000.");
        if(Double.IsNaN(tolerance)||Double.IsInfinity(tolerance)||tolerance<=0)throw new Fault("INVALID_ARGUMENT","tolerance must be a finite positive number.");
        if(Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA)throw new InvalidOperationException("Native fitting requires an STA worker.");
        if(session.BackgroundState==null)throw new Fault("BACKGROUND_REQUIRED","Set a background before fitting.");
        if(model.Peaks.Count<1)throw new Fault("PEAKS_REQUIRED","Add at least one peak before fitting.");
        if(session.EnergyScale!="kinetic"||session.X.Length<3)throw new Fault("INVALID_AXIS","Native fitting requires a one-dimensional energy spectrum.");
        lock(Gate){EnsureModule();return RunCore(session,model,maxIterations,tolerance,asymmetric,completed);}
    }
    static Dictionary<string,object> RunCore(SpectrumSession session,NativePeakModel model,int maxIterations,double tolerance,bool asymmetric,Action<Dictionary<string,object>> completed){
        Trace("start");
        model.AttachBackground(session.BackgroundState.Background,typeof(IAvantageBackground));
        double before=Residual(session,model,asymmetric);
        int method;model.Peaks[0].Native.GetGLCombineMethod(out method);
        foreach(var peak in model.Peaks){int other;peak.Native.GetGLCombineMethod(out other);if(other!=method)throw new Fault("MIXED_PEAK_METHODS","Avantage fitting requires all peaks to use the same GL combination method.");}
        if((long)session.X.Length*model.Peaks.Count>2000000)throw new Fault("MODEL_SIZE","Fitting is limited to two million component data points.");
        model.InvalidateFit();
        var axes=new DataAxesClass();var spaceAxes=new SpaceAxesClass();IAvFitProcess process=null;
        IntPtr input=IntPtr.Zero,axis=IntPtr.Zero,space=IntPtr.Zero,output=IntPtr.Zero,storage=IntPtr.Zero,fitTable=IntPtr.Zero,plugin=IntPtr.Zero,events=IntPtr.Zero,thread=IntPtr.Zero,dialog=IntPtr.Zero;
        IntPtr inputArray=Marshal.AllocHGlobal(8),axesArray=Marshal.AllocHGlobal(8);uint peakCookie=0,fitCookie=0;
        using(var tables=new NativeGridTables())try{
            Trace("tables");
            session.Data.GetDataAxes(axes);session.Data.GetSpaceAxes(spaceAxes);
            input=Marshal.GetComInterfaceForObject(session.Data,typeof(IDataSpace));axis=Marshal.GetComInterfaceForObject(axes,typeof(IDataAxes));space=Marshal.GetComInterfaceForObject(spaceAxes,typeof(ISpaceAxes));
            Marshal.WriteIntPtr(inputArray,input);Marshal.WriteIntPtr(axesArray,axis);
            Check(Export<CreateData>("?CreateDataSpace@@YAJPEAPEAUIDataSpace@@H@Z")(out output,1));
            Check(StgCreateDocfile(null,0x04001012,0,out storage));
            process=(IAvFitProcess)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9329D9F5-9345-11D2-8527-00104B053B4C")));
            Trace("process");
            plugin=Marshal.GetComInterfaceForObject(process,typeof(IAvFitProcess));Check(process.QuerySummaryAttributes(storage));
            Trace("summary");
            var adapter=new FitTableAdapter(model.Peaks.Select(p=>p.Native).ToArray(),asymmetric?1:0);fitTable=adapter.AcquireInterface();
            var register=Export<RegisterCookie>("?GetInterfaceCookie@@YAJPEAUIUnknown@@AEBU_GUID@@PEAK@Z");Guid peakId=new Guid("D62B63EA-4D36-11D2-BCAF-00C04F8EBEF8"),fitId=new Guid("D62B63EE-4D36-11D2-BCAF-00C04F8EBEF8");
            Check(register(tables.PeakTableInterface,ref peakId,out peakCookie));Check(register(fitTable,ref fitId,out fitCookie));TableProperties(storage,peakCookie,fitCookie);
            int outputs;Check(process.QueryProcessAttributes(storage,1,inputArray,axesArray,out outputs));
            Trace("query");
            Guid eventId=new Guid("2276F100-2554-11D2-B9DC-006097D69B02");Check(Marshal.QueryInterface(plugin,ref eventId,out events));
            thread=At<Construct>(0x42fc0)(At<Allocate>(0x88fd0)(new UIntPtr(0x240)),plugin);
            Trace("thread");
            Check(At<SetContext>(0x4ec70)(thread,storage,input,axis,output));
            Marshal.WriteInt32(thread,0x90,0);Marshal.WriteInt32(thread,0x94,0);Marshal.WriteInt32(thread,0x98,method);Marshal.WriteInt32(thread,0x3c,0);Marshal.WriteInt32(thread,0x38,10);
            dialog=At<ConstructDialog>(0x38160)(At<Allocate>(0x88fd0)(new UIntPtr(0x4ca8)),IntPtr.Zero,plugin);
            Trace("dialog");
            Marshal.WriteIntPtr(plugin,0x348,dialog);Marshal.WriteInt32(dialog,0xb70,maxIterations);Marshal.Copy(new[]{tolerance},0,IntPtr.Add(dialog,0xb68),1);
            RetainAt(thread,0xc0,space);RetainAt(thread,0xa8,events);RetainAt(thread,0xc8,fitTable);
            Check(At<Initialise>(0x4c4b0)(thread,input,axis));Trace("initialized");
            int fitStatus=At<InitialiseFit>(0x4d080)(thread,fitTable,input,space,output);
            // Verified native SetSimplexParameters returns this HRESULT when the
            // adjustable parameter count is zero (RVA 0x43ce2 in the pinned build).
            if(fitStatus==unchecked((int)0x800413c1)&&Marshal.ReadInt32(thread,0x198)<=0)throw new Fault("NO_FREE_PARAMETERS","No adjustable fit parameters remain.");
            Check(fitStatus);Trace("fit_initialized");
            int dimensions=Marshal.ReadInt32(thread,0x198);
            if(dimensions<1)throw new Fault("NO_FREE_PARAMETERS","No adjustable fit parameters remain.");
            if(Marshal.ReadIntPtr(thread,0xb0)==IntPtr.Zero||Marshal.ReadIntPtr(thread,0xb8)==IntPtr.Zero)throw new Fault("NATIVE_FIT_INITIALIZATION","Avantage could not initialize fit output.");
            int result=At<Simplex>(0x86ac0)(Marshal.ReadIntPtr(thread,8),Marshal.ReadIntPtr(thread,0x188),Marshal.ReadIntPtr(thread,0x190),dimensions,0);
            Trace("solved");
            int iterations=Marshal.ReadInt32(thread,0x18);double objective=ReadDouble(thread,0x20),improvement=ReadDouble(thread,0x28);
            if(result<1||result>3)throw new Fault("NATIVE_FIT_FAILED","Native optimizer stopped with status "+result+".");
            Check(At<SetPeaks>(0x4ee50)(thread,space));
            double after=Residual(session,model,asymmetric);
            int fittedPoints=session.BackgroundState.LastIndex-session.BackgroundState.FirstIndex+1;
            var report=J.Obj("engine","Avantage PeakFitting.dll native simplex","native_result",result,"iterations",iterations,"free_parameters",dimensions,"max_iterations",maxIterations,"tolerance",tolerance,"asymmetric",asymmetric,"native_objective",FiniteMetric(objective),"native_improvement",FiniteMetric(improvement),"residual_sse_before",before,"residual_sse_after",after,"residual_rmse",Math.Sqrt(after/fittedPoints),"fitted_point_count",fittedPoints,"converged",result==2,"reached_iteration_limit",result==1,"stop_reason",result==2?"tolerance":result==1?"iteration_limit":"cancelled","native_dll_sha256",SupportedSha256);
            if(adapter.UnsupportedSlots.Length!=0)throw new Fault("NATIVE_HOST_INCOMPATIBLE","Native fitting requested unsupported table operations.");
            model.MarkFitResult(report);GC.KeepAlive(adapter);
            // The native MFC engine has process-global fitting state. A one-shot child
            // commits its verified result and exits here; Windows reclaims native state.
            // This host must never run in the persistent spectrum/session process.
            completed(report);throw new InvalidOperationException("The isolated fit host must exit after committing its result.");
        }catch(Exception error){Engine.FailFitChild(error);throw;}finally{
            Trace("cleanup");
            if(thread!=IntPtr.Zero){At<Destruct>(0x436c0)(thread);At<SizedDelete>(0x89f08)(thread,new UIntPtr(0x240));Marshal.WriteIntPtr(IntPtr.Add(module,0xe5cf8),IntPtr.Zero);}
            if(dialog!=IntPtr.Zero){if(plugin!=IntPtr.Zero)Marshal.WriteIntPtr(plugin,0x348,IntPtr.Zero);var destroy=(DestroyDialog)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(dialog),8),typeof(DestroyDialog));destroy(dialog,1);}
            var remove=Export<RemoveCookie>("?RemoveInterfaceCookie@@YAJK@Z");if(peakCookie!=0)remove(peakCookie);if(fitCookie!=0)remove(fitCookie);
            Release(ref events);Release(ref plugin);J.Release(process);Release(ref fitTable);Release(ref storage);Release(ref output);Release(ref space);Release(ref axis);Release(ref input);
            Marshal.FreeHGlobal(inputArray);Marshal.FreeHGlobal(axesArray);J.Release(axes);J.Release(spaceAxes);
            Trace("cleaned");
        }
    }
    static object FiniteMetric(double value){return Double.IsNaN(value)||Double.IsInfinity(value)?null:(object)value;}
    static void Trace(string stage){if(Environment.GetEnvironmentVariable("AVANTAGE_NATIVE_DIAGNOSTICS")=="1")Console.Error.WriteLine("NativeSolver "+stage);}
}
}
