# 只读 in-process 上下文探针（开发文档）

`native/inprocess/ContextProbe.cs` 是一个**独立**的 .NET Framework C# COM 库，用来验证
Avantage 处理插件回调路线是否会把正确的执行上下文交到插件手里。它**只读**：不写模型、不写原始
数据、不碰文件/注册表/网络、不打开窗口、不启动进程。

**这份代码不等于写回功能已完成。** 它不实现拟合、不实现任何文档写入；成功的只读回调也不证明
后续原生写回安全。

## 为什么走这条路

跨进程 COM 指针不能当作本进程 `CProcessGridDoc` 的 C++ 对象地址使用（见
`docs/live-com-import-findings-20261020.md`）。厂商网格本身提供正常的处理插件回调，由它自己的
UI 派发。先做一个只读探针，确认这条路径是否真的提供可用上下文，再谈写入。

## 已确认的 ABI 证据

以下来自 Codex 对**本机已哈希校验**的 Avantage 6.9.0.59 二进制的静态分析，不是推测：

| 项 | 证据 |
|---|---|
| 插件创建 | `ProcessGrid.dll` `OnDeferredCreateProcessingPlugin` RVA `0x19c600`：`CLSIDFromString` 解析类 GUID，然后 `CoCreateInstance`（context 3，in-process），IID `IDataProcessControl`，再 QI `IDataProcess` |
| `IDataProcessControl` | IID `597365F0-21FB-11D2-B9D9-006097D69B02`，**6** 个 COM 槽 |
| `IDataProcess` | IID `3FB981F0-56AB-11D1-8AD5-006097D69B02`，**9** 个 COM 槽 |
| 上下文 QI | `IProcessingGrid`：`D0C45AA1-3DA2-11D2-93E9-006097C82421` |
| 槽位签名 | 来自 `ProcessingPlugInBase.dll` 导出的 `CMenuHelperBase` 接口 vtable 与修饰名 |

槽位含义（`PreserveSig`，返回 HRESULT32；前 3 槽为 IUnknown，不计入下表）：

`IDataProcessControl`

| 槽 | 方法 | self 之后的原生参数 |
|---|---|---|
| 3 | `ControlProcess` | `IProcessing*`, `IStorage*`, `LONG inputCount`, `IDataSpace** inputs`, `IDataAxes** axes`, `LONG* outputCount`, `IDataSpace*** outputs`, `enum32 flags` |
| 4 | `Reprocess` | `IProcessing*`, `IDataSpace*`, `IStream*` |
| 5 | `SetProcessingSchema` | `enum32 schema` |

`IDataProcess`

| 槽 | 方法 | self 之后的原生参数 |
|---|---|---|
| 3 | `QuerySummaryAttributes` | `IStorage*` |
| 4 | `QueryProcessAttributes` | `IStorage*`, `LONG inputCount`, `IDataSpace** inputs`, `IDataAxes** axes`, `LONG* outputCount` |
| 5 | `ExecuteProcess` | `IStorage*`, `LONG inputCount`, `IDataSpace** inputs`, `IDataAxes** axes`, `LONG outputCount`, `IDataSpace** outputs`（调用方数组，**不是**三重指针） |
| 6 | `PropertiesToString` | `BSTR*` |
| 7 | `SetProcessAttributes` | `IStorage*` |
| 8 | `ProcessControlComplete` | `LONG status` |

本仓库 `native/NativeSolver.cs` 里既有的 `IDataProcess` 声明（IID、6 个方法、
`out int outputs`、`IntPtr outData`）与上表一致，可作独立对照。

## 仍然是假设、尚未验证的部分

- `IDataProcess` 槽 5 的 `outputs` 在真实调用中是否允许为 NULL / 空数组——探针从不写它。
- `ControlProcess` 的 `flags` 具体位含义未验证；探针只把它原样记录进 JSON。
- `IProcessingGrid` 除了「QI 是否成功」以外的方法与语义完全未知；探针不调用任何方法。
- `schema` 的取值集合未知；探针只保存整数并回读。
- 回调所在线程是否为主窗口线程：探针**测量**它（`thread_matches_main_window`），不假设。
- NDR 元数据尚未由 Codex 交叉核对；**在核对前不要做真实激活**。

## 构建

```powershell
powershell -NoProfile -File scripts/build-inprocess-probe.ps1 -OutputPath <某个临时目录>\AvantageMcp.InProcessProbe.dll
```

- `-OutputPath` **必填**，且必须以 `.dll` 结尾；目录不存在会被创建。
- 目标为 `library` / `x64` / `.NET Framework`，编译器沿用仓库既有的 Roslyn 约定
  （`C:\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe`，回退到
  `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`）。
- 引用 `System.Web.Extensions.dll`（`JavaScriptSerializer`），不引用任何第三方或 Avantage 程序集。
- 脚本**不**注册程序集、**不**写注册表、**不**启动 Avantage、**不**改动
  `scripts/build-native.ps1` 的产物。它也不 glob `native/*.cs`，所以探针不会被编进
  `AvantageBridge.exe`。

## 只读行为契约

构造函数不做任何 I/O。所有回调都被 `try/catch` 包住，异常不会穿过 COM 边界。

`ControlProcess` 只做六件事：

1. **任何路径**都先置 `outputCount = 0`、`outputs = null`。
2. 要求当前进程名为 `Avantage`；要求 `processing`、`storage` 非空；要求
   `0 <= inputCount <= 32`。**不解引用** `inputs` / `axes` 数组（只记录指针是否为空）。
3. 打开调用方 `IStorage` 里的 `AvantageMcp.ProbeRequest` 流，用标准 `IStream` 读取**至多 64
   字节**，去掉首尾空白后要求完全等于 UTF-8 标记 `inspect-context-v1`。缺失或不匹配则返回
   `E_INVALIDARG` 且**不写入任何内容**——避免把诊断写进非预期的 storage 对象。
4. 对借用的 `IProcessing` 做一次 `IProcessingGrid` 的 QI，记录是否成功；**只释放自己 QI 得到
   的指针**，从不释放借用参数。
5. 采集：进程 id/名、当前线程 id、主窗口线程 id/进程 id（`GetWindowThreadProcessId`）、
   `thread_matches_main_window`、`grid_interface_available`、`schema`、`inputCount`。
   **不读取任何应用私有内存。**
6. 通过调用方 `IStorage` 的 `AvantageMcp.Context` 流写入有界 UTF-8 JSON（`CreateStream` 槽 3
   + 标准 `IStream` + `Commit`）。若宿主先提供了私有 summary storage，也写入同一份诊断。
   完成诊断后返回 `E_ABORT`，使已核实的宿主错误分支执行正常插件清理；JSON 中
   `probe_completed=true` 才代表诊断完成。此取消状态不是分峰失败，也不表示写回已实现。
   在标记有效之后若失败，返回失败 HRESULT，**不泄漏指针、凭据或堆栈**。

其余回调保持保守：

| 回调 | 行为 |
|---|---|
| `QueryProcessAttributes` | 报告 `outputCount = 0` |
| `QuerySummaryAttributes` | 非变更 `S_OK`，不持有或写入宿主私有 summary storage；结果由 COM33 复制到调用方存储 |
| `SetProcessAttributes` | 非变更 `S_OK` |
| `SetProcessingSchema` | 存进实例字段，`S_OK` |
| `ProcessControlComplete` | 非变更 `S_OK` |
| `ExecuteProcess` / `Reprocess` | `E_NOTIMPL`，不写任何文档或输出数据 |
| `PropertiesToString` | 返回正确分配的 BSTR 诊断标签 |

### 边界细节

- 标记流先 `Stat` 检查大小上限（64 字节）再分配，循环处理部分读取，未读满即视为无效。
- 输出 JSON 上限 4096 字节，超出则不写并返回 `E_FAIL`。
- `ComVisible(false)` 在程序集级；只有 `IDataProcessControl`、`IDataProcess`、`ContextProbe`
  三处标 `ComVisible(true)`。类为 `ClassInterface.None`，CLSID
  `96D0A0F0-56FC-4305-92B0-0BA9CB776AAE`。AAF 是已放弃的 summary 传输实验；
  已加载的程序集不可原位热替换，后续派生诊断使用不同程序集名和 CLSID。
- **不引用** `ProjectPersistence`、`Engine`、`NativeGridTables` 或任何私有宿主代码：那些会钉住
  独立的原生缓存，**绝不能**在应用进程内运行。
- 不注册 COM、不改注册表、不连接或启动 Avantage、不接触用户样品、不读取凭据。

## 输出 JSON 字段

```jsonc
{
  "probe": "inspect-context-v1",
  "grid_interface_available": true,
  "schema": 0,
  "input_count": 3,
  "flags": 0,
  "inputs_present": true,
  "axes_present": true,
  "process_id": 1234,
  "process_name": "Avantage",
  "main_window_present": true,
  "main_window_thread_id": 5678,
  "main_window_process_id": 1234,
  "thread_id": 5678,
  "thread_matches_main_window": true
}
```

失败且标记已校验时，写 `{"probe": "...", "probe_error": "<类型>: <最多200字符单行>"}`。

## 2026-10-20 实测结果与接口纠正

独立 Windows COM 内存存储/原生 CCW 调用检查共38项通过；完整离线回归204 passed、37 skipped。
在 PID1192 的同一个测试副本窗口连续两次回调，均取得 IProcessingGrid，执行线程23412
与主窗口线程一致。测试副本24个谱图的模型、原始数据与对象身份一致；磁盘文件未改。
另外六份已打开原文档的文档身份、路径、修改状态一致。未保存或编辑这些原文档。

调用返回 E_ABORT 触发 ProcessGrid.dll OnDeferredCallProcessingPlugin 的负 HRESULT 清理分支，
调用 DataProcessingComplete RVA0x198a60 释放 active control。连续第二次创建成功验证了该清理流程。

重要：COM32 和33与主 C++ 虚表顺序相反。实际包装函数及 NDR 方向已共同核实：

- COM32 → 主虚表0x4b0 → GetProcessingPluginSummaryAttributes(IStorage**)，取回摘要。
- COM33 → 主虚表0x4a8 → GetProcessingPluginCallAttributes(IStorage*)，复制到调用方存储并释放内部 call storage。

旧笔记曾把32/33标反，已纠正；不能按主虚表顺序推断 COM 槽位。
本轮仅临时注册自有 CLSID 到 HKCU，验证后已移除；不改供应商接口注册。
构建与协议依据：[IStorage](https://learn.microsoft.com/en-us/windows/win32/api/objidl/nn-objidl-istorage)、
[CreateStream](https://learn.microsoft.com/en-us/windows/win32/api/objidl/nf-objidl-istorage-createstream)、
[OpenStream](https://learn.microsoft.com/en-us/windows/win32/api/objidl/nf-objidl-istorage-openstream)。

## 后续（由 Codex 决定）

1. 编译、检查 COM 可见性与反射 ABI。
2. 在**不启动 Avantage**的前提下测试标记处理（合法 / 缺失 / 超长 / 非 UTF-8）。
3. 交叉核对 NDR 元数据，与上表逐项比对。
4. 只有在以上都通过后，才讨论单次测试窗口的注册/激活。

**即使只读回调成功，也不代表原生写回安全。** 将来的写入实现需要独立验证进程/模块/线程/上下文
身份，并自行实现回滚；本切片不包含任何写入实现。
