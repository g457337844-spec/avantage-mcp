# COM import investigation — verified boundaries

These findings are development evidence, not a claim that production structural live editing works. Original user samples have not been modified.

## Latest live test outcome (takes precedence over the private controls)

The guarded test window was opened once, and a complete *unchanged* graph reloaded via document COM slot17. All24 raw/model/grid snapshots, actual IDataSpace COM identities, document path and single-window count matched (`live-roundtrip-result.json`).

A structural0-to6 creation trial on originally empty, hidden Cu2p spectrum5 did NOT pass expected-model readback. Re-importing a complete rollback table did not remove those added peaks. The ordinary Save operation was never reached. A later invocation of document COM slot25 (statically mapped to RemovePeaksFromDoc) worked on a private native document but returned RPC_E_SERVERFAULT in the application. No retry was sent. A read-only audit confirmed only spectrum5 differed from disk; all24 raw/visibility snapshots and all other spectrum models matched.

Recovery: sent ordinary File Close only after verifying exactly one document at the fixed test path. Inspected the resulting Save prompt and chose No. Native MCP then confirmed documents=[] and the application still running. Test in-memory changes were discarded. No original user sample was opened or written, and no test save was executed. No Computer Use connector was activated.

Do NOT promote document slot17/25 to production structural editing based on the private controls. They are insufficient evidence for safe live ownership and cleanup. Production native source/executable are unchanged.

Further static mapping: grid COM slots24/25 are DeferredLoadPeakTable / DeferredLoadSelectedPeaks (primary offsets0x468/0x470). Selected-peaks arrays contain count+1 elements; indices[0] is selected count, selected indices start at1; destination IDataSpace pointers also start at1. Grid37 LoadDummyDocPeakTable targets a separate dummy document at view+0x908. Do not call these guessed workflows without establishing the full source/destination semantics; no such grid import was executed.

## Interface mapping, statically verified

For the hash-gated ProcessGrid.dll build, CProcessGridDoc's XProcessingDocument COM vtable is RVA0x2825a8. Slot17 wrapper RVA0x161cc0 subtracts interface offset0x778, dispatches primary C++ vtable offset0x2a0. Primary vtable RVA0x282698 at offset0x2a0 points to exported LoadPeakTable RVA0x82280. Thus document COM slot17 is the actual BSTR LoadPeakTable method. Classic NDR slot17 signature is BSTR input plus HRESULT. This is different from passing the COM proxy address to a local C++ export.

Slot19 wrapper RVA0x15dad0 likewise calls ClonePeaksFromDataSpace RVA0x15bf40, accepting two IDataSpace pointers. That implementation enumerates the current document's own peak collections; do not assume it can clone peaks from an arbitrary external document.

Grid slots57/58 forward to document slots10/12 (generic document operations), not LoadPeakTable. Their queued messages are0x44c/0x44d; handlers0x19ef40/0x19e810 QI the document IID and invoke offsets0x50/0x60. Do not label these peak-table import methods.

## Private native positive controls run by Codex

Probe sources, executable and JSON outputs are under:
`C:\Users\Administrator\Documents\Codex\outputs\avantage-com-load-proof-20261020`.
Input only `E:\AvantageMCP\tests\live-window-repair\2.vgp` and its test companion directory. Probe compiled from current native source with separate entrypoint; production bridge executable not replaced. Private controls do not connect to the application.

1. Export a grouped VGT, then invoke COM slot17 on a fresh owned native document whose model is initially empty. **6 of6 fitted peaks loaded**, complete parameters/backgrounds/constraints matched via PersistedEquivalent; raw fingerprint unchanged.
2. Invoke the same operation on a populated private24-spectrum project using only a single-region VGT. **Unsafe for general live use**: spectrum count remains24 and raw arrays unchanged, but unrelated models are lost/changed and association shifts. The full result is `full-private-result.json`. Do not enable this single-region import as production apply.
3. Save a complete native project graph (all collection streams) after changing a test peak label; invoke slot17 using that complete graph on the populated private document. **All24 spectrum model and raw snapshots preserved**, count24 before/after. Result: `whole-private-result.json`. This is only an in-process private control; live cache identities, grid bindings, save/reopen still require validation.

## Application state

Application was responsive and initially had no documents open. A separate guarded probe opened exactly one authorized test document at `E:\AvantageMCP\tests\live-window-repair\2.vgp`; it reports24 spectra. No original sample was opened or changed, no fit edit or save was sent. Result: `open-only-result.json`.

Production automatic open/close remain disabled. This one test-only open used the existing native UI path with an empty-application precondition and a fixed test-root guard. Do not duplicate it. No Computer Use was used. Preserve the persistent application anchor; do not terminate it with a probe/worker.

## Next validation requirements

Before any live structural import, compare every live raw/model snapshot against a complete private graph. Rebase staging data references to actual live source paths, not private snapshot copies, and verify live IDataSpace identities after import. A complete graph avoids dropping other models but does not by itself prove that grid objects still refer to the correct spectra. Snapshot all models and bindings, keep a full rollback graph, require one existing test window, and verify normal save plus disk reload. Do not call a partial VGT into the real application. No release or GitHub upload yet.
