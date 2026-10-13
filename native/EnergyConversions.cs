using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using VGDATASTORELib;

namespace AvantageMcp
{
    /// <summary>Uses Avantage's own calibrated energy conversions, including stored source parameters.</summary>
    public static class EnergyConversions
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ConvertEnergyArray(IntPtr dataSpace, int count,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] double[] input,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] double[] output);

        private static readonly object Gate = new object();
        private static IntPtr module;
        private static ConvertEnergyArray toBinding, toKinetic;

        private static void EnsureLoaded()
        {
            lock (Gate)
            {
                if (toBinding != null && toKinetic != null) return;
                string install = Environment.GetEnvironmentVariable("AVANTAGE_HOME") ??
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Thermo Scientific", "Avantage");
                string dll = Path.GetFullPath(Path.Combine(install, "bin", "ProcessingUtilities.dll"));
                if (!File.Exists(dll)) throw new FileNotFoundException("Avantage energy-conversion library was not found.", dll);
                // Pin the vendor library for the process lifetime while its delegates are retained.
                if (module == IntPtr.Zero) module = LoadLibraryEx(dll, IntPtr.Zero, 0x1100);
                if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not load " + dll);
                toBinding = Bind("?Kinetic2Binding@@YAJPEAUIDataSpace@@JQEAN1@Z");
                toKinetic = Bind("?Binding2Kinetic@@YAJPEAUIDataSpace@@JQEAN1@Z");
            }
        }

        private static ConvertEnergyArray Bind(string name)
        {
            IntPtr address = GetProcAddress(module, name);
            if (address == IntPtr.Zero) throw new EntryPointNotFoundException("Required Avantage conversion entry point is unavailable: " + name);
            return (ConvertEnergyArray)Marshal.GetDelegateForFunctionPointer(address, typeof(ConvertEnergyArray));
        }

        private static double[] Convert(IDataSpace data, double[] input, bool binding)
        {
            if (data == null) throw new ArgumentNullException("data");
            if (input == null) throw new ArgumentNullException("input");
            foreach (double value in input)
                if (Double.IsNaN(value) || Double.IsInfinity(value)) throw new ArgumentException("Energy values must be finite.", "input");
            if (input.Length == 0) return new double[0];
            EnsureLoaded();
            double[] output = new double[input.Length];
            IntPtr dataInterface = Marshal.GetComInterfaceForObject(data, typeof(IDataSpace));
            try
            {
                int hr = (binding ? toBinding : toKinetic)(dataInterface, input.Length, input, output);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                if (hr != 0) throw new InvalidOperationException("Avantage did not complete energy conversion; status 0x" + hr.ToString("X8") + ".");
                foreach (double value in output)
                    if (Double.IsNaN(value) || Double.IsInfinity(value)) throw new InvalidOperationException("Avantage returned a non-finite converted energy.");
                return output;
            }
            finally { Marshal.Release(dataInterface); }
        }

        public static double[] KineticToBinding(IDataSpace data, double[] values) { return Convert(data, values, true); }
        public static double[] BindingToKinetic(IDataSpace data, double[] values) { return Convert(data, values, false); }
        public static double KineticToBinding(IDataSpace data, double value) { return Convert(data, new[] { value }, true)[0]; }
        public static double BindingToKinetic(IDataSpace data, double value) { return Convert(data, new[] { value }, false)[0]; }
    }
}
