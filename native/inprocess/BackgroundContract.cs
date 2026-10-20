// Standalone copy of the native background ABI; exclude private background/session code.
using System;
using System.Runtime.InteropServices;
using VGDATASTORELib;
namespace AvantageMcp {
[ComImport,Guid("C9D7ED50-5854-11D2-B66E-006097BE683A"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAvantageBackground {
    void GetStart(out double value);
    void GetEnd(out double value);
    void GetMode(out int value);
    void SetStart(double value);
    void SetEnd(double value);
    void SetMode(int value);
    void GetPointsToAverage(out int value);
    void SetPointsToAverage(int value);
    void GeteVgranularity(out double value);
    void SeteVgranularity(double value);
    // The installed typelib mislabels destination arrays as IBackground.
    // Native calls and the DLL's implementation verify that these are double*.
    void Generate(IntPtr source,IntPtr destination,int size,int startIndex,int endIndex,sbyte veryWidePeaks);
    void GenerateEx(IntPtr source,IntPtr destination,int size,int startIndex,int endIndex,sbyte veryWidePeaks,double initial);
    void CloneBackground(out IAvantageBackground copy);
    void PropertiesToString([MarshalAs(UnmanagedType.BStr)] out string value);
    void GetShape([MarshalAs(UnmanagedType.Interface)] IDataSpace data,[MarshalAs(UnmanagedType.Interface)] IDataAxes axes,IntPtr destination,int size);
    void GetShapeParameters([MarshalAs(UnmanagedType.Interface)] IDataSpace data,out int size,out int energyAxis,out int first,out int last,out double start,out double end);
    void IsEquivalent(IAvantageBackground other,out sbyte equivalent);
    void GetGUIDString([MarshalAs(UnmanagedType.BStr)] out string value);
    void GetBackgroundType(out int value);
}

}
