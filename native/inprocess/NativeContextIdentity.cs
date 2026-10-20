// Read-only native context identity diagnostic for the Avantage processing-plugin path.
//
// This file is compiled only into the standalone in-process probe assembly
// (native/inprocess). It is NOT part of native/*.cs and must never be linked
// into AvantageBridge.exe.
//
// Purpose: prove, from inside the application process, that a borrowed
// IProcessing context really is the vendor grid object and that its document is
// the single fixed test project. Every guard fails closed with a short managed
// exception. Nothing is mutated, no model or raw data is read, no file is
// opened, and no private host type (Engine, ProjectPersistence,
// NativeGridTables) is referenced.
//
// The RVAs below are build-specific to the installed Avantage 6.9.0.59
// binaries. A mismatch fails; this code never scans for substitute offsets and
// never loads a library or resolves an export.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AvantageMcp.InProcess
{
    /// <summary>
    /// Read-only identity check for the native processing context.
    /// Call only from the read-only processing-plugin callback, inside Avantage.
    /// </summary>
    public static class NativeContextIdentity
    {
        // ---- fixed target (this diagnostic slice supports nothing else) ----
        const string TargetDocumentPath = @"E:\AvantageMCP\tests\live-window-repair\2.vgp";

        // ---- interface identities ------------------------------------------
        static readonly Guid ProcessingGridIid = new Guid("D0C45AA1-3DA2-11D2-93E9-006097C82421");
        static readonly Guid DocumentIid = new Guid("6E55B71A-944B-11D4-866C-00C04F68D1AC");

        // ---- build-specific RVAs, Avantage 6.9.0.59 ------------------------
        // Verified by the supervisor against the installed binaries. These are
        // not portable: a different build fails the vtable comparison.
        const long GridInterfaceVtableRva = 0x35430;      // ProcessingManager.dll
        const long GridViewVtableRva = 0x2862f8;          // ProcessGrid.dll
        const long DocumentVtableRva = 0x282698;          // ProcessGrid.dll
        const long DocumentInterfaceVtableRva = 0x2825a8; // ProcessGrid.dll

        // ---- offsets inside the already-verified objects -------------------
        const long GridInterfaceToView = 0x7e8;
        const long ViewWindowOffset = 0x40;
        const long ViewDocumentOffset = 0xe8;
        const long DocumentInterfaceOffset = 0x778;
        const int DocumentPathSlot = 14;

        // ---- fixed limits and page constants -------------------------------
        const int MaxDocumentPathBytes = 32768;
        const uint MEM_COMMIT = 0x1000;
        const uint PAGE_NOACCESS = 0x01;
        const uint PAGE_READONLY = 0x02;
        const uint PAGE_READWRITE = 0x04;
        const uint PAGE_WRITECOPY = 0x08;
        const uint PAGE_EXECUTE_READ = 0x20;
        const uint PAGE_EXECUTE_READWRITE = 0x40;
        const uint PAGE_EXECUTE_WRITECOPY = 0x80;
        const uint PAGE_GUARD = 0x100;

        /// <summary>Doc COM slot 14: HRESULT GetPathName(BSTR*).</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int GetPathNameSignature(IntPtr self, out IntPtr pathName);

        static bool layoutChecked;

        /// <summary>
        /// Validate the borrowed processing context and the single fixed test
        /// document. Throws on the first failed guard; on success every returned
        /// flag is true and the document path is the fixed target.
        /// </summary>
        public static Dictionary<string, object> Inspect(IntPtr processing)
        {
            return InspectForPath(processing, TargetDocumentPath);
        }

        internal static Dictionary<string, object> InspectForPath(IntPtr processing, string expectedPath)
        {
            if (processing == IntPtr.Zero) throw Fail("processing-null");
            RequireHostProcess();

            string binDirectory = AvantageBinDirectory();
            IntPtr managerModule = RequireModule("ProcessingManager.dll", binDirectory);
            IntPtr gridModule = RequireModule("ProcessGrid.dll", binDirectory);

            // The context is borrowed. Only this QI result is ours to release.
            IntPtr gridInterface = QueryInterfaceOrFail(processing, ProcessingGridIid, "grid-query-interface");
            try
            {
                RequireVtable(gridInterface, AddRva(managerModule, GridInterfaceVtableRva), "grid-vtable");

                IntPtr view = SubtractOffset(gridInterface, GridInterfaceToView, "view-base");
                RequireVtable(view, AddRva(gridModule, GridViewVtableRva), "view-vtable");
                RequireViewThread(view);

                IntPtr document = ReadPointer(AddOffset(view, ViewDocumentOffset, "document-field"));
                RequireVtable(document, AddRva(gridModule, DocumentVtableRva), "document-vtable");

                IntPtr documentInterface = AddOffset(document, DocumentInterfaceOffset, "document-interface");
                RequireVtable(
                    documentInterface,
                    AddRva(gridModule, DocumentInterfaceVtableRva),
                    "document-interface-vtable");
                RequireSingleIdentity(documentInterface, DocumentIid);

                // Only after every identity, readability and thread guard.
                string path = ReadDocumentPath(documentInterface);
                if (expectedPath != null && !string.Equals(path, expectedPath, StringComparison.OrdinalIgnoreCase))
                    throw Fail("unexpected-document-path");

                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                result["process_is_avantage"] = true;
                result["processing_manager_module_verified"] = true;
                result["process_grid_module_verified"] = true;
                result["grid_interface_vtable_verified"] = true;
                result["grid_view_vtable_verified"] = true;
                result["view_window_process_verified"] = true;
                result["view_window_thread_verified"] = true;
                result["main_window_thread_verified"] = true;
                result["document_vtable_verified"] = true;
                result["document_interface_vtable_verified"] = true;
                result["document_interface_identity_verified"] = true;
                result["path_name_verified"] = true;
                result["document_path_matches_target"] = true;
                result["document_path"] = path;
                result["all_guards_passed"] = true;
                return result;
            }
            finally
            {
                if (gridInterface != IntPtr.Zero) Marshal.Release(gridInterface);
            }
        }

        // --------------------------------------------------------------------
        // Process and module guards
        // --------------------------------------------------------------------
        // Only an internal, synchronous diagnostic may consume these borrowed
        // addresses. Never serialize them or accept an external native pointer.
        internal static T WithDocument<T>(IntPtr processing, Func<IntPtr, IntPtr, T> action)
        {
            return WithDocument(processing, TargetDocumentPath, action);
        }

        internal static T WithDocument<T>(IntPtr processing, string expectedPath, Func<IntPtr, IntPtr, T> action)
        {
            InspectForPath(processing, expectedPath);
            IntPtr grid = QueryInterfaceOrFail(processing, ProcessingGridIid, "grid-query-interface");
            try
            {
                IntPtr module = RequireModule("ProcessGrid.dll", AvantageBinDirectory());
                RequireVtable(grid, AddRva(RequireModule("ProcessingManager.dll", AvantageBinDirectory()), GridInterfaceVtableRva), "grid-vtable");
                IntPtr view = SubtractOffset(grid, GridInterfaceToView, "view-base");
                RequireVtable(view, AddRva(module, GridViewVtableRva), "view-vtable");
                RequireViewThread(view);
                IntPtr document = ReadPointer(AddOffset(view, ViewDocumentOffset, "document-field"));
                RequireVtable(document, AddRva(module, DocumentVtableRva), "document-vtable");
                IntPtr typed = AddOffset(document, DocumentInterfaceOffset, "document-interface");
                RequireVtable(typed, AddRva(module, DocumentInterfaceVtableRva), "document-interface-vtable");
                RequireSingleIdentity(typed, DocumentIid);
                if (!string.Equals(ReadDocumentPath(typed), expectedPath, StringComparison.OrdinalIgnoreCase)) throw Fail("unexpected-document-path");
                return action(document, typed);
            }
            finally { Marshal.Release(grid); }
        }

        static void RequireHostProcess()
        {
            using (Process current = Process.GetCurrentProcess())
            {
                if (!string.Equals(current.ProcessName, "Avantage", StringComparison.OrdinalIgnoreCase))
                    throw Fail("process-name");
            }
        }

        /// <summary>Exact installed bin directory; a different install fails closed.</summary>
        static string AvantageBinDirectory()
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (string.IsNullOrEmpty(programFiles)) throw Fail("program-files-unavailable");
            string path = Path.Combine(programFiles, "Thermo Scientific", "Avantage", "bin");
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        }

        /// <summary>
        /// Resolve an already-loaded module and require its path to sit exactly
        /// in the installed bin directory. Module names alone are not a build
        /// guard, so the full directory is compared.
        /// </summary>
        static IntPtr RequireModule(string fileName, string expectedDirectory)
        {
            IntPtr module = GetModuleHandleW(fileName);
            if (module == IntPtr.Zero)
                throw Fail("module-not-loaded", Marshal.GetLastWin32Error());

            string path = ModulePath(module);
            if (!string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase))
                throw Fail("module-file-name");

            string directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory) ||
                !string.Equals(
                    directory.TrimEnd(Path.DirectorySeparatorChar),
                    expectedDirectory,
                    StringComparison.OrdinalIgnoreCase))
                throw Fail("module-directory");

            return module;
        }

        static string ModulePath(IntPtr module)
        {
            const int Capacity = 32768;
            var buffer = new StringBuilder(Capacity);
            uint length = GetModuleFileNameW(module, buffer, (uint)Capacity);
            if (length == 0) throw Fail("module-path-unavailable", Marshal.GetLastWin32Error());
            if (length >= Capacity) throw Fail("module-path-too-long");
            return buffer.ToString(0, (int)length);
        }

        // --------------------------------------------------------------------
        // Object identity guards
        // --------------------------------------------------------------------
        static IntPtr QueryInterfaceOrFail(IntPtr unknown, Guid iid, string code)
        {
            IntPtr value = IntPtr.Zero;
            Guid requested = iid;
            int hr = Marshal.QueryInterface(unknown, ref requested, out value);
            if (hr < 0 || value == IntPtr.Zero)
            {
                if (value != IntPtr.Zero) Marshal.Release(value);
                throw Fail(code);
            }
            return value;
        }

        static void RequireVtable(IntPtr instance, IntPtr expectedVtable, string code)
        {
            IntPtr actual = ReadPointer(instance);
            if (actual == IntPtr.Zero) throw Fail(code + "-null");
            if (actual != expectedVtable) throw Fail(code + "-mismatch");
        }

        /// <summary>QI for the same IID must return the same interface pointer.</summary>
        static void RequireSingleIdentity(IntPtr documentInterface, Guid iid)
        {
            IntPtr queried = IntPtr.Zero;
            try
            {
                Guid requested = iid;
                int hr = Marshal.QueryInterface(documentInterface, ref requested, out queried);
                if (hr < 0 || queried == IntPtr.Zero) throw Fail("document-identity-query");
                if (queried != documentInterface) throw Fail("document-identity-mismatch");
            }
            finally
            {
                // Only the QI result is owned here.
                if (queried != IntPtr.Zero) Marshal.Release(queried);
            }
        }

        // --------------------------------------------------------------------
        // Thread and window guards (no activation, no UI operation)
        // --------------------------------------------------------------------
        static void RequireViewThread(IntPtr view)
        {
            IntPtr window = ReadPointer(AddOffset(view, ViewWindowOffset, "window-field"));
            if (window == IntPtr.Zero) throw Fail("window-null");

            uint windowProcess;
            uint windowThread = GetWindowThreadProcessId(window, out windowProcess);
            if (windowThread == 0) throw Fail("window-thread-unavailable");

            uint currentThread = GetCurrentThreadId();
            if (currentThread == 0) throw Fail("current-thread-unavailable");

            using (Process current = Process.GetCurrentProcess())
            {
                uint currentProcess = (uint)current.Id;
                IntPtr mainWindow = current.MainWindowHandle;
                if (mainWindow == IntPtr.Zero) throw Fail("main-window-unavailable");

                uint mainProcess;
                uint mainThread = GetWindowThreadProcessId(mainWindow, out mainProcess);
                if (mainThread == 0) throw Fail("main-window-thread-unavailable");
                if (mainProcess != currentProcess) throw Fail("main-window-process-mismatch");

                if (windowProcess != currentProcess) throw Fail("view-window-process-mismatch");
                if (windowThread != currentThread) throw Fail("view-window-thread-mismatch");
                if (mainThread != currentThread) throw Fail("main-window-thread-mismatch");
            }
        }

        // --------------------------------------------------------------------
        // The only COM call: doc slot 14 GetPathName
        // --------------------------------------------------------------------
        static string ReadDocumentPath(IntPtr documentInterface)
        {
            // The vtable was already proven equal to the known build RVA, so the
            // slot meaning is known. The slot pointer itself is read under the
            // same readability guard; an unverified address is never called.
            IntPtr vtable = ReadPointer(documentInterface);
            IntPtr slot = ReadPointer(AddOffset(vtable, (long)DocumentPathSlot * IntPtr.Size, "path-slot"));
            if (slot == IntPtr.Zero) throw Fail("path-slot-null");
            RequireReadable(slot, 1);

            var getPathName =
                (GetPathNameSignature)Marshal.GetDelegateForFunctionPointer(slot, typeof(GetPathNameSignature));

            IntPtr bstr = IntPtr.Zero;
            int hr;
            try
            {
                hr = getPathName(documentInterface, out bstr);
            }
            catch (Exception error)
            {
                throw Fail("path-name-call: " + error.GetType().Name);
            }

            // Free only a pointer the call actually produced on success.
            if (hr < 0 || bstr == IntPtr.Zero) throw Fail("path-name-hr");
            try
            {
                return ReadBoundedBstr(bstr);
            }
            finally
            {
                Marshal.FreeBSTR(bstr);
            }
        }

        /// <summary>
        /// Read a BSTR whose 4-byte byte-length prefix sits immediately before
        /// the character data. Both ranges are guarded before any read.
        /// </summary>
        static string ReadBoundedBstr(IntPtr bstr)
        {
            long address = bstr.ToInt64();
            if (address <= 4) throw Fail("bstr-address");

            IntPtr lengthAddress = new IntPtr(address - 4);
            RequireReadable(lengthAddress, 4);
            int byteLength = Marshal.ReadInt32(lengthAddress);

            if (byteLength <= 0 || byteLength > MaxDocumentPathBytes) throw Fail("bstr-length");
            if ((byteLength & 1) != 0) throw Fail("bstr-length-odd");

            // Character data plus the two-byte terminator must be readable.
            RequireReadable(bstr, byteLength + 2);

            string text = Marshal.PtrToStringUni(bstr, byteLength / 2);
            return text ?? string.Empty;
        }

        // --------------------------------------------------------------------
        // Guarded memory access
        // --------------------------------------------------------------------
        static IntPtr ReadPointer(IntPtr address)
        {
            RequireReadable(address, IntPtr.Size);
            return Marshal.ReadIntPtr(address);
        }

        internal static IntPtr ReadField(IntPtr instance, int offset)
        {
            return ReadPointer(offset == 0 ? instance : AddOffset(instance, offset, "field-offset"));
        }

        /// <summary>
        /// Prove the whole range is committed, readable, not a guard page, and
        /// inside the region VirtualQuery reports. The range must not straddle
        /// a region boundary; a partial answer fails closed.
        /// </summary>
        static void RequireReadable(IntPtr address, int size)
        {
            RequireLayout();
            if (size <= 0) throw Fail("range-size");

            long start = address.ToInt64();
            if (start <= 0) throw Fail("range-address");
            long end = start + size;
            if (end <= start) throw Fail("range-overflow");

            MemoryBasicInformation info;
            UIntPtr queried = VirtualQuery(
                address, out info, new UIntPtr((uint)MemoryBasicInformation.Size));
            if (queried.ToUInt64() < (ulong)MemoryBasicInformation.Size) throw Fail("virtual-query", Marshal.GetLastWin32Error());

            long regionStart = info.BaseAddress.ToInt64();
            long regionSize = (long)info.RegionSize.ToUInt64();
            if (regionStart <= 0 || regionSize <= 0) throw Fail("region-invalid");

            long regionEnd = regionStart + regionSize;
            if (regionEnd <= regionStart) throw Fail("region-overflow");
            if (start < regionStart || end > regionEnd) throw Fail("range-outside-region");

            if (info.State != MEM_COMMIT) throw Fail("region-not-committed");

            uint protect = info.Protect;
            if ((protect & PAGE_GUARD) != 0) throw Fail("region-guard-page");

            uint basic = protect & 0xFFu;
            if (basic == PAGE_NOACCESS) throw Fail("region-no-access");
            if (!IsReadableProtection(basic)) throw Fail("region-not-readable");
        }

        static bool IsReadableProtection(uint basic)
        {
            return basic == PAGE_READONLY
                || basic == PAGE_READWRITE
                || basic == PAGE_WRITECOPY
                || basic == PAGE_EXECUTE_READ
                || basic == PAGE_EXECUTE_READWRITE
                || basic == PAGE_EXECUTE_WRITECOPY;
        }

        /// <summary>Refuse to trust the marshalled layout on a non-x64 process.</summary>
        static void RequireLayout()
        {
            if (layoutChecked) return;
            if (IntPtr.Size != 8) throw Fail("not-x64");
            if (Marshal.SizeOf(typeof(MemoryBasicInformation)) != MemoryBasicInformation.Size)
                throw Fail("virtual-query-layout");
            layoutChecked = true;
        }

        // --------------------------------------------------------------------
        // Pointer arithmetic with explicit overflow and zero checks
        // --------------------------------------------------------------------
        static IntPtr AddRva(IntPtr moduleBase, long rva)
        {
            long baseAddress = moduleBase.ToInt64();
            if (baseAddress <= 0 || rva <= 0) throw Fail("module-rva");
            long expected = baseAddress + rva;
            if (expected <= baseAddress) throw Fail("module-rva-overflow");
            return new IntPtr(expected);
        }

        static IntPtr AddOffset(IntPtr instance, long offset, string code)
        {
            long address = instance.ToInt64();
            if (address <= 0) throw Fail(code);
            long result = address + offset;
            if (result <= address) throw Fail(code);
            return new IntPtr(result);
        }

        static IntPtr SubtractOffset(IntPtr instance, long offset, string code)
        {
            long address = instance.ToInt64();
            if (address <= offset) throw Fail(code);
            return new IntPtr(address - offset);
        }

        // --------------------------------------------------------------------
        // Failure helper and native declarations
        // --------------------------------------------------------------------
        static Exception Fail(string code)
        {
            return new InvalidOperationException("native-context-identity: " + code);
        }

        static Exception Fail(string code, int win32)
        {
            return new InvalidOperationException("native-context-identity: " + code + " (win32 " + win32 + ")");
        }

        /// <summary>
        /// MEMORY_BASIC_INFORMATION, x64 layout: 48 bytes. BaseAddress +0,
        /// AllocationBase +8, AllocationProtect +16, RegionSize +24, State +32,
        /// Protect +36, Type +40. Declared explicitly so no packing guess is
        /// involved; <see cref="RequireLayout"/> checks the marshalled size.
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 48)]
        struct MemoryBasicInformation
        {
            internal const int Size = 48;

            [FieldOffset(0)] internal IntPtr BaseAddress;
            [FieldOffset(8)] internal IntPtr AllocationBase;
            [FieldOffset(16)] internal uint AllocationProtect;
            [FieldOffset(24)] internal UIntPtr RegionSize;
            [FieldOffset(32)] internal uint State;
            [FieldOffset(36)] internal uint Protect;
            [FieldOffset(40)] internal uint Type;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern UIntPtr VirtualQuery(
            IntPtr address, out MemoryBasicInformation buffer, UIntPtr length);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr GetModuleHandleW(string moduleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetModuleFileNameW(IntPtr module, StringBuilder fileName, uint size);

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    }
}
