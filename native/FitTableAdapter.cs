using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AvantageMcp
{
    // The native solver only consumes enumeration and fit mode from this table.
    // Peak values, constraints, backgrounds and solving remain Avantage objects.
    [ComVisible(true), Guid("D62B63EE-4D36-11D2-BCAF-00C04F8EBEF8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAvFitTableAdapter
    {
        [PreserveSig] int EnumFittedPeaks(out IntPtr value); // Native slot 3.
        [PreserveSig] int Slot04(); [PreserveSig] int Slot05();
        [PreserveSig] int Slot06(); [PreserveSig] int Slot07();
        [PreserveSig] int Slot08(); [PreserveSig] int Slot09();
        [PreserveSig] int Slot10();
        [PreserveSig] int GetMode(out int value); // Native slot 11.
        [PreserveSig] int Slot12(); [PreserveSig] int Slot13();
        [PreserveSig] int Slot14(); [PreserveSig] int Slot15();
        [PreserveSig] int Slot16(); [PreserveSig] int Slot17();
        [PreserveSig] int Slot18(); [PreserveSig] int Slot19();
        [PreserveSig] int Slot20(); [PreserveSig] int Slot21();
        [PreserveSig] int Slot22(); [PreserveSig] int Slot23();
        [PreserveSig] int Slot24(); [PreserveSig] int Slot25();
    }

    [ComVisible(true), Guid("D62B63F2-4D36-11D2-BCAF-00C04F8EBEF8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAvFittedPeakEnumerator
    {
        [PreserveSig] int Next(uint count, IntPtr values, IntPtr fetched);
        [PreserveSig] int Skip(uint count);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IntPtr value);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class FitTableAdapter : IAvFitTableAdapter
    {
        private readonly IAvFittedPeak[] peaks;
        private readonly int mode;
        private readonly List<int> unsupported = new List<int>();
        public FitTableAdapter(IList<IAvFittedPeak> values, int fitMode)
        {
            if (IntPtr.Size != 8) throw new PlatformNotSupportedException("Native fitting adapter requires x64.");
            if (values == null) throw new ArgumentNullException("values");
            if (fitMode < 0 || fitMode > 1) throw new ArgumentOutOfRangeException("fitMode");
            peaks = new IAvFittedPeak[values.Count];
            for (int i = 0; i < values.Count; i++) { if (values[i] == null) throw new ArgumentException("Null peak.", "values"); peaks[i] = values[i]; }
            mode = fitMode;
        }
        public int[] UnsupportedSlots { get { lock (unsupported) return unsupported.ToArray(); } }
        public IntPtr AcquireInterface() { return Marshal.GetComInterfaceForObject(this, typeof(IAvFitTableAdapter)); }
        public int EnumFittedPeaks(out IntPtr value)
        {
            value = IntPtr.Zero;
            try { value = Marshal.GetComInterfaceForObject(new FittedPeakEnumerator(peaks), typeof(IAvFittedPeakEnumerator)); return 0; }
            catch (Exception e) { return Marshal.GetHRForException(e); }
        }
        public int GetMode(out int value) { value = mode; return 0; }
        private int Unsupported(int slot) { lock (unsupported) unsupported.Add(slot); return unchecked((int)0x80004001); }
        public int Slot04() { return Unsupported(4); } public int Slot05() { return Unsupported(5); }
        public int Slot06() { return Unsupported(6); } public int Slot07() { return Unsupported(7); }
        public int Slot08() { return Unsupported(8); } public int Slot09() { return Unsupported(9); }
        public int Slot10() { return Unsupported(10); } public int Slot12() { return Unsupported(12); }
        public int Slot13() { return Unsupported(13); } public int Slot14() { return Unsupported(14); }
        public int Slot15() { return Unsupported(15); } public int Slot16() { return Unsupported(16); }
        public int Slot17() { return Unsupported(17); } public int Slot18() { return Unsupported(18); }
        public int Slot19() { return Unsupported(19); } public int Slot20() { return Unsupported(20); }
        public int Slot21() { return Unsupported(21); } public int Slot22() { return Unsupported(22); }
        public int Slot23() { return Unsupported(23); } public int Slot24() { return Unsupported(24); }
        public int Slot25() { return Unsupported(25); }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class FittedPeakEnumerator : IAvFittedPeakEnumerator
    {
        private readonly IAvFittedPeak[] peaks;
        private int index;
        internal FittedPeakEnumerator(IAvFittedPeak[] values) { peaks = values; }
        public int Next(uint count, IntPtr values, IntPtr fetched)
        {
            if (fetched != IntPtr.Zero) Marshal.WriteInt32(fetched, 0);
            if (values == IntPtr.Zero || (fetched == IntPtr.Zero && count != 1)) return unchecked((int)0x80004003);
            if (count > Int32.MaxValue) return unchecked((int)0x80070057);
            int written = 0;
            try
            {
                while (written < count && index < peaks.Length)
                {
                    IntPtr value = Marshal.GetComInterfaceForObject(peaks[index], typeof(IAvFittedPeak));
                    Marshal.WriteIntPtr(values, written * IntPtr.Size, value);
                    written++; index++;
                }
                if (fetched != IntPtr.Zero) Marshal.WriteInt32(fetched, written);
                return written == count ? 0 : 1;
            }
            catch (Exception e)
            {
                for (int i = 0; i < written; i++) { Marshal.Release(Marshal.ReadIntPtr(values, i * IntPtr.Size)); Marshal.WriteIntPtr(values, i * IntPtr.Size, IntPtr.Zero); }
                return Marshal.GetHRForException(e);
            }
        }
        public int Skip(uint count)
        {
            int skipped = (int)Math.Min((long)count, peaks.Length - index);
            index += skipped; return skipped == count ? 0 : 1;
        }
        public int Reset() { index = 0; return 0; }
        public int Clone(out IntPtr value)
        {
            value = IntPtr.Zero;
            try { var copy = new FittedPeakEnumerator(peaks); copy.index = index; value = Marshal.GetComInterfaceForObject(copy, typeof(IAvFittedPeakEnumerator)); return 0; }
            catch (Exception e) { return Marshal.GetHRForException(e); }
        }
    }
}
