using System;
using System.Runtime.InteropServices;

namespace AvantageMcp {
// Interface order and parameter types are taken from the installed Peaks.dll type library.
[ComImport,Guid("D62B63F0-4D36-11D2-BCAF-00C04F8EBEF8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAvFittedPeak {
 void SetSymParams(double area,double areaRatio,double centre,double height,double heightRatio,double fwhm,double gl);
 void GetSymParams(out double area,out double areaRatio,out double centre,out double height,out double heightRatio,out double fwhm,out double gl);
 void SetAsymParams(double area,double areaRatio,double centre,double height,double heightRatio,double fwhm,double gl,double tailMix,double tailHeight,double tailExp);
 void GetAsymParams(out double area,out double areaRatio,out double centre,out double height,out double heightRatio,out double fwhm,out double gl,out double tailMix,out double tailHeight,out double tailExp);
 void SetName([MarshalAs(UnmanagedType.BStr)]string name);void GetName([MarshalAs(UnmanagedType.BStr)]out string name);
 void SetDataSpace(IntPtr ds);void GetDataSpace(out IntPtr ds);
 void SetBackground(IntPtr background);void GetBackground(out IntPtr background);
 void SetPeakEntry(IntPtr peak);void GetPeakEntry(out IntPtr peak);
 void SetArea(double value);void GetArea(out double value);
 void SetAreaRatio(double value);void GetAreaRatio(out double value);
 void SetCentre(double value);void GetCentre(out double value);
 void SetHeight(double value);void GetHeight(out double value);
 void SetHeightRatio(double value);void GetHeightRatio(out double value);
 void SetFWHM(double value);void GetFWHM(out double value);
 void SetGLRatio(double value);void GetGLRatio(out double value);
 void SetTailMix(double value);void GetTailMix(out double value);
 void SetTailHeight(double value);void GetTailHeight(out double value);
 void SetTailExp(double value);void GetTailExp(out double value);
 void EnumPeakFitConstraints(out IntPtr enumerator);
 void GetPeakFitConstraint(int attribute,out IntPtr constraint);
 void SetPkFitTblEntryRefNo(int value);void GetPkFitTblEntryRefNo(out int value);
 void Update(IntPtr axes);void CloneFrom(IntPtr peak);
 void UpdateConstrainingFittedPeaksPointers(IntPtr peaks);void UpdateConstrainingFittedPeaksIndices(IntPtr peaks,int count);
 void CreateInitialPeakFitConstraint(int attribute,IntPtr constraint);
 void ChangePeakFitConstraint(IntPtr constraint,int attribute);
 void RemovePeakFitConstraint(IntPtr constraint,int attribute);
 void GetLevelCount(out int count);void GetCurrentLevel(out int level);void SetCurrentLevel(int level);
 void PropogateConstraints(IntPtr sourceAxes,IntPtr volumeAxes,int attribute);
 void PropogateValues(IntPtr sourceAxes,IntPtr volumeAxes,int attribute);
 void SetGLCombineMethod(int value);void GetGLCombineMethod(out int value);
}
[ComImport,Guid("28D315C0-B798-11D2-988E-00C04F8EBE0F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAvPeakConstraint {
 void SetParameters(int type,double value1,double value2,double value3,IntPtr referencePeak,int op);
 void GetParameters(out int type,out double value1,out double value2,out double value3,out IntPtr referencePeak,out int op);
 void IsConstrained(out int value);
 void SetXRefIndex(int value);void GetXRefIndex(out int value);
 void SetConstrainingFittedPeak(IntPtr peak);void GetConstrainingFittedPeak(out IntPtr peak);
 void SetCloning(int value);void CloneFrom(IntPtr constraint);void SameAs(IntPtr other,out int same);
}
}
