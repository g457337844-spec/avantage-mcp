using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using VGDATASTORELib;

namespace AvantageMcp
{
    // Same binary fingerprint format as NativeSnapshot.Fingerprint, without
    // file persistence, private document/cache hosts or vendor-library loading.
    internal static class NativeRawFingerprint
    {
        sealed class Axis
        {
            internal string Label,Symbol,Unit;
            internal sbyte Linear;
            internal double Start,Step;
            internal int Size,Type;
            internal double[] Coordinates;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ConvertEnergy(IntPtr data,int count,
            [In,MarshalAs(UnmanagedType.LPArray,SizeParamIndex=1)]double[] input,
            [Out,MarshalAs(UnmanagedType.LPArray,SizeParamIndex=1)]double[] output);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern IntPtr GetModuleHandleW(string name);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern uint GetModuleFileNameW(IntPtr module,StringBuilder path,uint size);
        [DllImport("kernel32.dll",CharSet=CharSet.Ansi,ExactSpelling=true)]static extern IntPtr GetProcAddress(IntPtr module,string name);

        static double[] Binding(IDataSpace data,double[] input)
        {
            IntPtr module=GetModuleHandleW("ProcessingUtilities.dll");
            if(module==IntPtr.Zero)throw new InvalidOperationException("RAW_FINGERPRINT: energy module not loaded");
            var path=new StringBuilder(32768);uint length=GetModuleFileNameW(module,path,32768);
            string expected=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Thermo Scientific","Avantage","bin","ProcessingUtilities.dll");
            if(length==0||length>=32768||!string.Equals(path.ToString(),expected,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("RAW_FINGERPRINT: energy module path mismatch");
            IntPtr address=GetProcAddress(module,"?Kinetic2Binding@@YAJPEAUIDataSpace@@JQEAN1@Z");
            if(address==IntPtr.Zero)throw new InvalidOperationException("RAW_FINGERPRINT: energy conversion unavailable");
            var convert=(ConvertEnergy)Marshal.GetDelegateForFunctionPointer(address,typeof(ConvertEnergy));
            IntPtr pointer=Marshal.GetComInterfaceForObject(data,typeof(IDataSpace));
            try
            {
                var result=new double[input.Length];int hr=convert(pointer,input.Length,input,result);
                if(hr!=0||result.Any(x=>double.IsNaN(x)||double.IsInfinity(x)))throw new InvalidOperationException("RAW_FINGERPRINT: invalid converted energy");
                return result;
            }
            finally{Marshal.Release(pointer);}
        }

        public static string Fingerprint(IDataSpace data)
        {
            if(data==null)throw new ArgumentNullException("data");
            var dataAxes=new DataAxesClass();var spaceAxes=new SpaceAxesClass();
            try
            {
                data.GetDataAxes(dataAxes);data.GetSpaceAxes(spaceAxes);int count=dataAxes.GetSize();
                if(count<1||count>10000000)throw new InvalidOperationException("RAW_FINGERPRINT: point limit");
                var intensity=new double[count];data.GetData(dataAxes,out intensity[0],ref count);
                if(count!=intensity.Length||intensity.Any(x=>double.IsNaN(x)||double.IsInfinity(x)))throw new InvalidOperationException("RAW_FINGERPRINT: invalid intensity");
                int dimensions;spaceAxes.GetNAxes(out dimensions);
                if(dimensions<1||dimensions>16)throw new InvalidOperationException("RAW_FINGERPRINT: axis limit");
                var axes=new List<Axis>();long coordinates=0;
                for(int i=0;i<dimensions;i++)
                {
                    var axis=new Axis();DSAxisType type;
                    spaceAxes.GetAxisInfo(i,out axis.Label,out axis.Symbol,out axis.Unit,out axis.Linear,out axis.Start,out axis.Step,out axis.Size,out type);axis.Type=(int)type;
                    coordinates+=axis.Size;
                    if(axis.Size<1||coordinates>10000000)throw new InvalidOperationException("RAW_FINGERPRINT: coordinate limit");
                    axis.Coordinates=new double[axis.Size];for(int j=0;j<axis.Size;j++)spaceAxes.GetAxisPoint(i,j,out axis.Coordinates[j]);axes.Add(axis);
                }
                double sourceEnergy=0;try{sourceEnergy=Convert.ToDouble(data.GetProperty(DSPropSet.STDPROPSET,DSPropID.DS_SOPROPID_ENERGY));}catch(COMException){}
                double[] binding=dimensions==1&&axes[0].Unit=="eV"&&sourceEnergy>0?Binding(data,axes[0].Coordinates):null;
                using(var memory=new MemoryStream())
                {
                    using(var writer=new BinaryWriter(memory,Encoding.UTF8,true))
                    {
                        writer.Write(sourceEnergy);writer.Write(intensity.Length);foreach(double value in intensity)writer.Write(value);
                        writer.Write(axes.Count);
                        foreach(var axis in axes)
                        {
                            writer.Write(axis.Label??"");writer.Write(axis.Symbol??"");writer.Write(axis.Unit??"");writer.Write(axis.Linear);
                            writer.Write(axis.Start);writer.Write(axis.Step);writer.Write(axis.Size);writer.Write(axis.Type);
                            writer.Write(axis.Coordinates.Length);foreach(double value in axis.Coordinates)writer.Write(value);
                        }
                        writer.Write(binding!=null);if(binding!=null){writer.Write(binding.Length);foreach(double value in binding)writer.Write(value);}
                    }
                    using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(memory.ToArray())).Replace("-","");
                }
            }
            finally{Marshal.ReleaseComObject(dataAxes);Marshal.ReleaseComObject(spaceAxes);}
        }

        internal static void ValidateBackground(IDataSpace data,IDictionary<string,object> background)
        {
            var axes=new SpaceAxesClass();
            try
            {
                data.GetSpaceAxes(axes);int dimensions;axes.GetNAxes(out dimensions);
                if(dimensions!=1)throw new InvalidOperationException("LIVE_BACKGROUND_REQUIRES_ENERGY_SPECTRUM");
                string label,symbol,unit;sbyte linear;double start,increment;int count;DSAxisType type;
                axes.GetAxisInfo(0,out label,out symbol,out unit,out linear,out start,out increment,out count,out type);
                if(unit!="eV"||count<3||count>10000000)throw new InvalidOperationException("LIVE_BACKGROUND_REQUIRES_ENERGY_SPECTRUM");
                double lower=Convert.ToDouble(background["start_kinetic_ev"]),upper=Convert.ToDouble(background["end_kinetic_ev"]);
                int selected=0;double minimum=double.PositiveInfinity,maximum=double.NegativeInfinity,previous=0,step=0;
                for(int i=0;i<count;i++)
                {
                    double x;axes.GetAxisPoint(0,i,out x);if(double.IsNaN(x)||double.IsInfinity(x))throw new InvalidOperationException("LIVE_BACKGROUND_INVALID_AXIS");
                    if(i==1)step=Math.Abs(x-previous);
                    if(i>1&&Math.Abs(Math.Abs(x-previous)-step)>1e-5)throw new InvalidOperationException("LIVE_BACKGROUND_NONUNIFORM_AXIS");
                    previous=x;minimum=Math.Min(minimum,x);maximum=Math.Max(maximum,x);if(x>=lower-1e-8&&x<=upper+1e-8)selected++;
                }
                if(step<=0||Math.Abs(step-Convert.ToDouble(background["step_ev"]))>1e-5||lower<minimum-1e-6||upper>maximum+1e-6||selected<3||2*(Convert.ToInt32(background["points_to_average"])+1)>selected)throw new InvalidOperationException("LIVE_BACKGROUND_RANGE_MISMATCH");
            }
            finally{Marshal.ReleaseComObject(axes);}
        }
    }
}
