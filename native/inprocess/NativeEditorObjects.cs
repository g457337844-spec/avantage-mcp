using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace AvantageMcp.InProcess
{
    internal static class NativeEditorObjects
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]internal delegate int AddPrimary(IntPtr doc,IntPtr entry);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]internal delegate int AddFitted(IntPtr doc,IntPtr peak,IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]internal delegate int RemoveItem(IntPtr doc,IntPtr item,int update);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate IntPtr FirstPrimary(IntPtr doc);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate IntPtr NextPrimary(IntPtr doc,ref IntPtr position);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]internal delegate int PointerOut(IntPtr self,out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]internal delegate int SetPointer(IntPtr self,IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]internal delegate int SetInteger(IntPtr self,int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]internal delegate int GetInteger(IntPtr self,out int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int SetDouble(IntPtr self,double value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int SetByte(IntPtr self,sbyte value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int SetText(IntPtr self,[MarshalAs(UnmanagedType.BStr)]string value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]internal delegate int GetText(IntPtr self,[MarshalAs(UnmanagedType.BStr)]out string value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int SetAttributes(IntPtr self,double start,double centre,double end,double width);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int GetAttributes(IntPtr self,out double start,out double centre,out double end,out double width);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int SetSubParameters(IntPtr self,double height,double area,double transmission,double normalised);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern IntPtr GetModuleHandleW(string name);
        [DllImport("kernel32.dll",CharSet=CharSet.Ansi,ExactSpelling=true)]static extern IntPtr GetProcAddress(IntPtr module,string name);
        internal static T Slot<T>(IntPtr value,int index)where T:class
        {return Marshal.GetDelegateForFunctionPointer(NativeContextIdentity.ReadField(NativeContextIdentity.ReadField(value,0),index*8),typeof(T))as T;}
        internal static T Export<T>(string name,int rva)where T:class
        {
            IntPtr module=GetModuleHandleW("ProcessGrid.dll"),value=GetProcAddress(module,name);
            if(module==IntPtr.Zero||value!=IntPtr.Add(module,rva))throw new InvalidOperationException("LIVE_EDITOR_ABI_MISMATCH");
            return Marshal.GetDelegateForFunctionPointer(value,typeof(T))as T;
        }
        internal static IntPtr Query(IntPtr source,string iid)
        {Guid guid=new Guid(iid);IntPtr value;Marshal.ThrowExceptionForHR(Marshal.QueryInterface(source,ref guid,out value));return value;}
        internal static double Number(IDictionary<string,object> value,string key){return Convert.ToDouble(value[key],CultureInfo.InvariantCulture);}
        internal static int Integer(IDictionary<string,object> value,string key){return Convert.ToInt32(value[key],CultureInfo.InvariantCulture);}
        internal static void Pointer(IntPtr value,int slot,IntPtr parameter){Marshal.ThrowExceptionForHR(Slot<SetPointer>(value,slot)(value,parameter));}
        internal static void Integer(IntPtr value,int slot,int parameter){Marshal.ThrowExceptionForHR(Slot<SetInteger>(value,slot)(value,parameter));}
        static void Double(IntPtr value,int slot,double parameter){Marshal.ThrowExceptionForHR(Slot<SetDouble>(value,slot)(value,parameter));}

        internal sealed class Parent : IDisposable
        {
            internal IntPtr Pointer,Background;
            internal string Name;
            internal double Centre;
            internal bool Created,Added;
            internal object FactoryObject;
            public void Dispose(){if(Background!=IntPtr.Zero){Marshal.Release(Background);Background=IntPtr.Zero;}if(Pointer!=IntPtr.Zero){Marshal.Release(Pointer);Pointer=IntPtr.Zero;}if(FactoryObject!=null){Marshal.ReleaseComObject(FactoryObject);FactoryObject=null;}}
        }
        internal static List<Parent> Parents(IntPtr nativeDocument,IntPtr target)
        {
            var first=Export<FirstPrimary>("?GetFirstPeak@CProcessGridDoc@@QEBAPEAU__POSITION@@XZ",0x160c40);
            var next=Export<NextPrimary>("?GetNextPeak@CProcessGridDoc@@QEAAAEAVCPeakItem@@AEAPEAU__POSITION@@@Z",0x1610c0);
            var result=new List<Parent>();IntPtr targetIdentity=Query(target,"00000000-0000-0000-C000-000000000046");
            try
            {
                IntPtr position=first(nativeDocument);int guard=0;
                while(position!=IntPtr.Zero)
                {
                    if(++guard>10000)throw new InvalidOperationException("LIVE_PRIMARY_SIZE");
                    IntPtr item=next(nativeDocument,ref position),entry=Query(NativeContextIdentity.ReadField(item,0x60),"010DE959-68E5-11D2-851A-006097C81436");
                    IntPtr data=IntPtr.Zero,identity=IntPtr.Zero;bool retained=false;
                    try
                    {
                        try{Marshal.ThrowExceptionForHR(Slot<PointerOut>(entry,12)(entry,out data));}catch(NullReferenceException){continue;}
                        if(data==IntPtr.Zero)continue;identity=Query(data,"00000000-0000-0000-C000-000000000046");
                        if(identity!=targetIdentity)continue;
                        var parent=new Parent{Pointer=entry};
                        try
                        {
                            try{Marshal.ThrowExceptionForHR(Slot<PointerOut>(entry,10)(entry,out parent.Background));}catch(NullReferenceException){parent.Background=IntPtr.Zero;}
                            Marshal.ThrowExceptionForHR(Slot<GetText>(entry,14)(entry,out parent.Name));
                            double start,end,width;Marshal.ThrowExceptionForHR(Slot<GetAttributes>(entry,6)(entry,out start,out parent.Centre,out end,out width));
                            result.Add(parent);retained=true;
                        }
                        catch{if(parent.Background!=IntPtr.Zero)Marshal.Release(parent.Background);throw;}
                    }
                    finally{if(identity!=IntPtr.Zero)Marshal.Release(identity);if(data!=IntPtr.Zero)Marshal.Release(data);if(!retained)Marshal.Release(entry);}
                }
                return result;
            }
            catch{foreach(var parent in result)parent.Dispose();throw;}
            finally{Marshal.Release(targetIdentity);}
        }
        internal static Parent NewParent(IntPtr data,IntPtr background,IDictionary<string,object> peak,IDictionary<string,object> bg,Parent template)
        {
            var result=new Parent{Created=true};
            try
            {
                result.FactoryObject=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("010DE95A-68E5-11D2-851A-006097C81436")));
                IntPtr unknown=Marshal.GetIUnknownForObject(result.FactoryObject);try{result.Pointer=Query(unknown,"010DE959-68E5-11D2-851A-006097C81436");}finally{Marshal.Release(unknown);}
                if(template!=null)Pointer(result.Pointer,67,template.Pointer);
                Pointer(result.Pointer,57,IntPtr.Zero);Pointer(result.Pointer,11,data);Pointer(result.Pointer,9,background);Integer(result.Pointer,33,1);
                Marshal.ThrowExceptionForHR(Slot<SetText>(result.Pointer,13)(result.Pointer,(string)peak["label"]));
                if(template==null)
                {
                    Marshal.ThrowExceptionForHR(Slot<SetAttributes>(result.Pointer,5)(result.Pointer,Number(bg,"start_kinetic_ev"),Number(peak,"centre_kinetic_ev"),Number(bg,"end_kinetic_ev"),Number(peak,"fwhm_ev")));
                    Marshal.ThrowExceptionForHR(Slot<SetSubParameters>(result.Pointer,7)(result.Pointer,Number(peak,"height"),Number(peak,"area"),0,0));
                    Marshal.ThrowExceptionForHR(Slot<SetByte>(result.Pointer,35)(result.Pointer,0));Double(result.Pointer,51,0);
                }
                result.Centre=Number(peak,"centre_kinetic_ev");result.Name=(string)peak["label"];
                return result;
            }
            catch{result.Dispose();throw;}
        }

        internal static IAvantageBackground NewBackground(IDictionary<string,object> value)
        {
            string[] clsids={"C9D7ED53-5854-11D2-B66E-006097BE683A","C9D7ED55-5854-11D2-B66E-006097BE683A","C9D7ED57-5854-11D2-B66E-006097BE683A","C9D7ED59-5854-11D2-B66E-006097BE683A","10EEA3DF-59CF-40F8-8159-1C2ED4498C07"};
            int type=Integer(value,"type");var result=(IAvantageBackground)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid(clsids[type])));
            try
            {
                result.SetStart(Number(value,"start_kinetic_ev"));result.SetEnd(Number(value,"end_kinetic_ev"));result.SetMode(Integer(value,"mode"));
                result.SetPointsToAverage(Integer(value,"points_to_average"));result.SeteVgranularity(Number(value,"step_ev"));
                var ext=(IDictionary<string,object>)value["extensions"];
                if(type==1||type==2)
                {
                    IntPtr unknown=Marshal.GetIUnknownForObject(result),sub=IntPtr.Zero;
                    try
                    {
                        sub=Query(unknown,type==1?"C9D7ED54-5854-11D2-B66E-006097BE683A":"C9D7ED56-5854-11D2-B66E-006097BE683A");
                        if(type==1){Integer(sub,24,Integer(ext,"shirley_iterations"));Double(sub,25,Number(ext,"shirley_convergence"));Integer(sub,28,Integer(ext,"shirley_smooth"));}
                        else{Double(sub,28,Number(ext,"tougaard_y_offset"));Integer(sub,29,Integer(ext,"tougaard_type"));Double(sub,30,Number(ext,"tougaard_b"));Double(sub,31,Number(ext,"tougaard_c"));Double(sub,32,Number(ext,"tougaard_d"));Integer(sub,33,Integer(ext,"tougaard_energy_correction"));}
                    }
                    finally{if(sub!=IntPtr.Zero)Marshal.Release(sub);Marshal.Release(unknown);}
                }
                return result;
            }
            catch{Marshal.ReleaseComObject(result);throw;}
        }
        internal static void Values(IAvFittedPeak peak,IDictionary<string,object> values)
        {
            int existing;peak.GetGLCombineMethod(out existing);int method=Integer(values,"native_gl_combine_method");if(existing!=method)peak.SetGLCombineMethod(method);
            peak.SetAsymParams(Number(values,"area"),Number(values,"area_ratio"),Number(values,"centre_kinetic_ev"),Number(values,"height"),Number(values,"height_ratio"),Number(values,"fwhm_ev"),Number(values,"gaussian_lorentz"),Number(values,"tail_mix"),Number(values,"tail_height"),Number(values,"tail_exponential"));peak.SetName((string)values["label"]);
        }
        internal static void Constraints(IList<IAvFittedPeak> peaks,IList<Dictionary<string,object>> values)
        {
            var pointers=new List<IntPtr>();
            try
            {
                foreach(var peak in peaks)pointers.Add(Marshal.GetComInterfaceForObject(peak,typeof(IAvFittedPeak)));
                for(int i=0;i<peaks.Count;i++)
                {
                    var desired=new Dictionary<int,IDictionary<string,object>>();
                    foreach(var item in (System.Collections.IEnumerable)values[i]["constraints"]){var c=(IDictionary<string,object>)item;desired.Add(Integer(c,"attribute"),c);}
                    for(int attr=1;attr<=8;attr++)
                    {
                        IntPtr raw=IntPtr.Zero;IAvPeakConstraint constraint=null;
                        try
                        {
                            peaks[i].GetPeakFitConstraint(attr,out raw);
                            if(!desired.ContainsKey(attr)){if(raw!=IntPtr.Zero)peaks[i].RemovePeakFitConstraint(raw,attr);continue;}
                            bool create=raw==IntPtr.Zero;
                            constraint=create?(IAvPeakConstraint)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("68F7DAF5-B53B-11D2-988E-00C04F8EBE0F"))):(IAvPeakConstraint)Marshal.GetUniqueObjectForIUnknown(raw);
                            var c=desired[attr];IntPtr reference=c["reference_peak_index"]==null?IntPtr.Zero:pointers[Integer(c,"reference_peak_index")];
                            constraint.SetParameters(Integer(c,"type"),Number(c,"value1"),Number(c,"value2"),Number(c,"value3"),reference,Integer(c,"operator"));
                            if(create){raw=Marshal.GetComInterfaceForObject(constraint,typeof(IAvPeakConstraint));peaks[i].CreateInitialPeakFitConstraint(attr,raw);}
                        }
                        finally{if(constraint!=null)Marshal.ReleaseComObject(constraint);if(raw!=IntPtr.Zero)Marshal.Release(raw);}
                    }
                }
            }
            finally{foreach(var p in pointers)Marshal.Release(p);}
        }
    }
}
