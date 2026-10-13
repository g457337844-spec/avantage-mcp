using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using VGDATASTORELib;

namespace AvantageMcp {
[ComImport,Guid("C9D7ED50-5854-11D2-B66E-006097BE683A"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAvantageBackground {
    void GetStart(out double value);
    void GetEnd(out double value);
    void GetMode(out int value);
    void SetStart(double value);
    void SetEnd(double value);
    void SetMode(int value);
    void GetPointsToAverage(out int value);
    void SetPointsToAverage(int value);
    void GeteVgranularity(out double value);
    void SeteVgranularity(double value);
    // The installed typelib mislabels destination arrays as IBackground.
    // Native calls and the DLL's implementation verify that these are double*.
    void Generate(IntPtr source,IntPtr destination,int size,int startIndex,int endIndex,sbyte veryWidePeaks);
    void GenerateEx(IntPtr source,IntPtr destination,int size,int startIndex,int endIndex,sbyte veryWidePeaks,double initial);
    void CloneBackground(out IAvantageBackground copy);
    void PropertiesToString([MarshalAs(UnmanagedType.BStr)] out string value);
    void GetShape([MarshalAs(UnmanagedType.Interface)] IDataSpace data,[MarshalAs(UnmanagedType.Interface)] IDataAxes axes,IntPtr destination,int size);
    void GetShapeParameters([MarshalAs(UnmanagedType.Interface)] IDataSpace data,out int size,out int energyAxis,out int first,out int last,out double start,out double end);
    void IsEquivalent(IAvantageBackground other,out sbyte equivalent);
    void GetGUIDString([MarshalAs(UnmanagedType.BStr)] out string value);
    void GetBackgroundType(out int value);
}

public sealed class NativeBackground : IDisposable {
    static readonly Dictionary<string,string> Classes=new Dictionary<string,string> {
        {"linear","C9D7ED53-5854-11D2-B66E-006097BE683A"},
        {"shirley","C9D7ED55-5854-11D2-B66E-006097BE683A"},
        {"tougaard","C9D7ED57-5854-11D2-B66E-006097BE683A"},
        {"smart","C9D7ED59-5854-11D2-B66E-006097BE683A"},
        {"simple","10EEA3DF-59CF-40F8-8159-1C2ED4498C07"}
    };
    public IAvantageBackground Background;
    public string Kind;
    public double StartBinding,EndBinding;
    public int PointsToAverage;
    public int ShirleyIterations;
    public double ShirleyConvergence;
    public bool ShirleySmooth;
    public string TougaardType;
    public double TougaardB,TougaardC,TougaardD,TougaardYOffset;
    public bool TougaardEnergyCorrection;
    public int FirstIndex,LastIndex;
    public double[] Curve;
    internal static NativeBackground Adopt(SpectrumSession s,IAvantageBackground native,Dictionary<string,object> saved) {
        int type=J.Int(saved,"type",-1);string[] kinds={"linear","shirley","tougaard","smart","simple"};
        if(type<0||type>=kinds.Length)throw new Fault("UNSUPPORTED_BACKGROUND","The peak table uses an unsupported background type.");
        double start=J.Num(saved,"start_kinetic_ev"),end=J.Num(saved,"end_kinetic_ev");
        if(start>=end||start<s.X.Min()-1e-6||end>s.X.Max()+1e-6)throw new Fault("BACKGROUND_RANGE_MISMATCH","Peak-table background bounds are outside this spectrum. Use the matching VGD spectrum.");
        var bg=new NativeBackground{Background=native,Kind=kinds[type],PointsToAverage=J.Int(saved,"points_to_average",2),StartBinding=EnergyConversions.KineticToBinding(s.Data,end),EndBinding=EnergyConversions.KineticToBinding(s.Data,start)};
        var extensions=(Dictionary<string,object>)saved["extensions"];
        if(type==1){bg.ShirleyIterations=J.Int(extensions,"shirley_iterations",100);bg.ShirleyConvergence=J.Num(extensions,"shirley_convergence",1);bg.ShirleySmooth=J.Int(extensions,"shirley_smooth",0)!=0;}
        if(type==2){bg.TougaardType=J.Int(extensions,"tougaard_type",0)==0?"bc":"bcd";bg.TougaardB=J.Num(extensions,"tougaard_b");bg.TougaardC=J.Num(extensions,"tougaard_c");bg.TougaardD=J.Num(extensions,"tougaard_d");bg.TougaardYOffset=J.Num(extensions,"tougaard_y_offset",0);bg.TougaardEnergyCorrection=J.Int(extensions,"tougaard_energy_correction",0)!=0;}
        bg.Refresh(s);return bg;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetInteger(IntPtr instance,int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetDouble(IntPtr instance,double value);
    static void SetExtension(IAvantageBackground bg,string guid,int slot,object value) {
        IntPtr unknown=Marshal.GetIUnknownForObject(bg),sub=IntPtr.Zero;
        try {
            Guid iid=new Guid(guid);Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown,ref iid,out sub));
            IntPtr function=Marshal.ReadIntPtr(Marshal.ReadIntPtr(sub),slot*IntPtr.Size);
            int hr=value is int ? ((SetInteger)Marshal.GetDelegateForFunctionPointer(function,typeof(SetInteger)))(sub,(int)value)
                               : ((SetDouble)Marshal.GetDelegateForFunctionPointer(function,typeof(SetDouble)))(sub,(double)value);
            Marshal.ThrowExceptionForHR(hr);
        } finally {if(sub!=IntPtr.Zero)Marshal.Release(sub);Marshal.Release(unknown);}
    }
    public static NativeBackground Create(SpectrumSession s,Dictionary<string,object> args) {
        if(s.BindingEnergy==null||s.Axes.Count!=1)throw new Fault("UNSUPPORTED_DATA","Background calculation requires a one-dimensional energy spectrum.");
        string kind=J.Str(args,"kind","shirley").ToLowerInvariant(),clsid;
        if(!Classes.TryGetValue(kind,out clsid))throw new Fault("INVALID_ARGUMENT","Background kind must be linear, shirley, tougaard, smart, or simple.");
        double minimum=s.BindingEnergy.Min(),maximum=s.BindingEnergy.Max();
        double start=J.Num(args,"start_binding_ev",minimum),end=J.Num(args,"end_binding_ev",maximum);
        if(start>=end||start<minimum-1e-8||end>maximum+1e-8)throw new Fault("INVALID_ARGUMENT","Background binding-energy bounds must be increasing and inside the spectrum.");
        int selected=s.BindingEnergy.Count(v=>v>=start-1e-8&&v<=end+1e-8);
        int average=J.Int(args,"points_to_average",2);
        if(selected<3||average<0||average>100||2*(average+1)>selected)throw new Fault("INVALID_ARGUMENT","Background region needs at least three points; endpoint averaging must fit within the region.");
        var bg=new NativeBackground{Kind=kind,StartBinding=start,EndBinding=end,PointsToAverage=average};
        try {
            bg.Background=(IAvantageBackground)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid(clsid)));
            double a=EnergyConversions.BindingToKinetic(s.Data,start),b=EnergyConversions.BindingToKinetic(s.Data,end);
            bg.Background.SetStart(Math.Min(a,b));bg.Background.SetEnd(Math.Max(a,b));
            int mode=J.Int(args,"native_mode",0);if(mode<0||mode>1)throw new Fault("UNSUPPORTED_BACKGROUND","Native background mode must be User (0) or Peak (1).");
            bg.Background.SetMode(mode);bg.Background.SetPointsToAverage(average);
            if(kind=="shirley") {
                bg.ShirleyIterations=J.Int(args,"shirley_iterations",100);
                bg.ShirleyConvergence=J.Num(args,"shirley_convergence",1.0);
                bg.ShirleySmooth=J.Bool(args,"shirley_smooth",false);
                if(bg.ShirleyIterations<1||bg.ShirleyIterations>10000||bg.ShirleyConvergence<=0)
                    throw new Fault("INVALID_ARGUMENT","Shirley iterations must be 1–10000 and convergence must be positive.");
                const string iid="C9D7ED54-5854-11D2-B66E-006097BE683A";
                SetExtension(bg.Background,iid,24,bg.ShirleyIterations);
                SetExtension(bg.Background,iid,25,bg.ShirleyConvergence);
                // This field is not initialized by the Avantage coclass constructor.
                SetExtension(bg.Background,iid,28,bg.ShirleySmooth?1:0);
            }
            if(kind=="tougaard") {
                bg.TougaardType=J.Str(args,"tougaard_type","bc");
                bg.TougaardB=J.Num(args,"tougaard_b",2866);bg.TougaardC=J.Num(args,"tougaard_c",1643);bg.TougaardD=J.Num(args,"tougaard_d",0);
                bg.TougaardYOffset=J.Num(args,"tougaard_y_offset",0);bg.TougaardEnergyCorrection=J.Bool(args,"tougaard_energy_correction",false);
                if((bg.TougaardType!="bc"&&bg.TougaardType!="bcd")||bg.TougaardB<0||bg.TougaardC<=0||bg.TougaardD<0)
                    throw new Fault("INVALID_ARGUMENT","Tougaard type must be bc or bcd; B/D must be nonnegative and C positive.");
                const string iid="C9D7ED56-5854-11D2-B66E-006097BE683A";
                SetExtension(bg.Background,iid,28,bg.TougaardYOffset);SetExtension(bg.Background,iid,29,bg.TougaardType=="bc"?0:1);
                SetExtension(bg.Background,iid,30,bg.TougaardB);SetExtension(bg.Background,iid,31,bg.TougaardC);SetExtension(bg.Background,iid,32,bg.TougaardD);
                SetExtension(bg.Background,iid,33,bg.TougaardEnergyCorrection?1:0);
            }
            double increment=Math.Abs(s.X[1]-s.X[0]);
            if(increment<=0||s.X.Zip(s.X.Skip(1),(x,y)=>Math.Abs(Math.Abs(y-x)-increment)).Any(d=>d>1e-5))
                throw new Fault("UNSUPPORTED_DATA","Native backgrounds require a uniform energy axis.");
            bg.Background.SeteVgranularity(increment);
            bg.Refresh(s);
            return bg;
        } catch {bg.Dispose();throw;}
    }
    public void Refresh(SpectrumSession s) {
        var curve=new double[s.Y.Length];
        GCHandle pinned=default(GCHandle),source=default(GCHandle);
        try {
            int nativeSize,energyAxis,first,last;double start,end;
            try {Background.GetShapeParameters(s.Data,out nativeSize,out energyAxis,out first,out last,out start,out end);}
            catch(Exception e){throw new Fault("BACKGROUND_FAILED","Native background shape parameters failed: "+e.Message+" ("+e.HResult.ToString("X8")+").");}
            if(first<0||last>=s.Y.Length||nativeSize!=last-first+1)throw new Fault("BACKGROUND_FAILED","Unexpected native background region dimensions.");
            pinned=GCHandle.Alloc(curve,GCHandleType.Pinned);source=GCHandle.Alloc(s.Y,GCHandleType.Pinned);
            try {Background.GenerateEx(source.AddrOfPinnedObject(),pinned.AddrOfPinnedObject(),curve.Length,first,last,0,0);}
            catch(Exception e){throw new Fault("BACKGROUND_FAILED","Native background shape failed ("+e.HResult.ToString("X8")+"). Native size="+nativeSize+", axis="+energyAxis+", first="+first+", last="+last+", start="+start+", end="+end+".");}
            FirstIndex=first;LastIndex=last;
            if(curve.Any(v=>double.IsNaN(v)||double.IsInfinity(v)))throw new Fault("BACKGROUND_FAILED","Avantage returned a non-finite background.");
            Curve=curve;
        } finally {if(pinned.IsAllocated)pinned.Free();if(source.IsAllocated)source.Free();}
    }
    public Dictionary<string,object> Info() {
        int type,mode;Background.GetBackgroundType(out type);Background.GetMode(out mode);
        string description;Background.PropertiesToString(out description);
        var result=J.Obj("kind",Kind,"native_type",type,"native_mode",mode,"start_binding_ev",StartBinding,"end_binding_ev",EndBinding,"first_index",FirstIndex,"last_index",LastIndex,"points_to_average",PointsToAverage,"point_count",Curve.Length,"native_description",description,"backend","Avantage native IBackground","shirley_iterations",Kind=="shirley"?(object)ShirleyIterations:null,"shirley_convergence",Kind=="shirley"?(object)ShirleyConvergence:null,"shirley_smooth",Kind=="shirley"?(object)ShirleySmooth:null);
        if(Kind=="tougaard") {result["tougaard_type"]=TougaardType;result["tougaard_b"]=TougaardB;result["tougaard_c"]=TougaardC;result["tougaard_d"]=TougaardD;result["tougaard_y_offset"]=TougaardYOffset;result["tougaard_energy_correction"]=TougaardEnergyCorrection;}
        return result;
    }
    public void Dispose(){J.Release(Background);Background=null;}
}
public sealed partial class Engine {
    object SetBackground(SpectrumSession s,Dictionary<string,object> args) {
        if(s.FitState!=null)throw new Fault("MODEL_ALREADY_DEFINED","Set the background before defining peaks; clear the fit model to change its background.");
        var replacement=NativeBackground.Create(s,args);
        if(s.BackgroundState!=null)s.BackgroundState.Dispose();
        s.BackgroundState=replacement;
        return J.Obj("session_id",s.Id,"background",replacement.Info());
    }
    object GetBackground(SpectrumSession s){return J.Obj("session_id",s.Id,"background",s.BackgroundState==null?null:s.BackgroundState.Info());}
    object GetBackgroundData(SpectrumSession s,Dictionary<string,object> args) {
        if(s.BackgroundState==null)throw new Fault("BACKGROUND_NOT_DEFINED","Set a background first.");
        int offset=J.Int(args,"offset",0),limit=J.Int(args,"limit",1000);
        if(offset<0||offset>s.Y.Length||limit<1||limit>10000)throw new Fault("INVALID_ARGUMENT","offset must be within the spectrum and limit must be 1–10000.");
        int n=Math.Min(limit,s.Y.Length-offset);
        var bg=s.BackgroundState;
        return J.Obj("session_id",s.Id,"offset",offset,"count",n,"total",s.Y.Length,"binding_energy",s.BindingEnergy.Skip(offset).Take(n).ToArray(),"background",Enumerable.Range(offset,n).Select(i=>i<bg.FirstIndex||i>bg.LastIndex?null:(object)bg.Curve[i]).ToArray(),"background_subtracted",Enumerable.Range(offset,n).Select(i=>i<bg.FirstIndex||i>bg.LastIndex?null:(object)(s.Y[i]-bg.Curve[i])).ToArray(),"next_offset",offset+n<s.Y.Length?(object)(offset+n):null);
    }
}
}
