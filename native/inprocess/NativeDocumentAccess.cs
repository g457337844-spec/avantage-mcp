using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AvantageMcp.InProcess
{
    // Call only after NativeContextIdentity validates the local document.
    internal static class NativeDocumentAccess
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int PointerOut(IntPtr self,out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int NoArgs(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int PeaksOut(IntPtr self,IntPtr data,out IntPtr array,out int count);
        static T Slot<T>(IntPtr value,int index)where T:class{return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(value),index*8),typeof(T))as T;}
        internal static List<IntPtr> DataSpaces(IntPtr document)
        {
            var result=new List<IntPtr>();IntPtr enumerator=IntPtr.Zero;
            try
            {
                Marshal.ThrowExceptionForHR(Slot<PointerOut>(document,18)(document,out enumerator));
                Marshal.ThrowExceptionForHR(Slot<NoArgs>(enumerator,4)(enumerator));
                for(int i=0;i<=4096;i++)
                {
                    IntPtr value;Marshal.ThrowExceptionForHR(Slot<PointerOut>(enumerator,3)(enumerator,out value));
                    if(value==IntPtr.Zero)return result;result.Add(value);
                }
                throw new InvalidOperationException("LIVE_DOCUMENT_SIZE");
            }
            catch{foreach(var p in result)Marshal.Release(p);throw;}
            finally{if(enumerator!=IntPtr.Zero)Marshal.Release(enumerator);}
        }
        internal static List<IAvFittedPeak> Peaks(IntPtr document,IntPtr data)
        {
            IntPtr array=IntPtr.Zero;int count;
            Marshal.ThrowExceptionForHR(Slot<PeaksOut>(document,16)(document,data,out array,out count));
            var result=new List<IAvFittedPeak>();
            var owned=new List<IntPtr>();
            try
            {
                if(count<0||count>10000||(count>0&&array==IntPtr.Zero))throw new InvalidOperationException("LIVE_MODEL_SIZE");
                for(int i=0;i<count;i++)owned.Add(Marshal.ReadIntPtr(array,i*8));
                foreach(var value in owned)result.Add((IAvFittedPeak)Marshal.GetUniqueObjectForIUnknown(value));
                return result;
            }
            catch{foreach(var p in result)Marshal.ReleaseComObject(p);throw;}
            finally{foreach(var value in owned)if(value!=IntPtr.Zero)Marshal.Release(value);if(array!=IntPtr.Zero)Marshal.FreeCoTaskMem(array);}
        }
    }
}
