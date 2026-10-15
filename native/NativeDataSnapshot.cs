using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using VGDATASTORELib;

namespace AvantageMcp {
// Live DataSpaces can expose raw data without exposing file persistence storage.
// Clone through IDataSpace into an exclusively owned, initialized OLE storage.
public static class NativeSnapshot {
    [ComImport,Guid("0000010a-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPersistStorage {
        void GetClassID(out Guid id);[PreserveSig]int IsDirty();void InitNew(IntPtr storage);void Load(IntPtr storage);
        void Save(IntPtr storage,[MarshalAs(UnmanagedType.Bool)]bool sameAsLoad);void SaveCompleted(IntPtr storage);void HandsOffStorage();
    }
    [DllImport("ole32.dll",CharSet=CharSet.Unicode)]static extern int StgCreateDocfile(string path,uint mode,uint reserved,out IntPtr storage);
    sealed class Values {
        public double[] Intensities,Binding;public List<double[]> Coordinates=new List<double[]>();
        public List<Dictionary<string,object>> Axes=new List<Dictionary<string,object>>();public double SourceEnergy;
    }
    static Values Read(IDataSpace data) {
        var axes=new DataAxesClass();var space=new SpaceAxesClass();
        try {
            data.GetDataAxes(axes);data.GetSpaceAxes(space);int count=axes.GetSize();
            if(count<1||count>10000000)throw new Fault("DATA_SIZE","The native snapshot has an unsupported point count.");
            var result=new Values{Intensities=new double[count]};data.GetData(axes,out result.Intensities[0],ref count);
            if(count!=result.Intensities.Length||result.Intensities.Any(v=>Double.IsNaN(v)||Double.IsInfinity(v)))throw new Fault("SNAPSHOT_INVALID_DATA","Native snapshot intensities are invalid.");
            int dimensions;space.GetNAxes(out dimensions);
            for(int axis=0;axis<dimensions;axis++) {
                string label,symbol,unit;sbyte linear;double start,step;int size;DSAxisType type;
                space.GetAxisInfo(axis,out label,out symbol,out unit,out linear,out start,out step,out size,out type);
                if(size<1||size>10000000)throw new Fault("DATA_SIZE","The native snapshot axis has an unsupported size.");
                result.Axes.Add(J.Obj("label",label,"symbol",symbol,"unit",unit,"linear",linear,"start",start,"increment",step,"size",size,"type",(int)type));
                var coordinates=new double[size];for(int point=0;point<size;point++)space.GetAxisPoint(axis,point,out coordinates[point]);result.Coordinates.Add(coordinates);
            }
            try{result.SourceEnergy=Convert.ToDouble(data.GetProperty(DSPropSet.STDPROPSET,DSPropID.DS_SOPROPID_ENERGY));}catch(COMException){}
            if(dimensions==1&&Convert.ToString(result.Axes[0]["unit"])=="eV"&&result.SourceEnergy>0)result.Binding=EnergyConversions.KineticToBinding(data,result.Coordinates[0]);
            return result;
        }finally{J.Release(axes);J.Release(space);}
    }
    static bool Equal(Values expected,Values actual) {
        return expected.Intensities.SequenceEqual(actual.Intensities)&&expected.SourceEnergy==actual.SourceEnergy&&
            NativePeakTablePersistence.Equivalent(expected.Axes,actual.Axes)&&expected.Coordinates.Count==actual.Coordinates.Count&&
            expected.Coordinates.Select((coordinates,index)=>coordinates.SequenceEqual(actual.Coordinates[index])).All(equal=>equal)&&
            (expected.Binding==null?actual.Binding==null:actual.Binding!=null&&expected.Binding.SequenceEqual(actual.Binding));
    }
    public static string Fingerprint(IDataSpace source) {
        var value=Read(source);
        using(var stream=new MemoryStream()) {
            using(var writer=new BinaryWriter(stream,Encoding.UTF8,true)) {
                writer.Write(value.SourceEnergy);writer.Write(value.Intensities.Length);
                foreach(double point in value.Intensities)writer.Write(point);
                writer.Write(value.Axes.Count);
                for(int i=0;i<value.Axes.Count;i++) {
                    var axis=value.Axes[i];
                    foreach(string key in new[]{"label","symbol","unit"})writer.Write(Convert.ToString(axis[key])??"");
                    writer.Write(Convert.ToSByte(axis["linear"]));writer.Write(Convert.ToDouble(axis["start"]));
                    writer.Write(Convert.ToDouble(axis["increment"]));writer.Write(Convert.ToInt32(axis["size"]));
                    writer.Write(Convert.ToInt32(axis["type"]));writer.Write(value.Coordinates[i].Length);
                    foreach(double point in value.Coordinates[i])writer.Write(point);
                }
                writer.Write(value.Binding!=null);
                if(value.Binding!=null){writer.Write(value.Binding.Length);foreach(double point in value.Binding)writer.Write(point);}
            }
            using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-","");
        }
    }
    public static void CopyDataSpaceToFile(IDataSpace source,string copy) {
        if(source==null)throw new ArgumentNullException("source");
        if(!Path.IsPathRooted(copy)||!String.Equals(Path.GetExtension(copy),".vgd",StringComparison.OrdinalIgnoreCase))throw new Fault("INVALID_PATH","A native spectrum snapshot requires a new absolute .VGD path.");
        copy=Path.GetFullPath(copy);if(File.Exists(copy))throw new Fault("FILE_EXISTS","The snapshot destination already exists.");
        string parent=Path.GetDirectoryName(copy);if(!Directory.Exists(parent))throw new Fault("DIRECTORY_NOT_FOUND",parent);
        var expected=Read(source);DataSpaceClass local=null,reloaded=null;IntPtr storage=IntPtr.Zero;
        string seed=Path.Combine(parent,".snapshot-"+Guid.NewGuid().ToString("N")+".vgd");
        try {
            local=new DataSpaceClass();
            // STGM_CREATE|READWRITE|SHARE_EXCLUSIVE|DELETEONRELEASE. The seed is
            // exclusively owned by this operation and disappears on final release.
            Marshal.ThrowExceptionForHR(StgCreateDocfile(seed,0x04001012,0,out storage));((IPersistStorage)local).InitNew(storage);
            local.CloneDataSpace((DataSpace)source);
            if(!Equal(expected,Read(local)))throw new Fault("SNAPSHOT_MISMATCH","Native cloning did not preserve every intensity, axis and calibrated binding-energy value.");
            var persist=(System.Runtime.InteropServices.ComTypes.IPersistFile)local;persist.Save(copy,false);persist.SaveCompleted(null);
            reloaded=new DataSpaceClass();reloaded.LoadPersistFile(copy,0,1,0);
            if(!Equal(expected,Read(reloaded)))throw new Fault("SNAPSHOT_MISMATCH","The persisted native spectrum did not preserve every intensity, axis and calibrated binding-energy value.");
        }catch{if(reloaded!=null){J.Release(reloaded);reloaded=null;}if(File.Exists(copy))File.Delete(copy);throw;}
        finally{J.Release(reloaded);J.Release(local);if(storage!=IntPtr.Zero)Marshal.Release(storage);}
    }
}
}
