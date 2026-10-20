// Fixed-copy-only creation/deletion of a new native primary entry and its child.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AvantageMcp.InProcess
{
    [ComVisible(true)]
    [Guid("96D0A0F0-56FC-4305-92B0-0BA9CB776AB5")]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class EmptyRegionProbe : ContextProbe
    {
        const string Label="MCP empty region ownership probe";
        bool cleanup;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int AddPrimary(IntPtr doc,IntPtr entry);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int AddFitted(IntPtr doc,IntPtr peak,IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int RemoveItem(IntPtr doc,IntPtr item,int update);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int PrimaryCount(IntPtr doc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int SetPointer(IntPtr self,IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int SetInteger(IntPtr self,int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int SetText(IntPtr self,[MarshalAs(UnmanagedType.BStr)]string value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int GetText(IntPtr self,[MarshalAs(UnmanagedType.BStr)]out string value);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern IntPtr GetModuleHandleW(string name);
        [DllImport("kernel32.dll",CharSet=CharSet.Ansi,ExactSpelling=true)]static extern IntPtr GetProcAddress(IntPtr module,string name);
        protected override bool ValidateRequest(IntPtr storage)
        {
            if(!base.ValidateRequest(storage))return false;
            cleanup=HasMarker(storage,"AvantageMcp.EmptyRequest","remove-empty-test-v1");
            return cleanup||HasMarker(storage,"AvantageMcp.EmptyRequest","create-empty-test-v1");
        }
        protected override Dictionary<string,object> CollectContext(IntPtr processing,bool gridAvailable,int inputCount,IntPtr inputs,IntPtr axes,int flags)
        {
            if(inputCount!=0||inputs!=IntPtr.Zero||axes!=IntPtr.Zero)throw new InvalidOperationException("No external native objects accepted");
            var report=base.CollectContext(processing,gridAvailable,inputCount,inputs,axes,flags);
            report["native_identity"]=NativeContextIdentity.Inspect(processing);
            report["empty_region"]=NativeContextIdentity.WithDocument(processing,Execute);
            return report;
        }
        static T Export<T>(string name,int rva)where T:class
        {
            IntPtr module=GetModuleHandleW("ProcessGrid.dll"),address=GetProcAddress(module,name);
            if(module==IntPtr.Zero||address!=IntPtr.Add(module,rva))throw new InvalidOperationException("Empty test native ABI mismatch");
            return Marshal.GetDelegateForFunctionPointer(address,typeof(T))as T;
        }
        static T Slot<T>(IntPtr value,int index)where T:class{return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(value),index*8),typeof(T))as T;}
        static IntPtr Entry(object value)
        {
            IntPtr unknown=Marshal.GetIUnknownForObject(value);try{Guid iid=new Guid("010DE959-68E5-11D2-851A-006097C81436");IntPtr entry;Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown,ref iid,out entry));return entry;}finally{Marshal.Release(unknown);}
        }
        static int Count(IntPtr document,IntPtr data){var peaks=NativeDocumentAccess.Peaks(document,data);try{return peaks.Count;}finally{foreach(var p in peaks)Marshal.ReleaseComObject(p);}}
        Dictionary<string,object> Execute(IntPtr nativeDoc,IntPtr doc)
        {
            var addPrimary=Export<AddPrimary>("?AddPeak@CProcessGridDoc@@QEAAHPEAUIPeakTableEntry@@@Z",0x15ac10);
            var addFitted=Export<AddFitted>("?AddFittedPeak@CProcessGridDoc@@QEAAHPEAUIFittedPeak@@PEAUIDataSpace@@@Z",0x15a310);
            var removeFitted=Export<RemoveItem>("?RemoveFittedPeak@CProcessGridDoc@@QEAAHPEAUIFittedPeak@@H@Z",0x167660);
            var removePrimary=Export<RemoveItem>("?RemovePeak@CProcessGridDoc@@QEAAHPEAUIPeakTableEntry@@H@Z",0x167960);
            var countPrimary=Export<PrimaryCount>("?GetNumPeaks@CProcessGridDoc@@QEAAHXZ",0x161380);
            var data=NativeDocumentAccess.DataSpaces(doc);var held=new List<IAvFittedPeak>();
            object primaryObject=null;IAvantageBackground cloneBackground=null;
            IntPtr primary=IntPtr.Zero,parentTemplate=IntPtr.Zero,background=IntPtr.Zero,clonedBg=IntPtr.Zero,fittedPointer=IntPtr.Zero;
            IAvFittedPeak candidate=null;bool addedPrimary=false,addedFitted=false,complete=false;
            int primaryBefore=countPrimary(nativeDoc),primaryAfter=primaryBefore,before=0,after=0;
            try
            {
                if(data.Count!=24)throw new InvalidOperationException("Requires fixed 24-spectrum copy");
                held=NativeDocumentAccess.Peaks(doc,data[5]);before=held.Count;
                if(cleanup)
                {
                    if(before!=1)throw new InvalidOperationException("Empty cleanup requires one test candidate");
                    candidate=held[0];string name;candidate.GetName(out name);if(name!=Label)throw new InvalidOperationException("Not the empty test candidate");
                    candidate.GetPeakEntry(out primary);string entryName;Marshal.ThrowExceptionForHR(Slot<GetText>(primary,14)(primary,out entryName));
                    if(entryName!=Label)throw new InvalidOperationException("Not the empty test primary");
                    fittedPointer=Marshal.GetComInterfaceForObject(candidate,typeof(IAvFittedPeak));
                    if(removeFitted(nativeDoc,fittedPointer,0)!=1)throw new InvalidOperationException("Empty test child cleanup refused");
                    if(removePrimary(nativeDoc,primary,0)!=1)throw new InvalidOperationException("Empty test primary cleanup refused");
                }
                else
                {
                    if(before!=0)throw new InvalidOperationException("Empty test requires no fitted peaks at index5");
                    var templates=NativeDocumentAccess.Peaks(doc,data[23]);held.AddRange(templates);
                    if(templates.Count!=6)throw new InvalidOperationException("Requires six template components");
                    var source=templates[0];source.GetPeakEntry(out parentTemplate);source.GetBackground(out background);
                    var sourceBg=(IAvantageBackground)Marshal.GetUniqueObjectForIUnknown(background);
                    try{sourceBg.CloneBackground(out cloneBackground);}finally{Marshal.ReleaseComObject(sourceBg);}
                    clonedBg=Marshal.GetComInterfaceForObject(cloneBackground,typeof(IAvantageBackground));
                    primaryObject=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("010DE95A-68E5-11D2-851A-006097C81436")));primary=Entry(primaryObject);
                    Marshal.ThrowExceptionForHR(Slot<SetPointer>(primary,67)(primary,parentTemplate));
                    Marshal.ThrowExceptionForHR(Slot<SetPointer>(primary,57)(primary,IntPtr.Zero));
                    Marshal.ThrowExceptionForHR(Slot<SetPointer>(primary,11)(primary,data[5]));
                    Marshal.ThrowExceptionForHR(Slot<SetPointer>(primary,9)(primary,clonedBg));
                    Marshal.ThrowExceptionForHR(Slot<SetInteger>(primary,33)(primary,1));
                    Marshal.ThrowExceptionForHR(Slot<SetText>(primary,13)(primary,Label));
                    double a,ar,c,h,hr,w,g,tm,th,te;int combine;
                    source.GetAsymParams(out a,out ar,out c,out h,out hr,out w,out g,out tm,out th,out te);source.GetGLCombineMethod(out combine);
                    candidate=(IAvFittedPeak)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("B90B1A14-A54F-11D2-852A-00104B053B4C")));
                    candidate.SetDataSpace(data[5]);candidate.SetBackground(clonedBg);candidate.SetPeakEntry(primary);
                    candidate.SetAsymParams(a,ar,c,h,hr,w,g,tm,th,te);candidate.SetGLCombineMethod(combine);candidate.SetName(Label);
                    fittedPointer=Marshal.GetComInterfaceForObject(candidate,typeof(IAvFittedPeak));
                    if(addPrimary(nativeDoc,primary)!=1)throw new InvalidOperationException("Empty test primary insertion refused");addedPrimary=true;
                    if(addFitted(nativeDoc,fittedPointer,data[5])!=1)throw new InvalidOperationException("Empty test child insertion refused");addedFitted=true;
                }
                after=Count(doc,data[5]);primaryAfter=countPrimary(nativeDoc);
                if(after!=(cleanup?0:1)||primaryAfter!=primaryBefore+(cleanup?-1:1))throw new InvalidOperationException("Empty test count mismatch");
                complete=true;
                return new Dictionary<string,object>{{"spectrum_index",5},{"before",before},{"after",after},{"primary_before",primaryBefore},{"primary_after",primaryAfter},{"cleanup",cleanup},{"file_save_sent",false}};
            }
            finally
            {
                try
                {
                    if(!cleanup&&!complete)
                    {
                        if(addedFitted&&removeFitted(nativeDoc,fittedPointer,0)!=1)throw new InvalidOperationException("Empty test child rollback failed");
                        if(addedPrimary&&removePrimary(nativeDoc,primary,0)!=1)throw new InvalidOperationException("Empty test primary rollback failed");
                    }
                }
                finally
                {
                    if(fittedPointer!=IntPtr.Zero)Marshal.Release(fittedPointer);
                    if(!cleanup&&candidate!=null)Marshal.ReleaseComObject(candidate);
                    if(primary!=IntPtr.Zero)Marshal.Release(primary);
                    if(primaryObject!=null)Marshal.ReleaseComObject(primaryObject);
                    if(clonedBg!=IntPtr.Zero)Marshal.Release(clonedBg);
                    if(cloneBackground!=null)Marshal.ReleaseComObject(cloneBackground);
                    if(background!=IntPtr.Zero)Marshal.Release(background);
                    if(parentTemplate!=IntPtr.Zero)Marshal.Release(parentTemplate);
                    foreach(var p in held)Marshal.ReleaseComObject(p);
                    foreach(var p in data)Marshal.Release(p);
                }
            }
        }
    }
}
