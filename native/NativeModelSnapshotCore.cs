using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace AvantageMcp {
// Read-only native model values; no document factories, private hosts or cache pinning.
internal static class NativeModelSnapshotCore {
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetInteger(IntPtr self,out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDouble(IntPtr self,out double value);
        internal static List<Dictionary<string,object>> ReadPeaks(IList<IAvFittedPeak> peaks)
        {
            var pointers=new List<IntPtr>();
            try
            {
                // IUnknown is the COM identity. An RPC out-reference can expose
                // a different interface proxy than GetComInterfaceForObject.
                foreach(var peak in peaks){if(peak==null)throw new InvalidOperationException("Null fitted peak in model snapshot.");IntPtr typed=Marshal.GetComInterfaceForObject(peak,typeof(IAvFittedPeak));try{Guid iid=new Guid("00000000-0000-0000-C000-000000000046");IntPtr identity;Marshal.ThrowExceptionForHR(Marshal.QueryInterface(typed,ref iid,out identity));pointers.Add(identity);}finally{Marshal.Release(typed);}}
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
                            IntPtr identity=IntPtr.Zero;
                            try{if(reference!=IntPtr.Zero){Guid iid=new Guid("00000000-0000-0000-C000-000000000046");Marshal.ThrowExceptionForHR(Marshal.QueryInterface(reference,ref iid,out identity));}if(active!=0)constraints.Add(new Dictionary<string,object>{{"attribute",attr},{"type",type},{"value1",v1},{"value2",v2},{"value3",v3},{"operator",op},{"reference_peak_index",identity==IntPtr.Zero?(object)null:pointers.IndexOf(identity)}});}
                            finally{if(identity!=IntPtr.Zero)Marshal.Release(identity);if(reference!=IntPtr.Zero)Marshal.Release(reference);}
                        }
                        finally{if(constraint!=null)Marshal.ReleaseComObject(constraint);Marshal.Release(raw);}
                    }
                    object background=null;IntPtr bgRaw=IntPtr.Zero;
                    // A privately created peak has no background interface until
                    // one is attached, and the vendor call faults on the null
                    // reference instead of returning zero.
                    try{peak.GetBackground(out bgRaw);}catch(NullReferenceException){bgRaw=IntPtr.Zero;}
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
        internal static object PersistedModelParameters(object value)
        {
            var record=value as IDictionary<string,object>;
            if(record!=null)
            {
                bool fitted=record.ContainsKey("centre_kinetic_ev")&&
                    record.ContainsKey("native_gl_combine_method")&&record.ContainsKey("constraints");
                var normalized=new Dictionary<string,object>();
                foreach(var pair in record)
                    if(!fitted||(pair.Key!="area_ratio"&&pair.Key!="height_ratio"))
                        normalized.Add(pair.Key,PersistedModelParameters(pair.Value));
                return normalized;
            }
            var sequence=value as System.Collections.IEnumerable;
            if(!(value is string)&&sequence!=null)
                return sequence.Cast<object>().Select(PersistedModelParameters).ToArray();
            return value;
        }
        internal static bool PersistedEquivalent(object first,object second)
        {
            return Equivalent(PersistedModelParameters(first),PersistedModelParameters(second));
        }
        internal static bool Equivalent(object a,object b)
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
            if(IsNumber(a)&&IsNumber(b)){
                double first=Convert.ToDouble(a),second=Convert.ToDouble(b);
                return !double.IsNaN(first)&&!double.IsNaN(second)&&!double.IsInfinity(first)&&!double.IsInfinity(second)&&Math.Abs(first-second)<=1e-9*Math.Max(1,Math.Abs(first));
            }
            return a.Equals(b);
        }
        static bool IsNumber(object value){return value is byte||value is sbyte||value is short||value is ushort||value is int||value is uint||value is long||value is ulong||value is float||value is double||value is decimal;}
        internal static string Fingerprint(object value)
        {
            var json=new System.Web.Script.Serialization.JavaScriptSerializer{MaxJsonLength=1024*1024,RecursionLimit=64};
            using(var sha=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(json.Serialize(PersistedModelParameters(value))))).Replace("-","");
        }
        internal static bool EditableEquivalent(object first,object second){return Equivalent(Editable(first),Editable(second));}
        static object Editable(object value)
        {
            var record=value as IDictionary<string,object>;
            if(record!=null)
            {
                bool fitted=record.ContainsKey("centre_kinetic_ev")&&record.ContainsKey("constraints");
                bool background=record.ContainsKey("start_kinetic_ev")&&record.ContainsKey("extensions");
                var result=new Dictionary<string,object>();
                foreach(var item in record)if(!(fitted&&(item.Key=="area_ratio"||item.Key=="height_ratio"))&&!(background&&item.Key=="properties"))result.Add(item.Key,Editable(item.Value));
                return result;
            }
            var array=value as System.Collections.IEnumerable;if(array!=null&&!(value is string))return array.Cast<object>().Select(Editable).ToArray();
            return value;
        }
}
}
