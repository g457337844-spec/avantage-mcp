using System;
using System.Reflection;
using System.Runtime.InteropServices;
using AvantageMcp.InProcess;

class NativeContextIdentityTests
{
    [DllImport("kernel32.dll")]static extern IntPtr VirtualAlloc(IntPtr address,UIntPtr size,uint allocation,uint protection);
    [DllImport("kernel32.dll")]static extern bool VirtualProtect(IntPtr address,UIntPtr size,uint protection,out uint previous);
    [DllImport("kernel32.dll")]static extern bool VirtualFree(IntPtr address,UIntPtr size,uint freeType);
    static int count;
    static void Check(bool value,string message){if(!value)throw new Exception(message);count++;Console.WriteLine("PASS "+message);}
    static object Invoke(string method,params object[] args){return typeof(NativeContextIdentity).GetMethod(method,BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,args);}
    static void Reject(Action action,string message){bool rejected=false;try{action();}catch(TargetInvocationException e){rejected=e.InnerException is InvalidOperationException;}catch(InvalidOperationException){rejected=true;}Check(rejected,message);}
    [STAThread]static int Main(){
        try{
            Reject(()=>NativeContextIdentity.Inspect(IntPtr.Zero),"null context rejected");
            Reject(()=>NativeContextIdentity.Inspect(new IntPtr(1)),"wrong process rejected before pointer access");
            Reject(()=>Invoke("ReadPointer",IntPtr.Zero),"zero memory rejected");
            Reject(()=>Invoke("ReadPointer",new IntPtr(1)),"uncommitted memory rejected");
            Reject(()=>Invoke("AddOffset",new IntPtr(long.MaxValue-1),8L,"overflow"),"pointer addition overflow rejected");
            Reject(()=>Invoke("SubtractOffset",new IntPtr(8),8L,"underflow"),"pointer subtraction zero rejected");
            IntPtr memory=VirtualAlloc(IntPtr.Zero,new UIntPtr(8192),0x3000,4);
            Check(memory!=IntPtr.Zero,"fixture pages allocated");
            try{
                Marshal.WriteIntPtr(memory,new IntPtr(123456));
                Check((IntPtr)Invoke("ReadPointer",memory)==new IntPtr(123456),"read returns field value rather than field address");
                uint previous;Check(VirtualProtect(IntPtr.Add(memory,4096),new UIntPtr(4096),1,out previous),"fixture no-access page installed");
                Reject(()=>Invoke("ReadPointer",IntPtr.Add(memory,4096)),"no-access page rejected before read");
                Reject(()=>Invoke("RequireReadable",IntPtr.Add(memory,4092),8),"cross-region read rejected");
                Check(VirtualProtect(memory,new UIntPtr(4096),0x104,out previous),"fixture guard page installed");
                Reject(()=>Invoke("ReadPointer",memory),"guard page rejected before read");
            }finally{VirtualFree(memory,UIntPtr.Zero,0x8000);}
            IntPtr text=Marshal.StringToBSTR("E:\\测试\\2.vgp");
            try{Check((string)Invoke("ReadBoundedBstr",text)=="E:\\测试\\2.vgp","bounded Unicode BSTR read");}finally{Marshal.FreeBSTR(text);}
            text=Marshal.StringToBSTR(new string('x',17000));
            try{Reject(()=>Invoke("ReadBoundedBstr",text),"oversized BSTR rejected");}finally{Marshal.FreeBSTR(text);}
            var probe=new IdentityProbe();IntPtr unk=Marshal.GetIUnknownForObject(probe),control=IntPtr.Zero;
            try{Guid iid=typeof(IDataProcessControl).GUID;Check(Marshal.QueryInterface(unk,ref iid,out control)==0,"derived identity probe exposes inherited control interface");}
            finally{if(control!=IntPtr.Zero)Marshal.Release(control);Marshal.Release(unk);}
            Console.WriteLine("TOTAL "+count);return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
