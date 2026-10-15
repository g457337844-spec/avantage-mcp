using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace AvantageMcp
{
    /// <summary>
    /// Owns Avantage's genuine in-process table objects, without using the live application.
    /// The private native ABI is enabled only for the exact locally verified ProcessGrid.dll.
    /// Returned interface pointers are borrowed and must not outlive this owner.
    /// </summary>
    public sealed class NativeGridTables : IDisposable
    {
        public const string SupportedSha256 = "1CC8A83B0677853BFB7A28165146CA1F0349D3E19B62D9810B337A90072D3ED9";
        private static readonly object Gate = new object();
        private static IntPtr module;
        private readonly int ownerThread;
        private bool oleInitialized;
        private IntPtr dialogObject, dialogInterface, peakInterface, fitInterface;
        private bool disposed;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
        [DllImport("ole32.dll")] private static extern void OleUninitialize();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Factory();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Allocate(UIntPtr bytes);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Construct(IntPtr memory, IntPtr parent);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Destroy(IntPtr instance, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetInterface(IntPtr self, out IntPtr value);

        public IntPtr DialogInterface { get { CheckAlive(); return dialogInterface; } }
        public IntPtr PeakTableInterface { get { CheckAlive(); return peakInterface; } }
        public IntPtr PeakFitTableInterface { get { CheckAlive(); return fitInterface; } }
        internal static IntPtr VerifiedModule { get { EnsureModule(); return module; } }

        private static T At<T>(int rva) where T : class
        {
            return Marshal.GetDelegateForFunctionPointer(IntPtr.Add(module, rva), typeof(T)) as T;
        }

        private static void EnsureModule()
        {
            lock (Gate)
            {
                if (module != IntPtr.Zero) return;
                if (IntPtr.Size != 8) throw new PlatformNotSupportedException("Avantage 6.9 native table hosting requires an x64 worker.");
                string install = Environment.GetEnvironmentVariable("AVANTAGE_HOME") ??
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Thermo Scientific", "Avantage");
                string path = Path.GetFullPath(Path.Combine(install, "bin", "ProcessGrid.dll"));
                if (!File.Exists(path)) throw new FileNotFoundException("Avantage native table library was not found.", path);
                string hash;
                using (SHA256 sha = SHA256.Create())
                using (FileStream file = File.OpenRead(path))
                    hash = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
                if (!String.Equals(hash, SupportedSha256, StringComparison.Ordinal))
                    throw new NotSupportedException("Native fitting host is disabled for this ProcessGrid.dll build (SHA256 " + hash + ").");
                module = LoadLibraryEx(path, IntPtr.Zero, 0x1100);
                if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not load verified ProcessGrid.dll.");
                ProjectPersistence.EnsureIndependentCache();
                // Kept loaded while native function pointers and table objects may exist.
            }
        }

        public NativeGridTables()
        {
            ownerThread = Thread.CurrentThread.ManagedThreadId;
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Native table objects must be owned by an STA worker.");
            EnsureModule();
            int hr = OleInitialize(IntPtr.Zero);
            Marshal.ThrowExceptionForHR(hr);
            oleInitialized = true;
            try
            {
                // Sequence recovered from this build's CDataDialogView initialization.
                dialogObject = At<Factory>(0x71860)();
                if (dialogObject == IntPtr.Zero) throw new OutOfMemoryException("Native data-dialog allocation failed.");
                CreateChild(0xd38, 0x10f180, 0x188);
                CreateChild(0x8e0, 0xf3d40, 0x198);
                Guid dialogId = new Guid("FBD099B0-4C6E-11D2-BCAE-00C04F8EBEF8");
                hr = Marshal.QueryInterface(IntPtr.Add(dialogObject, 0x160), ref dialogId, out dialogInterface);
                Marshal.ThrowExceptionForHR(hr);
                peakInterface = GetTable(dialogInterface, 6);
                fitInterface = GetTable(dialogInterface, 7);
            }
            catch { Dispose(); throw; }
        }

        private void CreateChild(int size, int constructorRva, int memberOffset)
        {
            IntPtr memory = At<Allocate>(0x1e7181)(new UIntPtr((uint)size));
            if (memory == IntPtr.Zero) throw new OutOfMemoryException("Native table allocation failed.");
            IntPtr child = At<Construct>(constructorRva)(memory, dialogObject);
            if (child == IntPtr.Zero) throw new InvalidOperationException("Native table constructor failed.");
            // Only the newly allocated, exclusively owned host is initialized here.
            Marshal.WriteIntPtr(dialogObject, memberOffset, child);
        }

        private static IntPtr GetTable(IntPtr dialog, int slot)
        {
            IntPtr address = Marshal.ReadIntPtr(Marshal.ReadIntPtr(dialog), slot * IntPtr.Size);
            GetInterface get = (GetInterface)Marshal.GetDelegateForFunctionPointer(address, typeof(GetInterface));
            IntPtr result;
            Marshal.ThrowExceptionForHR(get(dialog, out result));
            if (result == IntPtr.Zero) throw new InvalidOperationException("Avantage did not provide a native table interface.");
            return result;
        }

        private void CheckAlive()
        {
            if (disposed) throw new ObjectDisposedException("NativeGridTables");
            if (Thread.CurrentThread.ManagedThreadId != ownerThread)
                throw new InvalidOperationException("Native tables cannot cross worker threads.");
        }

        public void Dispose()
        {
            if (disposed) return;
            if (Thread.CurrentThread.ManagedThreadId != ownerThread)
                throw new InvalidOperationException("Native tables must be disposed by their owning worker thread.");
            // Release acquired COM references before the view's native destructor deletes its children.
            if (fitInterface != IntPtr.Zero) { Marshal.Release(fitInterface); fitInterface = IntPtr.Zero; }
            if (peakInterface != IntPtr.Zero) { Marshal.Release(peakInterface); peakInterface = IntPtr.Zero; }
            if (dialogInterface != IntPtr.Zero) { Marshal.Release(dialogInterface); dialogInterface = IntPtr.Zero; }
            if (dialogObject != IntPtr.Zero)
            {
                IntPtr address = Marshal.ReadIntPtr(Marshal.ReadIntPtr(dialogObject), IntPtr.Size);
                Destroy destroy = (Destroy)Marshal.GetDelegateForFunctionPointer(address, typeof(Destroy));
                destroy(dialogObject, 1); // Scalar deleting destructor frees both native child tables.
                dialogObject = IntPtr.Zero;
            }
            if (oleInitialized) { OleUninitialize(); oleInitialized = false; }
            disposed = true;
        }
    }
}
