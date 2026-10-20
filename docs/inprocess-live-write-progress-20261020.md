# Current verified progress — 2026-10-20

This takes precedence over older failed direct COM import notes. Production MCP remains1.4.0 and still exposes parameter-only live apply. Do not advertise a released structural writer yet.

## Proven method

Use the vendor's processing-plugin callback, running on Avantage UI thread, to obtain actual **local** CProcessGridView/CProcessGridDoc native addresses after exact module/vtable/thread/path guards. Do not pass remote COM proxy pointers to local C++ methods. Do not initialize Engine, NativeGridTables, private native documents or independent cache inside Avantage.

Standalone sources live under native/inprocess and are excluded from production native/*.cs bridge glob. Build script supports baseline, IncludeNativeIdentity and IncludePeakRoundtrip research variants. FitInterfaces.cs is pure COM declarations and may be compiled into the standalone research library; other private bridge implementation must not be linked into it.

Registered own CLSIDs only temporarily under HKCU/Software/Classes/CLSID; removed each one after its validation. Do not modify vendor interface registrations or HKLM. CLR retains loaded assemblies, so each new research class/version has a distinct class id and assembly name. Loaded test classes:

- ContextProbe: ...776AAE, assembly AvantageMcp.InProcessProbe.dll.
- Abandoned summary transport experiment: ...776AAF, assembly AvantageMcp.InProcessProbe.Summary.dll. No longer in current source. Do not reuse.
- IdentityProbe: ...776AB1, assembly AvantageMcp.InProcessProbe.Identity.dll.
- PeakRoundtripProbe: ...776AB2, assembly AvantageMcp.InProcessProbe.Roundtrip.dll.
- PeakPersistenceProbe: ...776AB3, assembly AvantageMcp.InProcessProbe.Persistence.dll.

Exact CLSID prefix96D0A0F0-56FC-4305-92B0-0BA9CB. Last six hex above complete each GUID.

## Interface correction, important

COM order does **not** simply follow the native primary C++ vtable:

- Grid29 → primary0x490: DeferredCreateProcessingPlugin(event64, BSTRclsid, IStorage*, enum32schema).
- Grid30 → primary0x498: DeferredCallProcessingPlugin(event64, IUnknown*caller, IStorage*, count32, IDataSpace**inputs, IDataAxes**axes, enum32flags, BOOLforcePost).
- Grid32 → primary0x4b0: GetProcessingPluginSummaryAttributes(IStorage**out). Native method returns a borrowed pointer without AddRef; avoid relying on this for production transport.
- Grid33 → primary0x4a8: GetProcessingPluginCallAttributes(IStorage*destination). It copies internal call storage into caller-owned storage then releases the internal storage. Call once per callback. This is the verified diagnostic result transport.
- Grid51 reads deferred HRESULT.

The probe writes a JSON report into caller-provided IStorage (marker and4096-byte limits), then deliberately returns E_ABORT. Host OnDeferredCallProcessingPlugin negative-HRESULT branch calls DataProcessingComplete(RVA0x198a60), releasing the active control. Two consecutive activations prove lifecycle cleanup. `probe_completed=true` in the retrieved report, not HRESULT alone, establishes diagnostic success. Normal writer completion may use the same controlled cancellation cleanup initially, with an explicit successful-operation result; never treat arbitrary E_ABORT alone as success.

## Real live proofs

App PID1192, UI thread23412. Only fixed test `E:\AvantageMCP\tests\live-window-repair\2.vgp` was opened by this work. Six user originals remain open and untouched. User explicitly authorized keeping originals open and opening one test copy, so do not demand an empty application or close originals.

1. Baseline callback twice: right process/thread/grid interface. All24 raw/model/grid snapshots unchanged, disk unchanged, six original document identity/path/modified metadata unchanged.
2. Native identity twice: module paths/vtable RVAs/QI document identity/exact fixed path all pass. DSH helper initially used view+0xe8 field address as document pointer; supervisor fixed to ReadPointer(view+0xe8) before compilation/activation. Also fixed QI failure cleanup and added main-window PID and VirtualQuery returned-size guards.
3. Existing-region topology roundtrip twice on Cu2p index23: **6→7→6**. New real native fitted peak created **inside app**, bound to app-owned data/background/primary, native AddFittedPeak actual local doc base invoked. RemoveFittedPeak then removes only the new candidate. All24 live raw/model/grid snapshots restored, no file save. Six original document metadata unchanged.
4. Persistence: retain candidate **6→7**, ordinary File Save on the same test window, full private disk reread verifies all24 raw/model/grid states. Then cleanup uniquely labelled candidate **7→6**, ordinary File Save again, full reread passes. Same window and app process throughout; no close/reopen/restart. Full final snapshot equals initial with PersistedEquivalent. Six original document metadata unchanged.
5. Shared model-state reader: the in-process model snapshot and raw-data fingerprint match the existing external bridge on the test spectrum. Its six existing fitted peaks have six distinct primary entries; do not merge their parent relationships.
6. Empty spectrum: create one native primary entry and fitted peak, ordinary Save and complete reread, then remove the test objects and Save/reread again. Fitted count **0→1→0**, document primary count **20→21→20**; full raw/model/grid snapshots restored in the same window.

Normal Save invalidates previously enumerated data/peak links: the first persistence driver reused an old data vector and got E_INVALIDARG only at its final audit, after both saves had verified. Supervisor corrected the driver to reacquire data after Save; complete repeated run passed. Production already says reattach after save; preserve that contract or transparently reattach in orchestration.

## Proof artifacts

`C:\Users\Administrator\Documents\Codex\outputs\avantage-inprocess-probe-20261020`:

- live-context-result.json
- live-native-identity-result.json
- live-peak-roundtrip-result.json
- live-peak-persistence-result.json (complete successful run)
- persistence-checkpoints.json (two separately verified save reports)
- before-persistence/ (backup of test2.vgp and2.DATA only)
- LiveContextProbe.cs/exe (current external guarded driver, currently configured for AB3 retain/save/remove/save)
- ProbeTests.cs/exe, various independently compiled assemblies; current reusable test sources are in tests/native/.

The persistence driver preflights **all** source paths inside the test root before any Save. Temporary candidate label is `MCP reversible ownership probe`, and was removed and saved. Test2.vgp is now clean with its original six model components. Its file hash changes after ordinary native serialization, which is expected; raw/model/grid equality was verified.

## Remaining required work

- Integrate a bounded model-plan protocol and in-process writer into production MCP; current research probes are **not** the public feature.
- General background/constraint/topology application with validated before-state, rollback and readback. Existing-region add/remove and the bounded empty-region probe are proven; the general transaction writer is not yet activated or verified end-to-end.
- Installation/versioned assembly/own CLSID lifecycle must avoid hot-replacing already-loaded code, shared-cache initialization, accidental windows and vendor registration changes.
- Validate the new public MCP structural-write path end-to-end, meaningful regressions and normal save without closing before a feature release. A private source snapshot may include explicitly marked unfinished research code.

Latest snapshot validation: **268 passed,37 skipped** and a successful isolated normal bridge build. See [source-snapshot-20261020.md](source-snapshot-20261020.md). The following older test counts describe their historical checkpoint.

Tests as of last full run:204 passed,37 skipped before native identity tests were added; latest targeted3 tests pass (30 baseline COM cases,8 callback cases,16 native memory/identity cases). Full regression must be rerun after final implementation. No Computer Use connector, REA or RAE used in this continuation. DSH SG/max supplied baseline code and native identity helper; supervisor independently fixed, compiled and tested them.
