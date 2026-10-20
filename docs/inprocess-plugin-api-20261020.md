# Read-only in-process plugin probe: implementation contract

This is the next development slice, not a request to deploy a document writer. It must only inspect its execution context. No model edits, native private-pointer casts, UI automation, registration, or application connection belong in this worker slice.

## Why investigate this route

Out-of-process COM pointers cannot serve as local CProcessGridDoc C++ object addresses. Direct live whole-table import and deletion also failed structural live validation; see `live-com-import-findings-20261020.md`. The vendor grid offers a normal processing-plugin callback executed through its own UI dispatch. A read-only probe can establish whether a supported in-process callback supplies the correct context before any writer is attempted.

## Binary evidence collected by Codex (not guessed APIs)

In ProcessGrid.dll, OnDeferredCreateProcessingPlugin at RVA0x19c600 parses a class GUID using CLSIDFromString, then CoCreateInstance with context3 (in-process server/handler), IID IDataProcessControl. It QIs IDataProcess. The DLL is the already hash-gated Avantage6.9.0.59 build.

The registered proxy in ProcessingInterfacesps.dll describes:

- IDataProcessControl: IID597365F0-21FB-11D2-B9D9-006097D69B02,6 COM slots total.
- IDataProcess: IID3FB981F0-56AB-11D1-8AD5-006097D69B02,9 COM slots total.
- IProcessingGrid context QI IID: D0C45AA1-3DA2-11D2-93E9-006097C82421.

ProcessingPlugInBase.dll exported CMenuHelperBase interface vtables give these slot meanings/signatures. All returns are HRESULT32, PreserveSig; the first3 slots are IUnknown. Do not include later C++-only virtual methods as COM methods.

IDataProcessControl:

| Slot | Method | Native arguments after self |
|---|---|---|
|3|ControlProcess|IProcessing*, IStorage*, LONG inputCount, IDataSpace** inputs, IDataAxes** axes, LONG* outputCount, IDataSpace*** outputs, enum32 flags|
|4|Reprocess|IProcessing*, IDataSpace*, IStream*|
|5|SetProcessingSchema|enum32 schema|

IDataProcess:

| Slot | Method | Native arguments after self |
|---|---|---|
|3|QuerySummaryAttributes|IStorage*|
|4|QueryProcessAttributes|IStorage*, LONG inputCount, IDataSpace** inputs, IDataAxes** axes, LONG* outputCount|
|5|ExecuteProcess|IStorage*, LONG inputCount, IDataSpace** inputs, IDataAxes** axes, LONG outputCount, IDataSpace** outputs (caller array, NOT a triple pointer)|
|6|PropertiesToString|BSTR* output|
|7|SetProcessAttributes|IStorage*|
|8|ProcessControlComplete|LONG status|

These signatures come from the vendor exported interface vtables and decorated names. Codex will independently cross-check NDR metadata before real activation. Worker must flag ambiguities rather than infer more interfaces.

## Required three files

- `native/inprocess/ContextProbe.cs`: standalone .NET Framework C# COM library implementation, NOT included in native/*.cs bridge build. Define both ComVisible IUnknown interfaces with the exact IIDs and slot order, PreserveSig HRESULT. Raw borrowed interface/array pointers may be represented by IntPtr in this in-process ABI; use out int / out IntPtr only where native signatures actually have output pointers. ClassInterface.None, own CLSID96D0A0F0-56FC-4305-92B0-0BA9CB776AAE. Assembly ComVisible(false), mark only the necessary class/interfaces visible.
- `scripts/build-inprocess-probe.ps1`: explicit OutputPath required, build DLL as x64 .NET Framework with existing Roslyn compiler convention. Reference System.Web.Extensions.dll. No installation/registration, no registry changes, no application/process start. Do not alter the normal bridge build.
- `docs/inprocess-context-probe.md`: developer usage, exact ABI evidence versus remaining hypotheses, no deployment/writer-completion claims.

## Strict read-only behavior

Constructor must not execute commands or perform any I/O. Plugin callbacks never read/write project models, raw data, private object addresses, files, registry or network. No new windows, dialogs or processes. No arbitrary path/command input. Do not reference or call ProjectPersistence/Engine/NativeGridTables: those private hosts intentionally pin a separate cache and MUST NOT run inside the application.

ControlProcess may only:

1. Set outputCount=0 and outputs=null on every path.
2. Require the current process name to be Avantage. Require non-null context and storage, and inputCount between0 and32. Do not dereference data/axes arrays.
3. Open the caller-supplied IStorage stream `AvantageMcp.ProbeRequest`, read at most64 bytes via standard IStream, and require exact UTF-8 marker `inspect-context-v1` after trimming. If absent/invalid, return E_INVALIDARG and do not write anything. This prevents writing diagnostics into an unintended storage object.
4. Query the borrowed IProcessing context for IProcessingGrid, record whether QI succeeds; release only the owned QI pointer, never release borrowed parameters.
5. Collect process id/name, OS current thread id, main-window thread id/process id (GetWindowThreadProcessId), boolean thread_matches_main_window and grid_interface_available, schema and inputCount. No application/private-memory reads.
6. Write bounded UTF-8 JSON to caller IStorage stream `AvantageMcp.Context` using standard IStorage CreateStream slot3 / IStream and commit. No other persistence. The later verified implementation deliberately returns E_ABORT after a successful report, triggering the host's normal processing-completion cleanup branch. Require the explicit report success fields; E_ABORT alone is never success. This supersedes the initial S_OK completion hypothesis. If it fails after a valid marker, return HRESULT with a bounded diagnostic error; never leak pointers, credentials or stack dumps.

Use standard IStorage OpenStream slot4, CreateStream slot3, standard COM IStream methods. Bound stream Stat size to64 bytes before allocating/reading the marker; require complete read. Output JSON limit4096 bytes. Correctly release owned streams and RCWs, do not FinalRelease borrowed objects.

Other callbacks are conservative: QueryProcessAttributes reports zero outputs; QuerySummaryAttributes, SetProcessAttributes, SetProcessingSchema and ProcessControlComplete are non-mutating S_OK (schema may be stored in an instance field). ExecuteProcess and Reprocess may return E_NOTIMPL; they must not write document or output data. PropertiesToString returns a correctly allocated BSTR diagnostic label. Exceptions must not cross COM boundaries.

## Supervisor-owned validation / future work

Worker writes source only, no shell. Codex will compile, inspect COM-visible attributes and reflection ABI, and test marker handling without Avantage. Only after that will Codex decide registration/activation for a single test window. A successful read-only callback is not proof that native writeback is safe. A future writer would need to validate process/module/thread/context identity and own rollback before any native C++ export call; no writer implementation is requested in this slice.
