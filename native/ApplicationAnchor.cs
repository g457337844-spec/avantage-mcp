using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;

namespace AvantageMcp {
[ComVisible(true),Guid("5F13B24E-919B-4A67-A8BB-A93F741E5D5C"),ClassInterface(ClassInterfaceType.AutoDispatch)]
public sealed class LiveAnchorAccess {
    readonly object ui;
    public LiveAnchorAccess(object value){ui=value;}
    public object GetUi(){return ui;}
}
// A vendor single-object factory consumes the interactive owner's reference on
// activation. A proxy-local AddRef cannot repair a server-side owner reference
// after that proxy process exits. Activate once per application lifetime and
// retain that connection in a separate STA anchor. Other workers obtain the
// existing application interface through the standard running object table.
internal static class ApplicationAnchor {
    internal static bool Enabled {get{return Environment.GetEnvironmentVariable("AVANTAGE_DISABLE_LIVE")!="1";}}
    [DllImport("user32.dll")]static extern bool IsWindowEnabled(IntPtr window);
    internal static void RequireReady(Process application){
        application.Refresh();
        if(application.HasExited||!application.Responding)throw new Fault("APP_NOT_READY","Target application is unavailable.");
        if(application.MainWindowHandle==IntPtr.Zero||!IsWindowEnabled(application.MainWindowHandle))throw new Fault("APP_MODAL_OR_STARTING","Finish the application startup or dismiss its modal dialog before connecting. No application activation was sent.");
    }
    [DllImport("ole32.dll")]static extern int GetRunningObjectTable(uint reserved,out IRunningObjectTable table);
    [DllImport("ole32.dll",CharSet=CharSet.Unicode)]static extern int CreateItemMoniker(string delimiter,string item,out IMoniker moniker);
    [DllImport("ole32.dll")]static extern int CoCreateInstance(ref Guid cls,IntPtr outer,uint context,ref Guid iid,out IntPtr value);
    [DllImport("ole32.dll")]static extern int CoWaitForMultipleHandles(uint flags,uint timeout,uint count,IntPtr[] handles,out uint index);
    [DllImport("ole32.dll")]static extern int OleInitialize(IntPtr reserved);
    [StructLayout(LayoutKind.Sequential)]struct SecurityAttributes {public int Length;public IntPtr Descriptor;[MarshalAs(UnmanagedType.Bool)]public bool Inherit;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct StartupInfo {public int Size;public string Reserved,Desktop,Title;public int X,Y,Width,Height,CharsX,CharsY,Fill,Flags;public short Show,ReservedSize;public IntPtr ReservedBytes,Input,Output,Error;}
    [StructLayout(LayoutKind.Sequential)]struct ExtendedStartup {public StartupInfo Info;public IntPtr Attributes;}
    [StructLayout(LayoutKind.Sequential)]struct ProcessInformation {public IntPtr Process,Thread;public uint Pid,Tid;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr CreateFile(string path,uint access,uint sharing,ref SecurityAttributes security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,uint flags,ref IntPtr bytes);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,IntPtr attribute,IntPtr value,IntPtr size,IntPtr previous,IntPtr returned);
    [DllImport("kernel32.dll")]static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool CreateProcess(string application,System.Text.StringBuilder command,IntPtr processSecurity,IntPtr threadSecurity,bool inherit,uint flags,IntPtr environment,string directory,ref ExtendedStartup startup,out ProcessInformation created);
    static void StartDetachedAnchor(Process process){
        // Whitelist only NUL. Process.Start on .NET Framework can inherit other
        // pipe handles, keeping an MCP client's reader blocked after worker exit.
        var security=new SecurityAttributes{Length=Marshal.SizeOf(typeof(SecurityAttributes)),Inherit=true};
        IntPtr nul=CreateFile("NUL",0xC0000000,3,ref security,3,0,IntPtr.Zero);
        if(nul==new IntPtr(-1))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        IntPtr list=IntPtr.Zero,handles=IntPtr.Zero;bool initialized=false;
        try{
            IntPtr size=IntPtr.Zero;InitializeProcThreadAttributeList(IntPtr.Zero,1,0,ref size);
            if(size==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            list=Marshal.AllocHGlobal(size);
            if(!InitializeProcThreadAttributeList(list,1,0,ref size))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            initialized=true;handles=Marshal.AllocHGlobal(IntPtr.Size);Marshal.WriteIntPtr(handles,nul);
            if(!UpdateProcThreadAttribute(list,0,new IntPtr(0x20002),handles,new IntPtr(IntPtr.Size),IntPtr.Zero,IntPtr.Zero))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var startup=new ExtendedStartup{Info=new StartupInfo{Size=Marshal.SizeOf(typeof(ExtendedStartup)),Flags=0x100,Input=nul,Output=nul,Error=nul},Attributes=list};
            string executable=Assembly.GetExecutingAssembly().Location;
            var command=new System.Text.StringBuilder("\""+executable+"\" --live-anchor "+process.Id+" "+process.StartTime.ToUniversalTime().Ticks);
            ProcessInformation created;
            if(!CreateProcess(executable,command,IntPtr.Zero,IntPtr.Zero,true,0x08080000,IntPtr.Zero,null,ref startup,out created))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            CloseHandle(created.Thread);CloseHandle(created.Process);
        }finally{if(initialized)DeleteProcThreadAttributeList(list);if(list!=IntPtr.Zero)Marshal.FreeHGlobal(list);if(handles!=IntPtr.Zero)Marshal.FreeHGlobal(handles);CloseHandle(nul);}
    }
    static string Identity(Process process){return "AvantageMCP.UI."+process.Id+"."+process.StartTime.ToUniversalTime().Ticks;}
    static object Existing(string name){
        IRunningObjectTable rot=null;IMoniker moniker=null;
        try{Marshal.ThrowExceptionForHR(GetRunningObjectTable(0,out rot));Marshal.ThrowExceptionForHR(CreateItemMoniker("!",name,out moniker));object value;int hr=rot.GetObject(moniker,out value);if(hr==unchecked((int)0x800401E3))return null;Marshal.ThrowExceptionForHR(hr);return value;}
        finally{if(moniker!=null)Marshal.ReleaseComObject(moniker);if(rot!=null)Marshal.ReleaseComObject(rot);}
    }
    internal static IntPtr Connect(Process process){
        if(!Enabled)throw new Fault("LIVE_CONNECTION_DISABLED","Live application connections were disabled by AVANTAGE_DISABLE_LIVE.");
        RequireReady(process);
        string name=Identity(process);object value=Existing(name);
        if(value==null){
            StartDetachedAnchor(process);
            var timer=Stopwatch.StartNew();
            while(value==null&&timer.ElapsedMilliseconds<15000){if(process.HasExited)throw new Fault("APP_EXITED","The original application exited before connection. No new application was started.");Thread.Sleep(100);value=Existing(name);}
            if(value==null)throw new Fault("ANCHOR_NOT_READY","The per-application live anchor did not become ready. Do not repeat activation; inspect its diagnostic log or restart the test application.");
        }
        IntPtr unknown=IntPtr.Zero;object remoteUi=null;
        try{dynamic access=value;remoteUi=access.GetUi();unknown=Marshal.GetIUnknownForObject(remoteUi);return ProcessingCom.Query(unknown,ProcessingCom.UiId);}
        finally{if(unknown!=IntPtr.Zero)Marshal.Release(unknown);if(remoteUi!=null)Marshal.ReleaseComObject(remoteUi);Marshal.ReleaseComObject(value);}
    }
    internal static int Run(int pid,long ticks){
        Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
        using(var application=Process.GetProcessById(pid)){
            if(application.StartTime.ToUniversalTime().Ticks!=ticks||!String.Equals(application.ProcessName,"Avantage",StringComparison.OrdinalIgnoreCase))return 2;
            string name=Identity(application);bool held=false;
            using(var gate=new Mutex(false,@"Local\"+name)){
                try{held=gate.WaitOne(0);}catch(AbandonedMutexException){held=true;}
                if(!held)return 0;
                IRunningObjectTable rot=null;IMoniker moniker=null;object app=null,uiObject=null;IntPtr raw=IntPtr.Zero,ui=IntPtr.Zero;int cookie=0;
                string logs=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AvantageMCP","anchors");Directory.CreateDirectory(logs);string log=Path.Combine(logs,name+".log");
                Action<string> record=message=>File.AppendAllText(log,DateTime.UtcNow.ToString("O")+" "+message+Environment.NewLine);
                try{
                    try{
                        RequireReady(application);
                        Guid cls=new Guid("052CC3C7-B7E2-40C3-92F5-3FA00FA81C1D"),dispatch=new Guid("00020400-0000-0000-C000-000000000046");
                        Marshal.ThrowExceptionForHR(CoCreateInstance(ref cls,IntPtr.Zero,4,ref dispatch,out raw));
                        record("application activation returned");
                        app=Marshal.GetObjectForIUnknown(raw);dynamic automation=app;
                        record("application RCW created");
                        if(!(bool)automation.ManualMode)automation.ManualMode=true;
                        if((bool)automation.RemoteMode)automation.RemoteMode=false;
                        if(application.HasExited||(bool)automation.RemoteMode)throw new Fault("APP_IDENTITY_CHANGED","The original interactive application did not retain its mode.");
                        record("interactive mode restored");
                        ui=ProcessingCom.Query(raw,ProcessingCom.UiId);uiObject=Marshal.GetObjectForIUnknown(ui);
                        Marshal.ThrowExceptionForHR(GetRunningObjectTable(0,out rot));Marshal.ThrowExceptionForHR(CreateItemMoniker("!",name,out moniker));cookie=rot.Register(1,new LiveAnchorAccess(uiObject),moniker);
                        record("ready; one activation; standard ROT interface published");
                    }catch(Exception error){record("connection unavailable: "+error.ToString());}
                    // Keep the retained application references alive even when ROT
                    // publication failed. Never drop them while the GUI is running.
                    // Pump COM calls on this STA; MCP worker EOF must not stop us.
                    while(!application.HasExited){uint index;CoWaitForMultipleHandles(0x18,1000,1,new[]{application.Handle},out index);}
                    record("target application exited; releasing anchor");
                }finally{
                    if(cookie!=0&&rot!=null)try{rot.Revoke(cookie);}catch(COMException){}
                    if(uiObject!=null)Marshal.ReleaseComObject(uiObject);if(ui!=IntPtr.Zero)Marshal.Release(ui);if(app!=null)Marshal.ReleaseComObject(app);if(raw!=IntPtr.Zero)Marshal.Release(raw);
                    if(moniker!=null)Marshal.ReleaseComObject(moniker);if(rot!=null)Marshal.ReleaseComObject(rot);if(held)gate.ReleaseMutex();
                }
            }
        }
        return 0;
    }
}
}
