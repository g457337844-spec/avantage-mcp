using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.IO;
using VGDATASTORELib;

namespace AvantageMcp {
/// <summary>Owns real Avantage fitted-peak and constraint COM objects, independently of the live UI.</summary>
public sealed class NativePeakModel : IDisposable {
    public sealed class Peak : IDisposable {
        public string Id; public IAvFittedPeak Native;
        public readonly Dictionary<string,Dictionary<string,object>> Constraints=new Dictionary<string,Dictionary<string,object>>();
        public void Dispose(){J.Release(Native);Native=null;}
    }
    readonly IDataSpace data;
    readonly List<Peak> peaks=new List<Peak>();
    Dictionary<string,object> fitReport;
    public void InvalidateFit(){fitReport=null;}
    public void MarkFitResult(Dictionary<string,object> report){fitReport=report;}
    public IList<Peak> Peaks {get{return peaks.AsReadOnly();}}
    static readonly Dictionary<string,int> Attributes=new Dictionary<string,int>{{"area",1},{"centre",2},{"height",3},{"fwhm",4},{"gl",5},{"tail_height",6},{"tail_mix",7},{"tail_exponential",8}};
    static readonly Dictionary<string,int> Types=new Dictionary<string,int>{{"fixed",1},{"plus_current",2},{"minus_current",3},{"plus_minus_current",4},{"linked",5},{"range",6},{"linked_range",7},{"linked_range_percent",8}};
    static readonly Dictionary<string,int> Operators=new Dictionary<string,int>{{"none",0},{"plus",1},{"minus",2},{"multiply",3},{"divide",4}};
    static readonly Dictionary<string,int> CombinationMethods=new Dictionary<string,int>{{"product",0},{"sum",1},{"convolution",2}};
    public NativePeakModel(IDataSpace source){if(source==null)throw new ArgumentNullException("source");data=source;}
    // Takes ownership of the supplied RCWs only when adoption completes successfully.
    internal static NativePeakModel AdoptNative(IDataSpace source,IList<IAvFittedPeak> values,IList<Dictionary<string,object>> snapshots) {
        if(values.Count!=snapshots.Count)throw new ArgumentException("Native peak metadata count differs.");
        var model=new NativePeakModel(source);
        foreach(var native in values)model.peaks.Add(new Peak{Id=Guid.NewGuid().ToString("N"),Native=native});
        for(int index=0;index<values.Count;index++)foreach(var saved in (IEnumerable<Dictionary<string,object>>)snapshots[index]["constraints"]) {
            int attribute=Convert.ToInt32(saved["attribute"]),type=Convert.ToInt32(saved["type"]),op=Convert.ToInt32(saved["operator"]);
            string name=Attributes.FirstOrDefault(p=>p.Value==attribute).Key,kind=Types.FirstOrDefault(p=>p.Value==type).Key,operation=Operators.FirstOrDefault(p=>p.Value==op).Key;
            if(name==null||kind==null||operation==null)throw new Fault("UNSUPPORTED_CONSTRAINT","The native peak table contains a constraint this bridge cannot edit.");
            var spec=J.Obj("type",kind,"value1",saved["value1"],"value2",saved["value2"],"value3",saved["value3"],"operator",operation,"energy_scale","kinetic",
                "native",J.Obj("type",type,"value1",saved["value1"],"value2",saved["value2"],"value3",saved["value3"],"operator",op,"centre_scale","kinetic"));
            if(saved["reference_peak_index"]!=null) {
                int target=Convert.ToInt32(saved["reference_peak_index"]);
                if(target<0||target>=model.peaks.Count)throw new Fault("UNSUPPORTED_CONSTRAINT","A native constraint references a peak outside the imported table.");
                spec["reference_peak_id"]=model.peaks[target].Id;
            }
            model.peaks[index].Constraints[name]=spec;
        }
        return model;
    }
    internal static bool EquivalentBackground(IntPtr first,IntPtr second) {
        if(first==IntPtr.Zero||second==IntPtr.Zero)return first==second;
        IAvantageBackground a=null,b=null;
        try{a=(IAvantageBackground)Marshal.GetObjectForIUnknown(first);b=(IAvantageBackground)Marshal.GetObjectForIUnknown(second);sbyte equivalent;a.IsEquivalent(b,out equivalent);return equivalent!=0;}
        finally{if(a!=null)Marshal.ReleaseComObject(a);if(b!=null)Marshal.ReleaseComObject(b);}
    }
    public Peak Find(string id){var p=peaks.FirstOrDefault(x=>x.Id==id);if(p==null)throw new Fault("PEAK_NOT_FOUND",id);return p;}
    static IntPtr Pointer(object value,Type type){return Marshal.GetComInterfaceForObject(value,type);}
    static void Positive(double value,string name,bool zero=false){if(value<0||(!zero&&value==0))throw new Fault("INVALID_ARGUMENT",name+(zero?" must be nonnegative.":" must be positive."));}
    static void Fraction(double value,string name){if(value<0||value>1)throw new Fault("INVALID_ARGUMENT",name+" must be between 0 and 1.");}
    public object Add(Dictionary<string,object> args) {
        var p=new Peak{Id=Guid.NewGuid().ToString("N"),Native=(IAvFittedPeak)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("B90B1A14-A54F-11D2-852A-00104B053B4C")))};
        try {
            IntPtr ds=Pointer(data,typeof(IDataSpace));try{p.Native.SetDataSpace(ds);}finally{Marshal.Release(ds);}
            Set(p,args,true);peaks.Add(p);InvalidateFit();return Read(p);
        } catch {p.Dispose();throw;}
    }
    public object Update(string id,Dictionary<string,object> args){var p=Find(id);Set(p,args,false);InvalidateFit();return Read(p);}
    void Set(Peak p,Dictionary<string,object>a,bool initial) {
        double area,ar,c,h,hr,w,g,tm,th,te;p.Native.GetAsymParams(out area,out ar,out c,out h,out hr,out w,out g,out tm,out th,out te);
        string name;p.Native.GetName(out name);
        name=J.Str(a,"label",initial?"Peak "+(peaks.Count+1):name??"");
        if(name.Length==0||name.Length>256)throw new Fault("INVALID_ARGUMENT","Peak label must contain 1–256 characters.");
        if(initial||a.ContainsKey("centre_ev")) {
            c=J.Num(a,"centre_ev");string scale=J.Str(a,"energy_scale","binding");
            if(scale=="binding")c=EnergyConversions.BindingToKinetic(data,c);else if(scale!="kinetic")throw new Fault("INVALID_ARGUMENT","energy_scale must be binding or kinetic.");
        }
        h=J.Num(a,"height",initial?1:h);w=J.Num(a,"fwhm_ev",initial?1:w);g=J.Num(a,"gaussian_lorentz",initial?0.3:g);
        area=J.Num(a,"area",initial?0:area);ar=J.Num(a,"area_ratio",initial?1:ar);hr=J.Num(a,"height_ratio",initial?1:hr);
        tm=J.Num(a,"tail_mix",initial?0:tm);th=J.Num(a,"tail_height",initial?0:th);te=J.Num(a,"tail_exponential",initial?0:te);
        Positive(h,"height",true);Positive(w,"fwhm_ev");Positive(area,"area",true);Positive(ar,"area_ratio",true);Positive(hr,"height_ratio",true);Fraction(g,"gaussian_lorentz");Fraction(tm,"tail_mix");Positive(th,"tail_height",true);Positive(te,"tail_exponential",true);
        int method;p.Native.GetGLCombineMethod(out method);
        if(initial||a.ContainsKey("gl_combine_method")){string combine=J.Str(a,"gl_combine_method","product");if(!CombinationMethods.TryGetValue(combine,out method))throw new Fault("INVALID_ARGUMENT","gl_combine_method must be product, sum, or convolution.");}
        p.Native.SetAsymParams(area,ar,c,h,hr,w,g,tm,th,te);p.Native.SetName(name);p.Native.SetGLCombineMethod(method);
    }
    public void AttachBackground(object background,Type backgroundInterface) {
        IntPtr b=Pointer(background,backgroundInterface),identity=IntPtr.Zero;Guid unknown=new Guid("00000000-0000-0000-C000-000000000046");bool changed=false;
        try{
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(b,ref unknown,out identity));var pending=new List<Peak>();
            foreach(var p in peaks){IntPtr existing=IntPtr.Zero,other=IntPtr.Zero;try{try{p.Native.GetBackground(out existing);}catch(Exception error){if(error.HResult!=unchecked((int)0x80004003))throw;}if(existing!=IntPtr.Zero)Marshal.ThrowExceptionForHR(Marshal.QueryInterface(existing,ref unknown,out other));if(other!=identity&&!EquivalentBackground(existing,b)){if(p.Constraints.Count>0)throw new Fault("BACKGROUND_HAS_CONSTRAINTS","Changing the background would clear native peak constraints. Clear constraints before replacing the background.");pending.Add(p);}}finally{if(other!=IntPtr.Zero)Marshal.Release(other);if(existing!=IntPtr.Zero)Marshal.Release(existing);}}
            foreach(var p in pending){p.Native.SetBackground(b);changed=true;}
        }finally{if(identity!=IntPtr.Zero)Marshal.Release(identity);Marshal.Release(b);}
        if(changed)InvalidateFit();
    }
    public object Remove(string id){var p=Find(id);foreach(var other in peaks)foreach(var c in other.Constraints.Values)if(c.ContainsKey("reference_peak_id")&&Convert.ToString(c["reference_peak_id"])==id)throw new Fault("PEAK_REFERENCED","Remove constraints referencing this peak before deleting it.");peaks.Remove(p);p.Dispose();InvalidateFit();return J.Obj("removed",true,"peak_id",id);}
    public object Read(Peak p) {
        string label;double area,ar,c,h,hr,w,g,tm,th,te;int method;
        p.Native.GetName(out label);p.Native.GetAsymParams(out area,out ar,out c,out h,out hr,out w,out g,out tm,out th,out te);p.Native.GetGLCombineMethod(out method);
        return J.Obj("peak_id",p.Id,"label",label,"centre_binding_ev",EnergyConversions.KineticToBinding(data,c),"centre_kinetic_ev",c,"height",h,"fwhm_ev",w,"gaussian_lorentz",g,"area",area,"area_ratio",ar,"height_ratio",hr,"tail_mix",tm,"tail_height",th,"tail_exponential",te,"native_gl_combine_method",method,"constraints",p.Constraints,"engine","Avantage IFittedPeak COM");
    }
    public object Info(){return J.Obj("peaks",peaks.Select(Read).ToArray(),"peak_count",peaks.Count,"engine","Avantage IFittedPeak COM","fit_executed",fitReport!=null,"fit_report",fitReport);}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr LoadLibraryEx(string path,IntPtr file,uint flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Ansi,ExactSpelling=true)]static extern IntPtr GetProcAddress(IntPtr module,string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int GenerateCurve(double centreIndex,double height,double widthPoints,double gl,double tailHeight,double tailMix,double tailExp,[Out,MarshalAs(UnmanagedType.LPArray,SizeParamIndex=8)]double[] curve,uint count,int asymmetric,int method);
    static IntPtr curveModule;static GenerateCurve generateCurve;
    public object Evaluate(double[] kineticAxis,bool asymmetric) {
        if(kineticAxis==null||kineticAxis.Length<2)throw new Fault("INVALID_AXIS","At least two energy points are required.");
        double step=kineticAxis[1]-kineticAxis[0];if(step<=0||Double.IsNaN(step)||Double.IsInfinity(step))throw new Fault("INVALID_AXIS","Native peak generation requires an increasing, regular kinetic-energy axis.");
        for(int i=0;i<kineticAxis.Length;i++)if(Double.IsNaN(kineticAxis[i])||Double.IsInfinity(kineticAxis[i])||Math.Abs(kineticAxis[i]-(kineticAxis[0]+i*step))>Math.Max(1e-6,Math.Abs(step)*1e-5))throw new Fault("INVALID_AXIS","Native peak generation requires a regular kinetic-energy axis.");
        if(generateCurve==null){string install=Environment.GetEnvironmentVariable("AVANTAGE_HOME")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Thermo Scientific","Avantage");curveModule=LoadLibraryEx(Path.Combine(install,"bin","PlugInUtilities.dll"),IntPtr.Zero,0x1100);if(curveModule==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());IntPtr address=GetProcAddress(curveModule,"?GenerateGLCurve@@YAHNNNNNNNPEANKHJ@Z");if(address==IntPtr.Zero)throw new EntryPointNotFoundException("Avantage GenerateGLCurve is unavailable.");generateCurve=(GenerateCurve)Marshal.GetDelegateForFunctionPointer(address,typeof(GenerateCurve));}
        var components=new List<object>();double[] sum=new double[kineticAxis.Length];
        foreach(var p in peaks){double area,ar,c,h,hr,w,g,tm,th,te;int method;p.Native.GetAsymParams(out area,out ar,out c,out h,out hr,out w,out g,out tm,out th,out te);p.Native.GetGLCombineMethod(out method);var curve=new double[kineticAxis.Length];int result=generateCurve((c-kineticAxis[0])/step,h,w/step,g,th,tm,te,curve,(uint)curve.Length,asymmetric?1:0,method);if(result==0)throw new Fault("NATIVE_CURVE_FAILED","Avantage could not generate peak "+p.Id);for(int i=0;i<curve.Length;i++){if(Double.IsNaN(curve[i])||Double.IsInfinity(curve[i]))throw new Fault("NATIVE_CURVE_FAILED","Avantage generated non-finite intensities.");sum[i]+=curve[i];}components.Add(J.Obj("peak_id",p.Id,"y",curve));}
        return J.Obj("components",components,"peak_sum",sum,"point_count",sum.Length,"asymmetric",asymmetric,"engine","Avantage PlugInUtilities.GenerateGLCurve");
    }
    bool Reaches(string from,string target,HashSet<string>seen){if(from==target)return true;if(!seen.Add(from))return false;return Find(from).Constraints.Values.Any(c=>c.ContainsKey("reference_peak_id")&&Reaches(Convert.ToString(c["reference_peak_id"]),target,seen));}
    public object Constraint(string id,string attribute,Dictionary<string,object>a) {
        int attr,type,op;string kind=J.Str(a,"type"),operation=J.Str(a,"operator","none");
        if(!Attributes.TryGetValue(attribute,out attr)||!Types.TryGetValue(kind,out type)||!Operators.TryGetValue(operation,out op))throw new Fault("INVALID_ARGUMENT","Unknown constraint attribute, type, or operator.");
        var p=Find(id);double v1=J.Num(a,"value1"),v2=J.Num(a,"value2",0),v3=J.Num(a,"value3",0);string refId=null;Peak reference=null;
        bool linked=type==5||type==7||type==8;
        if(linked){refId=J.Str(a,"reference_peak_id");reference=Find(refId);if(Reaches(refId,id,new HashSet<string>()))throw new Fault("CYCLIC_CONSTRAINT","Peak constraint would create a circular reference.");if(op==0||(op==4&&v1==0))throw new Fault("INVALID_ARGUMENT","Linked constraints require an operator; division by zero is invalid.");}
        else if(a.ContainsKey("reference_peak_id")||op!=0)throw new Fault("INVALID_ARGUMENT","Only linked constraints accept a reference peak or operator.");
        if(type==6&&v1>v2)throw new Fault("INVALID_ARGUMENT","Range lower bound must not exceed upper bound.");
        string scale=J.Str(a,"energy_scale","binding");if(scale!="binding"&&scale!="kinetic")throw new Fault("INVALID_ARGUMENT","energy_scale must be binding or kinetic.");
        if(attribute=="centre"&&scale=="binding") {
            if(type==1)v1=EnergyConversions.BindingToKinetic(data,v1);
            else if(type==6){double low=EnergyConversions.BindingToKinetic(data,v2);v2=EnergyConversions.BindingToKinetic(data,v1);v1=low;}
            else if(type==2)type=3;else if(type==3)type=2;
            else if(linked){if(op==3||op==4)throw new Fault("INVALID_ARGUMENT","Binding-energy centre links support plus or minus only; use kinetic scale for multiply/divide.");op=op==1?2:1;}
        }
        var constraint=(IAvPeakConstraint)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("68F7DAF5-B53B-11D2-988E-00C04F8EBE0F")));
        IntPtr refPtr=IntPtr.Zero,cp=IntPtr.Zero;
        try {
            if(reference!=null)refPtr=Pointer(reference.Native,typeof(IAvFittedPeak));constraint.SetParameters(type,v1,v2,v3,refPtr,op);
            cp=Pointer(constraint,typeof(IAvPeakConstraint));
            if(p.Constraints.ContainsKey(attribute))p.Native.ChangePeakFitConstraint(cp,attr);else p.Native.CreateInitialPeakFitConstraint(attr,cp);
            int actualType,actualOp;double av1,av2,av3;IntPtr actualRef,stored;p.Native.GetPeakFitConstraint(attr,out stored);
            if(stored==IntPtr.Zero)throw new Fault("NATIVE_CONSTRAINT_FAILED","Avantage did not retain the constraint.");
            IAvPeakConstraint readback=(IAvPeakConstraint)Marshal.GetObjectForIUnknown(stored);try{readback.GetParameters(out actualType,out av1,out av2,out av3,out actualRef,out actualOp);if(actualRef!=IntPtr.Zero)Marshal.Release(actualRef);}finally{Marshal.Release(stored);Marshal.ReleaseComObject(readback);}
            var spec=new Dictionary<string,object>(a);spec["native"]=J.Obj("type",actualType,"value1",av1,"value2",av2,"value3",av3,"operator",actualOp,"centre_scale","kinetic");p.Constraints[attribute]=spec;InvalidateFit();
            return Read(p);
        } finally {if(cp!=IntPtr.Zero)Marshal.Release(cp);if(refPtr!=IntPtr.Zero)Marshal.Release(refPtr);J.Release(constraint);}
    }
    public object RemoveConstraint(string id,string attribute) {
        int attr;if(!Attributes.TryGetValue(attribute,out attr))throw new Fault("INVALID_ARGUMENT","Unknown constraint attribute.");var p=Find(id);
        if(!p.Constraints.ContainsKey(attribute))return Read(p);
        IntPtr c;p.Native.GetPeakFitConstraint(attr,out c);try{p.Native.RemovePeakFitConstraint(c,attr);}finally{if(c!=IntPtr.Zero)Marshal.Release(c);}p.Constraints.Remove(attribute);InvalidateFit();return Read(p);
    }
    public void Dispose(){foreach(var p in peaks)p.Dispose();peaks.Clear();}
}
}
