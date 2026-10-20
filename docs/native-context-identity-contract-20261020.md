# Next bounded slice: readonly native document identity

Do not implement a writer or install anything. Add only `native/inprocess/NativeContextIdentity.cs` and `docs/native-context-identity.md`. Supervisor owns compilation and activation. Read AGENTS.md if present, preserve earlier changes. Shell is unavailable in the worker. Existing ContextProbe stays unchanged during this slice.

Implement public static `AvantageMcp.InProcess.NativeContextIdentity.Inspect(IntPtr processing)` returning Dictionary<string,object>. This helper will be called only by the readonly processing-plugin callback, inside Avantage. It must fail closed with a short managed exception whenever a guard fails. No model access, pointer exports, native mutations, original-file operations, registry/network/process/window changes. No Engine/private cache dependencies. No native-library loading or GetProcAddress: GetModuleHandleW of already-loaded installed modules only.

Supervisor has proved two consecutive real plugin callbacks in PID1192 on UI thread23412, IProcessingGrid QI available, unchanged 24-spectrum test model/raw/identities and six original documents' metadata. This is context proof only. The only permitted target path for the new helper is E:\AvantageMCP\tests\live-window-repair\2.vgp (case-insensitive exact full path). Never support an arbitrary user-supplied path in this diagnostic slice.

Verified installed Avantage6.9.0.59 ABI:

- IProcessingGrid IID D0C45AA1-3DA2-11D2-93E9-006097C82421. QueryInterface the borrowed context, release only the owned result. Its vtable must equal loaded ProcessingManager.dll base+RVA0x35430.
- Native CProcessGridView base is that local grid interface address minus0x7e8. Its primary vtable must equal ProcessGrid.dll base+RVA0x2862f8.
- View+0x40 is HWND; GetWindowThreadProcessId must match current PID and OS thread. Require current process name Avantage and the thread also matches Process.MainWindowHandle's thread. No window activation or other UI operation.
- View+0xe8 holds the CProcessGridDoc native base. Its primary vtable must equal ProcessGrid.dll base+RVA0x282698.
- Local doc XProcessingDocument interface is native doc base+0x778; its vtable must equal ProcessGrid.dll base+RVA0x2825a8. QueryInterface DocIID6E55B71A-944B-11D4-866C-00C04F68D1AC must return the same local interface pointer (single inheritance identity evidence). Release only QI result.
- Doc COM slot14 is HRESULT GetPathName(BSTR*). Call only after all vtable, readability and thread guards. Free returned BSTR. Require exact fixed test path before success. No other document getter or export needed.
- Module names alone are not a build guard: require loaded module paths exactly under Environment.SpecialFolder.ProgramFiles/Thermo Scientific/Avantage/bin. External supervisor will hash-check the installed binaries before activation (existing ProcessingCom.Connection gate). Include a comment that these RVAs are build-specific; mismatches fail, never scan for substitute offsets.
- Before EACH direct memory read, use VirtualQuery to prove the entire range committed, readable, not PAGE_GUARD or PAGE_NOACCESS, and contained in the returned region with overflow-safe range math. Check native pointer arithmetic for overflow/zero. Object vtable pointers must match exact known values, no unknown vtable call. VirtualQuery structure layout must be correct for x64; no data array dereferences.

Return only booleans for validated module/vtable/thread/document identity and the fixed document path; no raw pointer addresses or original spectra/data. ContextProbe remains the baseline isolated no-private-memory probe; this new helper is an explicit subsequent ABI identity diagnostic, not part of its old strict contract.

Source evidence: docs/inprocess-plugin-api-20261020.md, docs/live-com-import-findings-20261020.md, native/ProcessingDocuments.cs (Doc getters/COM contracts), native/NativeGridTables.cs (module build gate if present). Do not copy or call private-host initialization from them. Standard Microsoft Win32 documentation may be researched; cite primary URLs for material external claims. Return exact changes, checks not run and unresolved concerns. No commits/pushes/external messages/credential reads/destructive cleanup/.git writes; request a concrete supervisor decision for capability boundary issues.

Important correction to earlier notes: grid COM32 maps primary0x4b0 GetProcessingPluginSummaryAttributes(IStorage**), while grid COM33 maps primary0x4a8 GetProcessingPluginCallAttributes(IStorage*). They swap primary order! Actual wrapper disassembly plus NDR directions confirmed this. Do not infer COM order from the native primary vtable.
