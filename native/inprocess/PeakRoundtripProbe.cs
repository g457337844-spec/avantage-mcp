// Experimental, fixed-test-document-only reversible ownership probe.
// This is not exposed by the production MCP. No fit, data or file saving.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AvantageMcp.InProcess
{
    [ComVisible(true)]
    [Guid("96D0A0F0-56FC-4305-92B0-0BA9CB776AB2")]
    [ClassInterface(ClassInterfaceType.None)]
    public class PeakRoundtripProbe : ContextProbe
    {
        protected virtual bool RetainCandidate { get { return false; } }
        protected virtual bool CleanupOnly { get { return false; } }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int PointerOut(IntPtr self, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int NoArgs(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int PeaksOut(IntPtr self, IntPtr data, out IntPtr values, out int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int AddPeak(IntPtr self, IntPtr peak, IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int RemovePeak(IntPtr self, IntPtr peak, int update);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);
        [DllImport("kernel32.dll",CharSet=CharSet.Ansi,ExactSpelling=true)] static extern IntPtr GetProcAddress(IntPtr module,string name);

        protected override bool ValidateRequest(IntPtr storage)
        {
            return base.ValidateRequest(storage) && HasMarker(storage,
                "AvantageMcp.RoundtripRequest", "add-remove-one-existing-region-v1");
        }

        protected override Dictionary<string,object> CollectContext(IntPtr processing, bool gridAvailable,
            int inputCount, IntPtr inputs, IntPtr axes, int flags)
        {
            if(inputCount!=0||inputs!=IntPtr.Zero||axes!=IntPtr.Zero)throw new InvalidOperationException("roundtrip requires no external data objects");
            var report=base.CollectContext(processing,gridAvailable,inputCount,inputs,axes,flags);
            report["native_identity"]=NativeContextIdentity.Inspect(processing);
            report["peak_roundtrip"]=NativeContextIdentity.WithDocument(processing,Roundtrip);
            return report;
        }

        static T Slot<T>(IntPtr p,int slot) where T:class
        {return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(p),slot*8),typeof(T))as T;}

        static IntPtr[] Peaks(IntPtr document,IntPtr data)
        {
            IntPtr array=IntPtr.Zero;int count;
            Marshal.ThrowExceptionForHR(Slot<PeaksOut>(document,16)(document,data,out array,out count));
            var pointers=new List<IntPtr>();
            try
            {
                if(count<0||count>100)throw new InvalidOperationException("unexpected peak count");
                for(int i=0;i<count;i++)pointers.Add(Marshal.ReadIntPtr(array,i*8));
                return pointers.ToArray();
            }
            catch{foreach(var p in pointers)Marshal.Release(p);throw;}
            finally{if(array!=IntPtr.Zero)Marshal.FreeCoTaskMem(array);}
        }

        static int Count(IntPtr document,IntPtr data)
        {var peaks=Peaks(document,data);try{return peaks.Length;}finally{foreach(var p in peaks)Marshal.Release(p);}}

        Dictionary<string,object> Roundtrip(IntPtr nativeDocument,IntPtr documentInterface)
        {
            // NativeContextIdentity has already validated module, vtables,
            // UI thread and the exact fixed test path. Resolve only these two
            // known exports, and require their build-specific RVAs as well.
            IntPtr module=GetModuleHandleW("ProcessGrid.dll");
            IntPtr addAddress=GetProcAddress(module,"?AddFittedPeak@CProcessGridDoc@@QEAAHPEAUIFittedPeak@@PEAUIDataSpace@@@Z");
            IntPtr removeAddress=GetProcAddress(module,"?RemoveFittedPeak@CProcessGridDoc@@QEAAHPEAUIFittedPeak@@H@Z");
            if(addAddress!=IntPtr.Add(module,0x15a310)||removeAddress!=IntPtr.Add(module,0x167660))throw new InvalidOperationException("unsupported peak export ABI");
            var add=(AddPeak)Marshal.GetDelegateForFunctionPointer(addAddress,typeof(AddPeak));
            var remove=(RemovePeak)Marshal.GetDelegateForFunctionPointer(removeAddress,typeof(RemovePeak));
            IntPtr enumerator=IntPtr.Zero,data=IntPtr.Zero,parent=IntPtr.Zero,background=IntPtr.Zero,candidatePointer=IntPtr.Zero;
            IAvFittedPeak source=null,candidate=null;bool added=false,verified=false;int before=0,during=0,after=0;
            var dataPointers=new List<IntPtr>();
            try
            {
                Marshal.ThrowExceptionForHR(Slot<PointerOut>(documentInterface,18)(documentInterface,out enumerator));
                Marshal.ThrowExceptionForHR(Slot<NoArgs>(enumerator,4)(enumerator));
                for(int i=0;i<25;i++)
                {
                    IntPtr item;Marshal.ThrowExceptionForHR(Slot<PointerOut>(enumerator,3)(enumerator,out item));
                    if(item==IntPtr.Zero)break;dataPointers.Add(item);
                }
                if(dataPointers.Count!=24)throw new InvalidOperationException("requires fixed 24-spectrum test graph");
                data=dataPointers[23];var originals=Peaks(documentInterface,data);
                try
                {
                    before=originals.Length;
                    if(CleanupOnly)
                    {
                        if(before!=7)throw new InvalidOperationException("cleanup requires seven test components");
                        foreach(var pointer in originals)
                        {
                            var item=(IAvFittedPeak)Marshal.GetUniqueObjectForIUnknown(pointer);
                            string name;item.GetName(out name);
                            if(name=="MCP reversible ownership probe")
                            {if(candidate!=null){Marshal.ReleaseComObject(item);throw new InvalidOperationException("ambiguous test candidate");}candidate=item;}
                            else Marshal.ReleaseComObject(item);
                        }
                        if(candidate==null)throw new InvalidOperationException("test candidate not found");
                    }
                    else
                    {
                        if(before!=6)throw new InvalidOperationException("requires six existing fitted components at test index23");
                        source=(IAvFittedPeak)Marshal.GetUniqueObjectForIUnknown(originals[0]);
                    }
                }
                finally{foreach(var p in originals)Marshal.Release(p);}
                if(CleanupOnly)
                {
                    candidatePointer=Marshal.GetComInterfaceForObject(candidate,typeof(IAvFittedPeak));
                    added=true;during=before;verified=true;
                }
                else
                {
                source.GetPeakEntry(out parent);source.GetBackground(out background);
                if(parent==IntPtr.Zero||background==IntPtr.Zero)throw new InvalidOperationException("existing peak lacks app-owned region/background");
                double area,ratio,centre,height,heightRatio,width,gl,tailMix,tailHeight,tailExp;
                source.GetAsymParams(out area,out ratio,out centre,out height,out heightRatio,out width,out gl,out tailMix,out tailHeight,out tailExp);
                int combine;source.GetGLCombineMethod(out combine);
                candidate=(IAvFittedPeak)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("B90B1A14-A54F-11D2-852A-00104B053B4C")));
                candidate.SetDataSpace(data);candidate.SetBackground(background);candidate.SetPeakEntry(parent);
                candidate.SetAsymParams(area,ratio,centre,height,heightRatio,width,gl,tailMix,tailHeight,tailExp);
                candidate.SetGLCombineMethod(combine);candidate.SetName("MCP reversible ownership probe");
                candidatePointer=Marshal.GetComInterfaceForObject(candidate,typeof(IAvFittedPeak));
                if(add(nativeDocument,candidatePointer,data)!=1)throw new InvalidOperationException("native add refused candidate");
                added=true;
                during=Count(documentInterface,data);
                if(during!=before+1)throw new InvalidOperationException("candidate count verification failed");
                verified=true;
                }
            }
            finally
            {
                try
                {
                    if(added&&(!RetainCandidate||!verified))
                    {
                        if(remove(nativeDocument,candidatePointer,0)!=1)throw new InvalidOperationException("native candidate rollback failed; test document remains unsaved");
                        after=Count(documentInterface,data);
                        if(after!=(CleanupOnly?before-1:before))throw new InvalidOperationException("candidate rollback count mismatch");
                    }
                    else if(added)after=during;
                }
                finally
                {
                    if(candidatePointer!=IntPtr.Zero)Marshal.Release(candidatePointer);
                    if(candidate!=null)Marshal.ReleaseComObject(candidate);
                    if(source!=null)Marshal.ReleaseComObject(source);
                    if(background!=IntPtr.Zero)Marshal.Release(background);
                    if(parent!=IntPtr.Zero)Marshal.Release(parent);
                    foreach(var p in dataPointers)Marshal.Release(p);
                    if(enumerator!=IntPtr.Zero)Marshal.Release(enumerator);
                }
            }
            return new Dictionary<string,object>{{"spectrum_index",23},{"before",before},{"during",during},{"after",after},{"candidate_removed",!RetainCandidate},{"file_save_sent",false}};
        }
    }
}
