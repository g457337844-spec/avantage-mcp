using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AvantageMcp {
// Resolve existing aliases by Windows file identity, including hard links and junctions.
public static class FileSafety {
    [StructLayout(LayoutKind.Sequential)] struct FileInformation {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32", SetLastError=true)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle,out FileInformation information);
    public static bool SameFile(string first,string second) {
        if(String.Equals(Path.GetFullPath(first),Path.GetFullPath(second),StringComparison.OrdinalIgnoreCase)) return true;
        if(!File.Exists(first)||!File.Exists(second)) return false;
        using(var a=new FileStream(first,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
        using(var b=new FileStream(second,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) {
            FileInformation x,y;
            if(!GetFileInformationByHandle(a.SafeFileHandle,out x)||!GetFileInformationByHandle(b.SafeFileHandle,out y))
                throw new IOException("Cannot verify output file identity.");
            return x.VolumeSerial==y.VolumeSerial && x.IndexHigh==y.IndexHigh && x.IndexLow==y.IndexLow;
        }
    }
}
}
