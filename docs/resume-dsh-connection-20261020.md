# Resume DSH supervised Avantage work

Updated 2026-10-20 11:45 Asia/Singapore. This is a progress record, not a completion claim.

## Latest correction and user instruction (takes precedence over older notes)

The user explicitly says stay in THIS conversation; do not request a new conversation. Await native connection refresh here.
The earlier `${PLUGIN_ROOT}` change was incorrect for this installed Codex version. `codex mcp list --json` showed that variable remained literal in both args and cwd, producing a nonexistent directory. Reverted source args to `./dist/dsh-relay-proxy.mjs` and cwd to `.`; retained the authorized Avantage allowed root. Rebuilt successfully and officially reinstalled as `0.2.19-local.2+codex.20261020114247`.
Actual resolved configuration now confirms working directory exists, entrypoint exists, enabled=true, and no unexpanded variable. Native tools are still absent in current chat; a user connection refresh request is pending. DSH backend PID15720 remains listening on3081, not restarted.
Targeted verification of DSH's new tests failed during collection: `tests/test_processing_proxy_inventory.py` dynamically imports the dataclass-containing tool without registering the module in `sys.modules`, causing AttributeError inside dataclasses. Do not claim tests pass. Script still has incorrect stub count offset8 and NDR stack offset4 at last read. These must be returned to the existing worker via native Relay after connection restoration. Do not overlap edits while its task state remains uncollected.

## User intent

Use native DeepSeek Harness Relay to supervise the existing worker and fix Avantage live fit-model writeback, including creating peaks on an empty spectrum in the same window and normally saving/reopening. Preserve existing DeepSeek changes and original sample files. Do not open many visible copies, activate Computer Use, kill Avantage, or fall back to shell Relay RPC. User authorized DSH implementation and verification. No GitHub release until actually usable and verified.

## Existing accepted implementation task

- runId: `110966cb-f5d7-4ff1-a563-f075649a3ccd`
- sessionId: `session-d27f15aa-d2c1-46ed-9c96-e88a820419fd`
- Web URL: http://127.0.0.1:3081/?sessionId=session-d27f15aa-d2c1-46ed-9c96-e88a820419fd
- Registered workspace: `E:\Desktop\文件\工具\avantage-mcp`
- workspaceId: `765515b2-5ba6-4aad-a007-999e1b854516`
- Actual last observed route: `workbuddy-ai/deepseek-v4.1-flash-sg`, reasoning `max`, workspace-write. This is the direct WorkBuddy route, not Magpie.
- Write scope only: `scripts/inspect_processing_proxy.py`, `tests/test_processing_proxy_inventory.py`, `docs/processing-proxy-inventory.md`.
- Baseline: `C:\Users\Administrator\Documents\Codex\outputs\avantage-proxy-inventory-20261020\baseline.json`.
- Native monitoring became unavailable before terminal collection. Do not duplicate dispatch. First use native wait/status on the same run and consume final assistantText.
- A read-only local projection check observed open turn 11 at sequence 909; this is not a native terminal result. Script and test files appeared, but docs had not yet appeared at last filesystem check.

## Connection repair made in this continuation

Native Harness tools are absent from ALL_TOOLS even after user refreshed. Installed plugin is enabled; backend port 3081 is still owned by PID 15720. No backend restart performed.

Source: `E:\Desktop\文件\工具\DeepSeek连接\relay-mcp-0.2.19`.
Changed owning source `.mcp.json`: use `${PLUGIN_ROOT}` for cwd/entrypoint, and add the authorized Avantage workspace to explicit allowed roots. Preserved existing allowed roots.
Ran `pnpm run prepare:codex-local`: typecheck/build/version verification passed (3 artifacts, 3 required proxy tools).
Then generated manifest cachebuster and installed using official `codex plugin add deepseek-harness-relay@deepseek-local --json`.
Installed version: `0.2.19-local.2+codex.20261020111921`.
Installed path: `C:\Users\Administrator\.codex\plugins\cache\deepseek-local\deepseek-harness-relay\0.2.19-local.2+codex.20261020111921`.
Checked installed runtime/entrypoint existence and Node syntax; passed. Native tool discovery/doctor still NOT verified; needs a new Codex conversation per skill. Do not claim connection works from manifest/version alone.
Backups and first actual-DLL inventory: `C:\Users\Administrator\Documents\Codex\outputs\relay-connection-repair-20261020`.

## Important independent verification failure

Executed DSH's current script read-only against vendor `ProcessingInterfacesps.dll`; it reports invalid stub, ambiguous proxy, and no slot count. The synthetic tests may mirror incorrect structure layouts; passing them alone is insufficient.

Current script constants incorrectly place stub DispatchTableCount at IID+8. Actual x64 CInterfaceStubVtbl header is IID pointer, pServerInfo pointer, uint32 DispatchTableCount with padding, pDispatchTable. Count is IID+16. Proxy layout also is incorrect: MIDL_STUBLESS_PROXY_INFO pointer precedes IID pointer, followed by inline dispatch slots; it is not IID plus a pointer to a separate vtable. The script invents an RPC_SERVER_INTERFACE search which is not needed for this object-interface association. NDR header field offsets are also incorrect (stack size actually +8, not +4). Return these findings through native steer/reply only, scoped to existing files, with path references rather than pasted code. Do not edit these files while worker is active.

Vendor binary SHA256: `35CB5A0E55CCE5584ADA0BD745CD3108087C1B4F7A5FFF3D7729F801DFF83D17`, ImageBase `0x180000000`.
Document IID `6E55B71A-944B-11D4-866C-00C04F68D1AC`: standalone IID RVA 0x175f0; stub header 0xab50, server info 0x13b90, count **29**; proxy IID pointer 0x1e648, preceding proxy-info pointer at 0x1e640 -> proxy info 0x13c78. Proc format RVA 0xe702, biased format-offset base 0xb7e2; use base+slot*2 for slot >=3.
Grid IID `D0C45AA1-3DA2-11D2-93E9-006097C82421`: stub header 0xa1f0, server info 0x13830, count **80**; proxy IID pointer 0x1e1a8, proxy info 0x13870, format-offset base 0xb6fa, same proc format 0xe702.
Classic NDR procedure: opnum uint16+6; stack uint16+8; Oi2 flags byte14; param count byte15; extension length byte16 when flags&0x40; parameter descriptors begin 16+extension_length, each 6 bytes. Static descriptions do not prove semantic identity or safe invocation.

## Core ABI finding already independently confirmed

Experimental `native/ApplyLiveModelCreate.cs` passes an out-of-process COM proxy as native `CProcessGridDoc*` to local ProcessGrid.dll AddPeak/AddFittedPeak. This is invalid. AddPeak reads this+0x3c0; AddFittedPeak reads this+0x498. Keep this path disabled. A C++ virtual method is not automatically a COM method; do not scan arbitrary 96 vtable slots.

Possible real COM route still being investigated statically, NOT called:
Grid slots 57/58 have signature event handle, IUnknown/document, BSTR, HRESULT. ProcessingManager.dll interface vtable RVA0x35430 wrappers subtract this0x7e8 and call primary vtable offsets 0x578/0x580. ProcessGrid.dll CProcessGridView primary vtable RVA0x2862f8 maps these to functions 0x194b30/0x1945c0. Their prologues check pending event handle (this+0x8f8), copy BSTR path to this+0x958/0x968 using CoTaskMem, and queue work. Continue static mapping to handlers/LoadPeakTable before any application experiment. Exact semantic identity unproven.

External-only research deps pefile/capstone: `C:\Users\Administrator\Documents\Codex\outputs\avantage-native-inspection-deps`. These are not runtime dependencies of the requested parser, which must use standard library. Do not load/execute vendor DLL for inventory tests.

## Verification after native connection returns

1. doctor + exact catalog; monitor existing run to terminal, consume actual result.
2. Compare baseline, confirm only three authorized files changed and preserve earlier changes.
3. Correct real structure parsing and meaningful synthetic tests; require real document count29 and grid count80 with evidence and fail-closed bounds/ambiguity handling.
4. Run targeted then full pytest with AVANTAGE_INTEGRATION=0 and AVANTAGE_LIVE_WRITE_TEST=0. Previous full baseline: 175 passed, 37 skipped.
5. Continue same-window writeback investigation. Static parser completion is a stepping stone, not resolution of the main user request.

Read the current Harness skill before dispatch/monitoring. Native tools only. Do not access credential stores or print raw endpoint/log data.
