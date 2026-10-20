# 只读原生上下文身份诊断（NativeContextIdentity）

`native/inprocess/NativeContextIdentity.cs` 是处理插件探针的**后续**诊断切片，用来证明借到的
`IProcessing` 上下文确实是厂商网格对象，并且它对应的文档就是那一个固定的测试工程。

**它不是写回实现，也不代表同窗口新增/删除分峰可用。** 它只读身份与一个固定路径，返回布尔
结论；成功一次只说明“上下文身份对上了”。

## 与 ContextProbe 的关系

| | `ContextProbe` | `NativeContextIdentity` |
|---|---|---|
| 定位 | 基线隔离探针 | 显式 ABI 身份诊断 |
| 读取范围 | 不碰任何私有内存 | 在全部 vtable/线程/可读性守卫通过后，读取固定文档的路径 |
| 依赖 | 无私有宿主 | 无私有宿主（同样不引用 Engine/ProjectPersistence/NativeGridTables） |

`ContextProbe.cs` 在本切片中**保持不变**；本文件是它之外的独立新增。

## 唯一入口

```csharp
public static Dictionary<string, object> Inspect(IntPtr processing)
```

只由只读处理插件回调在 Avantage 进程内调用。**任何守卫失败都抛简短托管异常**
（`InvalidOperationException`，消息形如 `native-context-identity: <code>`），绝不返回部分成功。

允许的目标路径**只有一个**，大小写不敏感精确匹配：

```
E:\AvantageMCP\tests\live-window-repair\2.vgp
```

本切片**不支持**任意用户路径。需要别的文档时必须由监督者另行决定，而不是放宽这个常量。

## 守卫顺序（全部通过才返回）

1. `processing` 非空；当前进程名为 `Avantage`。
2. 取 `ProcessingManager.dll` 与 `ProcessGrid.dll` 的**已加载**模块基址
   （`GetModuleHandleW`，不加载新库、不 `GetProcAddress`），并要求各自的完整路径恰好位于
   `%ProgramFiles%\Thermo Scientific\Avantage\bin`。**模块名本身不是构建守卫**，所以比较的是
   目录全路径。
3. QI 借用的上下文到 `IProcessingGrid`（`D0C45AA1-3DA2-11D2-93E9-006097C82421`），
   **只释放这次 QI 得到的结果**。其 vtable 必须等于 `ProcessingManager.dll` 基址 + `0x35430`。
4. 网格视图 = 该接口地址 − `0x7e8`；其 primary vtable 必须等于 `ProcessGrid.dll` 基址 + `0x2862f8`。
5. `View+0x40` 是 HWND。`GetWindowThreadProcessId` 得到的进程必须是当前 PID、线程必须是当前
   线程；且当前线程还必须等于 `Process.MainWindowHandle` 的线程。**不做任何窗口激活或其它 UI 操作。**
6. `View+0xe8` 是 `CProcessGridDoc` 原生基址；其 primary vtable 必须等于 `ProcessGrid.dll` 基址
   + `0x282698`。
7. 本地文档接口 = 文档基址 + `0x778`；其 vtable 必须等于 `ProcessGrid.dll` 基址 + `0x2825a8`；
   再 QI `6E55B71A-944B-11D4-866C-00C04F68D1AC`，必须返回**同一个**本地接口指针（单继承同一性
   证据），**只释放 QI 结果**。
8. 以上全部通过后才调用文档 COM 槽 14 `HRESULT GetPathName(BSTR*)`，读取并 `FreeBSTR`，要求
   精确等于固定测试路径。

任何一步失败都立即抛出，不会继续用未知 vtable 调用任何方法。

## 每次直接内存读取前的检查

`RequireReadable` 在**每一次**直接内存读取之前执行，且失败即抛出：

- `VirtualQuery` 必须成功；
- 整段范围必须落在 `VirtualQuery` 报告的同一个区域内（跨区域即失败，不做拼接推断）；
- `State == MEM_COMMIT`；
- `Protect` 不含 `PAGE_GUARD`，且不是 `PAGE_NOACCESS`；
- 基础保护必须是可读的（`PAGE_READONLY` / `PAGE_READWRITE` / `PAGE_WRITECOPY` /
  `PAGE_EXECUTE_READ` / `PAGE_EXECUTE_READWRITE` / `PAGE_EXECUTE_WRITECOPY`）；
- 范围与区域边界的运算全部做溢出检查（`end <= start`、`regionEnd <= regionStart` 即失败）。

指针算术同样检查：模块基址与 RVA 必须为正、相加不得回绕；`+0x40`、`+0xe8`、`+0x778`、
`−0x7e8` 都经过显式的零/溢出检查。

`MEMORY_BASIC_INFORMATION` 按 x64 显式布局声明（`LayoutKind.Explicit`，48 字节），并在首次使用时
用 `Marshal.SizeOf` 校验；非 x64 进程直接失败，而不是猜布局。

BSTR 读取同样受限：先守卫 4 字节长度前缀，长度必须为正、偶数且不超过 32768 字节，再守卫
“字符数据 + 2 字节终止符”整段，最后才 `PtrToStringUni`。

## 返回字段（只有布尔与固定路径）

```jsonc
{
  "process_is_avantage": true,
  "processing_manager_module_verified": true,
  "process_grid_module_verified": true,
  "grid_interface_vtable_verified": true,
  "grid_view_vtable_verified": true,
  "view_window_process_verified": true,
  "view_window_thread_verified": true,
  "main_window_thread_verified": true,
  "document_vtable_verified": true,
  "document_interface_vtable_verified": true,
  "document_interface_identity_verified": true,
  "path_name_verified": true,
  "document_path_matches_target": true,
  "document_path": "E:\\AvantageMCP\\tests\\live-window-repair\\2.vgp",
  "all_guards_passed": true
}
```

不返回原始指针地址、不返回任何谱图或原始数据、不返回其它文档信息。

## 构建说明

该文件位于 `native/inprocess/`，只属于独立探针程序集；`scripts/build-native.ps1` 只 glob
`native/*.cs`，因此它**不会**进入 `AvantageBridge.exe`。它不引用任何 Avantage 程序集，也不需要
`VGDATASTORELib`。

## 未验证项与不确定性

- **未编译**：本切片没有运行任何 shell，`csc` 是否接受该文件未验证。
- **未激活**：未注册 COM、未连接 Avantage、未做任何真实回调。
- RVA 常量（`0x35430`、`0x2862f8`、`0x282698`、`0x2825a8`）与偏移（`0x7e8`、`0x40`、`0xe8`、
  `0x778`）**只对 Avantage 6.9.0.59 有效**。不同构建会失败，这是预期行为：本代码不做偏移扫描，
  也不尝试替换候选。
- 槽 14 的 `GetPathName` 语义来自 `native/ProcessingDocuments.cs` 中既有 Doc getter 约定与
  监督者静态分析；其 NDR 方向（`BSTR*` 为 out）在真实调用前未再独立核对。
- 文档未覆盖：多文档、多个网格视图、文档已保存/未保存状态、`GetPathName` 返回空串的情形
  （空串会因不等于固定路径而失败）。

## 明确不做的事

不改模型、不导出指针、不做原生变更、不打开/写原始文件、不读注册表、不联网、不启动进程或
窗口；不加载原生库、不 `GetProcAddress`；不提交、不推送、不外发消息、不读凭据、不做破坏性清理、
不改 `.git`。
