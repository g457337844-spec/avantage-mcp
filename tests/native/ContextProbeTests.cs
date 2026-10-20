using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using AvantageMcp.InProcess;

[ComImport, Guid("0000000B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface TestStorage {
    void CreateStream([MarshalAs(UnmanagedType.LPWStr)] string name, uint mode, uint r1, uint r2, out IStream stream);
    void OpenStream([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr r1, uint mode, uint r2, out IStream stream);
}
class ProbeTests {
    [DllImport("ole32.dll", PreserveSig=false)] static extern void CreateILockBytesOnHGlobal(IntPtr mem, [MarshalAs(UnmanagedType.Bool)] bool delete, out IntPtr bytes);
    [DllImport("ole32.dll", PreserveSig=false)] static extern void StgCreateDocfileOnILockBytes(IntPtr bytes, uint mode, uint reserved, out IntPtr storage);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Control(IntPtr self, IntPtr processing, IntPtr storage, int n, IntPtr inputs, IntPtr axes, out int outputCount, out IntPtr outputs, int flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int StringResult(IntPtr self, out IntPtr text);
    static int passed;
    static void Check(bool condition,string label) { if(!condition) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
    static object Invoke(string name,params object[] args) { return typeof(ContextProbe).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,args); }
    static void Put(TestStorage storage,string name,byte[] data) {
        IStream stream; storage.CreateStream(name,0x1012,0,0,out stream);
        try { stream.Write(data,data.Length,IntPtr.Zero); stream.Commit(0); } finally { Marshal.ReleaseComObject(stream); }
    }
    static byte[] Get(TestStorage storage,string name) {
        IStream stream; storage.OpenStream(name,IntPtr.Zero,0x10,0,out stream);
        try { System.Runtime.InteropServices.ComTypes.STATSTG st;stream.Stat(out st,1);var b=new byte[(int)st.cbSize]; stream.Read(b,b.Length,IntPtr.Zero);return b; } finally { Marshal.ReleaseComObject(stream); }
    }
    static int HostCallbackTests() {
        IntPtr bytes,ptr;CreateILockBytesOnHGlobal(IntPtr.Zero,true,out bytes);
        try { StgCreateDocfileOnILockBytes(bytes,0x1012,0,out ptr); } finally { Marshal.Release(bytes); }
        var store=(TestStorage)Marshal.GetObjectForIUnknown(ptr);
        IntPtr context=Marshal.GetIUnknownForObject(new object());
        try {
            var probe=new ContextProbe();int count;IntPtr outputs;
            Check(probe.ControlProcess(context,ptr,0,IntPtr.Zero,IntPtr.Zero,out count,out outputs,0)==unchecked((int)0x80070057),"host callback rejects missing marker");
            bool absent=false;try{Get(store,"AvantageMcp.Context");}catch(COMException){absent=true;}Check(absent,"missing marker writes nothing");
            Put(store,"AvantageMcp.ProbeRequest",Encoding.UTF8.GetBytes("inspect-context-v1"));
            Check(probe.ControlProcess(context,ptr,33,IntPtr.Zero,IntPtr.Zero,out count,out outputs,0)==unchecked((int)0x80070057),"host callback rejects excessive input count");
            Check(probe.QuerySummaryAttributes(ptr)==0,"summary callback accepts host storage without mutation");
            Check(probe.ControlProcess(context,ptr,0,IntPtr.Zero,IntPtr.Zero,out count,out outputs,0)==unchecked((int)0x80004004),"valid diagnostic returns cancellation for host cleanup");
            Check(count==0&&outputs==IntPtr.Zero,"successful diagnostic produces no data outputs");
            var text=Encoding.UTF8.GetString(Get(store,"AvantageMcp.Context"));
            Check(text.Contains("\"probe_completed\":true")&&text.Contains("\"grid_interface_available\":false"),"complete diagnostic handles unsupported context QI");
            Check(text.Contains("\"main_window_present\":false")&&text.Contains("\"main_window_process_id\":0"),"windowless host context handled");
            Console.WriteLine("TOTAL "+passed);return 0;
        } finally {Marshal.Release(context);Marshal.ReleaseComObject(store);Marshal.Release(ptr);}
    }
    [STAThread] static int Main(string[] args) {
        try {
            if(args.Length==1&&args[0]=="--host-callback")return HostCallbackTests();
            Check(typeof(IDataProcessControl).GUID==new Guid("597365F0-21FB-11D2-B9D9-006097D69B02"),"control IID");
            Check(typeof(IDataProcess).GUID==new Guid("3FB981F0-56AB-11D1-8AD5-006097D69B02"),"process IID");
            string[] control={"ControlProcess","Reprocess","SetProcessingSchema"};
            string[] process={"QuerySummaryAttributes","QueryProcessAttributes","ExecuteProcess","PropertiesToString","SetProcessAttributes","ProcessControlComplete"};
            foreach(var pair in new[]{Tuple.Create(typeof(IDataProcessControl),control),Tuple.Create(typeof(IDataProcess),process)}) {
                var methods=pair.Item1.GetMethods(); Check(methods.Length==pair.Item2.Length,"method count " + pair.Item1.Name);
                Array.Sort(methods,(a,b)=>a.MetadataToken.CompareTo(b.MetadataToken));
                for(int i=0;i<methods.Length;i++) Check(methods[i].Name==pair.Item2[i] && (methods[i].MethodImplementationFlags & MethodImplAttributes.PreserveSig)!=0,"slot " + pair.Item1.Name+"."+pair.Item2[i]);
            }
            IntPtr bytes,ptr;CreateILockBytesOnHGlobal(IntPtr.Zero,true,out bytes);
            try { StgCreateDocfileOnILockBytes(bytes,0x1012,0,out ptr); } finally { Marshal.Release(bytes); }
            var store=(TestStorage)Marshal.GetObjectForIUnknown(ptr);
            try {
                Check(!(bool)Invoke("HasRequestMarker",ptr),"missing marker rejected");
                Put(store,"Sentinel",new byte[]{1,2,3});
                foreach(var invalid in new[]{new byte[0],new byte[65],new byte[]{0xff,0xfe},Encoding.UTF8.GetBytes("inspect-context-v2")}) {
                    Put(store,"AvantageMcp.ProbeRequest",invalid);
                    Check(!(bool)Invoke("HasRequestMarker",ptr),"invalid marker rejected length="+invalid.Length);
                }
                Put(store,"AvantageMcp.ProbeRequest",Encoding.UTF8.GetBytes("  inspect-context-v1\r\n"));
                Check((bool)Invoke("HasRequestMarker",ptr),"valid trimmed UTF8 marker accepted");
                var doc=new Dictionary<string,object>{{"probe","inspect-context-v1"},{"value",42}};
                Check((bool)Invoke("WriteContext",ptr,doc),"standard IStorage write succeeds");
                Check(Encoding.UTF8.GetString(Get(store,"AvantageMcp.Context")).Contains("42"),"standard IStorage readback exact");
                Check(Get(store,"Sentinel")[2]==3,"unrelated stream retained");
                var prior=Convert.ToBase64String(Get(store,"AvantageMcp.Context"));
                Check(!(bool)Invoke("WriteContext",ptr,new Dictionary<string,object>{{"large",new string('中',2000)}}),"UTF8 byte bound rejects oversized output");
                Check(Convert.ToBase64String(Get(store,"AvantageMcp.Context"))==prior,"oversized output preserves existing context");
                var probe=new ContextProbe();
                IntPtr unk=Marshal.GetIUnknownForObject(probe);IntPtr cp=IntPtr.Zero,dp=IntPtr.Zero;
                try {
                    Guid iid=typeof(IDataProcessControl).GUID;Check(Marshal.QueryInterface(unk,ref iid,out cp)==0,"CCW exposes control");
                    iid=typeof(IDataProcess).GUID;Check(Marshal.QueryInterface(unk,ref iid,out dp)==0,"CCW exposes process");
                    var call=(Control)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(cp),3*IntPtr.Size),typeof(Control));
                    int count=99;IntPtr outputs=new IntPtr(1);
                    Check(call(cp,new IntPtr(1),ptr,0,IntPtr.Zero,IntPtr.Zero,out count,out outputs,0)<0 && count==0 && outputs==IntPtr.Zero,"native callback rejects non-Avantage host without touching borrowed pointer");
                    Check(Convert.ToBase64String(Get(store,"AvantageMcp.Context"))==prior,"rejected callback writes nothing");
                    var label=(StringResult)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(dp),6*IntPtr.Size),typeof(StringResult));
                    IntPtr text;Check(label(dp,out text)==0 && text!=IntPtr.Zero,"native PropertiesToString BSTR");
                    try { Check(Marshal.PtrToStringBSTR(text).Contains("read-only"),"BSTR content"); } finally {Marshal.FreeBSTR(text);}
                } finally {if(cp!=IntPtr.Zero)Marshal.Release(cp);if(dp!=IntPtr.Zero)Marshal.Release(dp);Marshal.Release(unk);}
            } finally { Marshal.ReleaseComObject(store);Marshal.Release(ptr); }
            Console.WriteLine("TOTAL " + passed);return 0;
        } catch(Exception e) {Console.Error.WriteLine(e);return 1;}
    }
}
