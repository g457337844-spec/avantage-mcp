// Read-only in-process context probe for the Avantage processing-plugin path.
//
// This library is NOT part of the native/*.cs bridge build and must never be
// compiled into AvantageBridge.exe. It is a standalone COM server whose only
// job is to report the execution context it was called with.
//
// Hard boundaries (see docs/inprocess-context-probe.md):
//   * no project/model/raw-data access, no private object addresses;
//   * no file, registry, network, process-start or window/dialog activity;
//   * no dependency on Engine, ProjectPersistence, NativeGridTables or any
//     other private host: those pin a separate data cache and must never run
//     inside the application;
//   * no fitting, no writer, no output data.
//
// The only persistence is the caller-supplied IStorage passed into the
// callback. Nothing is written unless that storage contains the exact
// `AvantageMcp.ProbeRequest` marker stream.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

[assembly: ComVisible(false)]
[assembly: Guid("7F2C4A61-3E58-4D0B-9C21-5A6E8D4F1B37")]

namespace AvantageMcp.InProcess
{
    // Only the first two IStorage methods are needed. Declaration order is
    // native vtable order: IUnknown slots 0..2, CreateStream 3, OpenStream 4.
    [ComImport]
    [ComVisible(false)]
    [Guid("0000000B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IStorage
    {
        void CreateStream([MarshalAs(UnmanagedType.LPWStr)] string name,
            uint mode, uint reserved1, uint reserved2, out IStream stream);
        void OpenStream([MarshalAs(UnmanagedType.LPWStr)] string name,
            IntPtr reserved1, uint mode, uint reserved2, out IStream stream);
    }

    /// <summary>IDataProcessControl, IID 597365F0-21FB-11D2-B9D9-006097D69B02.</summary>
    /// <remarks>
    /// Six COM slots in total: three IUnknown slots plus the three methods
    /// below, in declaration order. Later C++-only virtual methods are not COM
    /// methods and are deliberately absent.
    /// </remarks>
    [ComVisible(true)]
    [Guid("597365F0-21FB-11D2-B9D9-006097D69B02")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDataProcessControl
    {
        /// <summary>Slot 3. Caller arrays are never dereferenced.</summary>
        [PreserveSig]
        int ControlProcess(
            IntPtr processing,
            IntPtr storage,
            int inputCount,
            IntPtr inputs,
            IntPtr axes,
            out int outputCount,
            out IntPtr outputs,
            int flags);

        /// <summary>Slot 4.</summary>
        [PreserveSig]
        int Reprocess(IntPtr processing, IntPtr data, IntPtr stream);

        /// <summary>Slot 5.</summary>
        [PreserveSig]
        int SetProcessingSchema(int schema);
    }

    /// <summary>IDataProcess, IID 3FB981F0-56AB-11D1-8AD5-006097D69B02.</summary>
    /// <remarks>
    /// Nine COM slots in total: three IUnknown slots plus the six methods below.
    /// The signature shapes match the vendor interface vtables recorded in the
    /// contract; <c>ExecuteProcess</c> receives a caller-owned
    /// <c>IDataSpace**</c> array, not a triple pointer.
    /// </remarks>
    [ComVisible(true)]
    [Guid("3FB981F0-56AB-11D1-8AD5-006097D69B02")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDataProcess
    {
        /// <summary>Slot 3.</summary>
        [PreserveSig]
        int QuerySummaryAttributes(IntPtr storage);

        /// <summary>Slot 4. Reports zero outputs; no array is written.</summary>
        [PreserveSig]
        int QueryProcessAttributes(IntPtr storage, int inputCount, IntPtr inputs, IntPtr axes, out int outputCount);

        /// <summary>Slot 5. Not implemented: this probe never produces data.</summary>
        [PreserveSig]
        int ExecuteProcess(
            IntPtr storage,
            int inputCount,
            IntPtr inputs,
            IntPtr axes,
            int outputCount,
            IntPtr outputs);

        /// <summary>Slot 6. Returns a bounded diagnostic label as an allocated BSTR.</summary>
        [PreserveSig]
        int PropertiesToString([MarshalAs(UnmanagedType.BStr)] out string value);

        /// <summary>Slot 7. Non-mutating.</summary>
        [PreserveSig]
        int SetProcessAttributes(IntPtr storage);

        /// <summary>Slot 8. Non-mutating.</summary>
        [PreserveSig]
        int ProcessControlComplete(int status);
    }

    /// <summary>
    /// Minimal read-only implementation. The constructor performs no I/O and
    /// starts nothing; every callback is wrapped so no exception crosses the
    /// COM boundary.
    /// </summary>
    [ComVisible(true)]
    [Guid("96D0A0F0-56FC-4305-92B0-0BA9CB776AAE")]
    [ClassInterface(ClassInterfaceType.None)]
    public class ContextProbe : IDataProcessControl, IDataProcess
    {
        // --- HRESULTs ------------------------------------------------------
        const int S_OK = 0;
        const int E_NOTIMPL = unchecked((int)0x80004001);
        const int E_FAIL = unchecked((int)0x80004005);
        const int E_ABORT = unchecked((int)0x80004004);
        const int E_INVALIDARG = unchecked((int)0x80070057);

        // --- fixed limits and names ---------------------------------------
        const string RequestStreamName = "AvantageMcp.ProbeRequest";
        const string ContextStreamName = "AvantageMcp.Context";
        const string ExpectedMarker = "inspect-context-v1";
        const int MaxMarkerBytes = 64;
        const int MaxJsonBytes = 4096;
        const int MaxInputs = 32;

        /// <summary>IProcessingGrid context interface, used for a QI probe only.</summary>
        static readonly Guid ProcessingGridIid = new Guid("D0C45AA1-3DA2-11D2-93E9-006097C82421");

        const uint STGM_READ = 0x00000000u;
        const uint STGM_WRITE = 0x00000001u;
        const uint STGM_SHARE_EXCLUSIVE = 0x00000010u;
        const uint STGM_CREATE = 0x00001000u;
        const int STATFLAG_NONAME = 1;

        int schema;

        public ContextProbe()
        {
            // Intentionally empty: no I/O, no commands, no application calls.
        }

        // ------------------------------------------------------------------
        // IDataProcessControl
        // ------------------------------------------------------------------
        public int ControlProcess(
            IntPtr processing,
            IntPtr storage,
            int inputCount,
            IntPtr inputs,
            IntPtr axes,
            out int outputCount,
            out IntPtr outputs,
            int flags)
        {
            // Rule 1: no output data on any path, including failures.
            outputCount = 0;
            outputs = IntPtr.Zero;
            bool markerValidated = false;
            try
            {
                // Rule 2: only the host application, only real arguments.
                if (!IsHostProcess()) return E_INVALIDARG;
                if (processing == IntPtr.Zero || storage == IntPtr.Zero) return E_INVALIDARG;
                if (inputCount < 0 || inputCount > MaxInputs) return E_INVALIDARG;

                // Rule 3: the marker is checked before anything is written, so a
                // storage object that is not ours never receives diagnostics.
                if (!ValidateRequest(storage)) return E_INVALIDARG;
                markerValidated = true;

                // Rule 4: probe the borrowed context, release only our own QI.
                bool gridAvailable = QueryGridAvailable(processing);

                // Rule 5: context facts only; no private memory is read.
                Dictionary<string, object> document = CollectContext(processing, gridAvailable, inputCount, inputs, axes, flags);

                // Rule 6: bounded JSON into the caller's own storage.
                // This diagnostic intentionally cancels processing after writing
                // its report. The verified host handler calls its normal
                // DataProcessingComplete cleanup for a negative HRESULT; merely
                // returning S_OK would leave the active plugin retained.
                return WriteContext(storage, document) ? E_ABORT : E_FAIL;
            }
            catch (Exception error)
            {
                // A diagnostic is written only after the marker proved this
                // storage object is the one that asked for the probe.
                if (markerValidated) TryWriteError(storage, Describe(error));
                return E_FAIL;
            }
        }

        public int Reprocess(IntPtr processing, IntPtr data, IntPtr stream)
        {
            return E_NOTIMPL;
        }

        public int SetProcessingSchema(int value)
        {
            try
            {
                Interlocked.Exchange(ref schema, value);
                return S_OK;
            }
            catch (Exception)
            {
                return E_FAIL;
            }
        }

        // ------------------------------------------------------------------
        // IDataProcess
        // ------------------------------------------------------------------
        public int QuerySummaryAttributes(IntPtr storage)
        {
            return S_OK;
        }

        public int QueryProcessAttributes(
            IntPtr storage,
            int inputCount,
            IntPtr inputs,
            IntPtr axes,
            out int outputCount)
        {
            outputCount = 0;
            return S_OK;
        }

        public int ExecuteProcess(
            IntPtr storage,
            int inputCount,
            IntPtr inputs,
            IntPtr axes,
            int outputCount,
            IntPtr outputs)
        {
            // A probe must never write document or output data.
            return E_NOTIMPL;
        }

        public int PropertiesToString([MarshalAs(UnmanagedType.BStr)] out string value)
        {
            try
            {
                value = "Avantage MCP read-only context probe (inspect-context-v1)";
                return S_OK;
            }
            catch (Exception)
            {
                value = null;
                return E_FAIL;
            }
        }

        public int SetProcessAttributes(IntPtr storage)
        {
            return S_OK;
        }

        public int ProcessControlComplete(int status)
        {
            return S_OK;
        }

        // ------------------------------------------------------------------
        // Storage helpers (standard COM only)
        // ------------------------------------------------------------------
        static bool HasRequestMarker(IntPtr storage)
        {
            return HasMarker(storage, RequestStreamName, ExpectedMarker);
        }

        protected virtual bool ValidateRequest(IntPtr storage)
        {
            return HasRequestMarker(storage);
        }

        protected static bool HasMarker(IntPtr storage, string streamName, string expectedMarker)
        {
            IStream stream = null;
            try
            {
                var store = (IStorage)Marshal.GetUniqueObjectForIUnknown(storage);
                try
                {
                    store.OpenStream(streamName, IntPtr.Zero, STGM_READ | STGM_SHARE_EXCLUSIVE, 0, out stream);
                }
                finally
                {
                    ReleaseCom(store);
                }
                if (stream == null) return false;

                // Bound the size before allocating, then require a complete read.
                System.Runtime.InteropServices.ComTypes.STATSTG stat;
                stream.Stat(out stat, STATFLAG_NONAME);
                if (stat.cbSize < 0 || stat.cbSize > MaxMarkerBytes) return false;
                int length = (int)stat.cbSize;
                if (length == 0) return false;

                byte[] buffer = new byte[length];
                int read = ReadExactly(stream, buffer);
                if (read != length) return false;

                string text = new UTF8Encoding(false, true).GetString(buffer, 0, read).Trim();
                return string.Equals(text, expectedMarker, StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                ReleaseCom(stream);
            }
        }

        static int ReadExactly(IStream stream, byte[] buffer)
        {
            IntPtr count = Marshal.AllocCoTaskMem(4);
            try
            {
                int total = 0;
                while (total < buffer.Length)
                {
                    byte[] part = new byte[buffer.Length - total];
                    Marshal.WriteInt32(count, 0);
                    stream.Read(part, part.Length, count);
                    int read = Marshal.ReadInt32(count);
                    if (read <= 0 || read > part.Length) break;
                    Buffer.BlockCopy(part, 0, buffer, total, read);
                    total += read;
                }
                return total;
            }
            finally
            {
                Marshal.FreeCoTaskMem(count);
            }
        }

        static bool QueryGridAvailable(IntPtr processing)
        {
            IntPtr grid = IntPtr.Zero;
            try
            {
                Guid iid = ProcessingGridIid;
                // The context is borrowed; only the QI result belongs to us.
                int hr = Marshal.QueryInterface(processing, ref iid, out grid);
                return hr == S_OK && grid != IntPtr.Zero;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (grid != IntPtr.Zero) Marshal.Release(grid);
            }
        }

        protected virtual Dictionary<string, object> CollectContext(
            IntPtr processing,
            bool gridAvailable,
            int inputCount,
            IntPtr inputs,
            IntPtr axes,
            int flags)
        {
            var document = new Dictionary<string, object>(StringComparer.Ordinal);
            document["probe"] = ExpectedMarker;
            document["probe_completed"] = true;
            document["completion_mode"] = "cancel-processing-after-report";
            document["grid_interface_available"] = gridAvailable;
            document["schema"] = Interlocked.CompareExchange(ref schema, 0, 0);
            document["input_count"] = inputCount;
            document["flags"] = flags;
            // Pointer null-ness only: the arrays themselves are never read.
            document["inputs_present"] = inputs != IntPtr.Zero;
            document["axes_present"] = axes != IntPtr.Zero;

            using (Process current = Process.GetCurrentProcess())
            {
                document["process_id"] = current.Id;
                document["process_name"] = current.ProcessName;
                IntPtr window = current.MainWindowHandle;
                document["main_window_present"] = window != IntPtr.Zero;

                uint windowPid = 0;
                int windowThread = window == IntPtr.Zero
                    ? 0
                    : NativeMethods.GetWindowThreadProcessId(window, out windowPid);
                document["main_window_thread_id"] = windowThread;
                document["main_window_process_id"] = window == IntPtr.Zero ? 0 : (int)windowPid;

                int currentThread = NativeMethods.GetCurrentThreadId();
                document["thread_id"] = currentThread;
                document["thread_matches_main_window"] = windowThread != 0 && windowThread == currentThread;
            }
            return document;
        }

        static bool WriteContext(IntPtr storage, Dictionary<string, object> document)
        {
            byte[] payload = Serialize(document);
            if (payload == null || payload.Length == 0 || payload.Length > MaxJsonBytes) return false;
            return WriteStream(storage, ContextStreamName, payload);
        }

        static void TryWriteError(IntPtr storage, string message)
        {
            try
            {
                var document = new Dictionary<string, object>(StringComparer.Ordinal);
                document["probe"] = ExpectedMarker;
                document["probe_error"] = message;
                byte[] payload = Serialize(document);
                if (payload != null && payload.Length <= MaxJsonBytes) WriteStream(storage, ContextStreamName, payload);
            }
            catch (Exception)
            {
                // A failed diagnostic must not change the returned HRESULT.
            }
        }

        static byte[] Serialize(Dictionary<string, object> document)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = MaxJsonBytes, RecursionLimit = 8 };
            string text = serializer.Serialize(document);
            if (string.IsNullOrEmpty(text)) return null;
            return new UTF8Encoding(false).GetBytes(text);
        }

        static bool WriteStream(IntPtr storage, string name, byte[] payload)
        {
            IStream stream = null;
            try
            {
                var store = (IStorage)Marshal.GetUniqueObjectForIUnknown(storage);
                try
                {
                    store.CreateStream(name, STGM_CREATE | STGM_WRITE | STGM_SHARE_EXCLUSIVE, 0, 0, out stream);
                }
                finally
                {
                    ReleaseCom(store);
                }
                if (stream == null) return false;

                IntPtr written = Marshal.AllocCoTaskMem(4);
                try
                {
                    Marshal.WriteInt32(written, 0);
                    stream.Write(payload, payload.Length, written);
                    if (Marshal.ReadInt32(written) != payload.Length) return false;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(written);
                }
                stream.Commit(0);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                ReleaseCom(stream);
            }
        }

        static void ReleaseCom(object value)
        {
            if (value == null) return;
            try
            {
                if (Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
            }
            catch (Exception)
            {
                // Releasing is best-effort; never fail a callback over it.
            }
        }

        static bool IsHostProcess()
        {
            try
            {
                using (Process current = Process.GetCurrentProcess())
                {
                    return string.Equals(current.ProcessName, "Avantage", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Bounded, non-sensitive error text for the probe_error field.</summary>
        static string Describe(Exception error)
        {
            if (error == null) return "unknown";
            string name = error.GetType().Name;
            string text = error.Message ?? string.Empty;
            if (text.Length > 200) text = text.Substring(0, 200);
            // Keep it a single short line; no stack traces or pointers.
            text = text.Replace("\r", " ").Replace("\n", " ");
            return name + ": " + text;
        }

        static class NativeMethods
        {
            [DllImport("kernel32.dll")]
            internal static extern int GetCurrentThreadId();

            [DllImport("user32.dll")]
            internal static extern int GetWindowThreadProcessId(IntPtr window, out uint processId);
        }
    }
}
