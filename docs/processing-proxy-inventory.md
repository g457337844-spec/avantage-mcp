# 处理接口代理/存根静态清单工具

`scripts/inspect_processing_proxy.py` 是一个**只读**静态检查工具：给定一个 PE32+ x64 DLL
和一个接口 IID，报告该映像里是否存在 MIDL 生成的对象接口存根表与代理表，以及每个方法槽位的
NDR 过程头描述。每条结论都附带证据 RVA。

## 用法

```powershell
python scripts/inspect_processing_proxy.py <dll路径>
python scripts/inspect_processing_proxy.py <dll路径> --iid 6E55B71A-944B-11D4-866C-00C04F68D1AC
python scripts/inspect_processing_proxy.py <dll路径> --output report.json
```

默认 IID 为 `ProcessingCom.DocId`（`6E55B71A-944B-11D4-866C-00C04F68D1AC`）。退出码 0 表示
文件已解析（结论可能是 `not_found`/`ambiguous`/`unsupported`），1 表示文件完全无法读取。

只依赖 Python 标准库，不需要 `pefile`。工具**不会** `LoadLibrary`、不会 `ctypes` 调用、
不会解引用文件之外的地址，也不执行任何代码；所有读取都经过文件边界检查。

## 读取的结构（x64 / `/Oicf` 对象接口）

这些是 MIDL 为**对象接口**生成的结构，不是旧的 OLE 代理布局：

```text
CInterfaceStubVtbl
  +0   const IID *piid
  +8   MIDL_SERVER_INFO *pServerInfo
  +16  unsigned int DispatchTableCount   (u32 + 4 字节填充)
  +24  RPC_DISPATCH_FUNCTION *DispatchTable   （纯解释型接口可为 NULL）
  +32  LONG_PTR Reserved

CInterfaceProxyVtbl
  +0   MIDL_STUBLESS_PROXY_INFO *pProxyInfo
  +8   const IID *piid
  +16  内联分派槽位（前三个是 IUnknown）

MIDL_SERVER_INFO          pStubDesc, DispatchTable, ProcString, FmtStringOffset,
                          ThunkTable, pTransferSyntax, nCount, pSyntaxInfo
                          （x64: +0, +8, +16, +24, +32, +40, +48, +56）

MIDL_STUBLESS_PROXY_INFO  pStubDesc, ProcFormatString, FormatStringOffset,
                          pTransferSyntax, nCount, pSyntaxInfo
                          （x64: +0, +8, +16, +24, +32, +40）
```

**槽位数只来自存根表的 `DispatchTableCount`**（`piid` 之后 16 字节处），不预设 19、80 或任何
常数。

**两个结构的 `nCount` 都是“传输语法数量”，不是方法数。** 它表示接口为几种传输语法
（例如 NDR32 + NDR64）生成编组代码，典型值为 1 或 2；真实文件的服务器信息里就是 2，而方法数
是 29 或 80。因此工具把该字段单独报告为 `transfer_syntax_count`，**绝不**拿它和
`DispatchTableCount` 比较，也不用它裁剪槽位循环。

**`MIDL_STUBLESS_PROXY_INFO` 在 +40 是 `pSyntaxInfo` 指针**，不是计数；把那里当 `nCount`
会读出一个任意 64 位值。计数只在 +32 读取。

**纯解释型对象接口允许 `DispatchTable` 为 NULL。** MIDL 对这类接口仍会写出计数和格式串，只是
没有本地 RPC 分派表。工具把 NULL 视为有效证据（`dispatch_table_present: false`），绝不把零
地址当作可解引用地址；只有当表非 NULL 时才做边界检查。

解析流程：在文件里找 IID 的小端 GUID → 找指向该 GUID 的 8 字节对齐 VA 引用 → 每个引用分别按
“存根 `piid` 字段”与“代理 `piid` 字段”解释（两者头部起点不同，因此同一个引用不会同时成立）。
服务器信息与代理信息通过**共同的 `MIDL_STUB_DESC` 指针**关联；不一致时报 `mismatch`，不强行
拼成一对。

## NDR 过程头

对每个方法槽位（`slot >= 3`，前三槽为 IUnknown，没有格式串）：

```text
+0  u8  handle_type
+1  u8  oi_flags
+2  u16 rpc_flags
+6  u16 opnum
+8  u16 stack_size
+14 u8  oi2_flags
+15 u8  parameter count
+16 extension length（仅当 oi2_flags & 0x40）
随后每个参数描述符 6 字节
```

格式串偏移表按下标寻址：槽位 `s` 的偏移取 `FmtStringOffset + s*2`（该表对 0..2 槽无有效项，
工具直接跳过而不是读一个假值）。

`stack_size` 在 **+8**（不是 +4）。`parameter_count` **只在**其后的描述符区域确实存在于文件中
时才输出；否则该字段保持 `null`，并给出 `parameter_count_status` 说明原因。工具不会用不可靠
的字节凑一个数。

## 输出结构

```jsonc
{
  "status": "ok | partial | not_found | ambiguous | unsupported | truncated | unreadable | invalid_iid",
  "file": {"path": "...", "size": 123456, "sha256": "..."},
  "pe": {"machine": "AMD64", "magic": "PE32+", "image_base": "0x...", "sections": [...]},
  "iid": {"text": "...", "bytes_le": "...", "occurrences": [...], "references": [...]},
  "stub": {"status":..., "evidence_rva":..., "dispatch_table_count":...,
           "dispatch_table_present": true, "dispatch_entries_total":..., "candidates": [...]},
  "proxy": {"status":..., "evidence_rva":..., "proxy_vtbl_rva":...,
            "transfer_syntax_count":..., "iunknown_slots": [...]},
  "server_info": {"status":..., "evidence_rva":..., "transfer_syntax_count":..., "proc_string_rva":...},
  "proxy_info": {"status":..., "evidence_rva":..., "transfer_syntax_count":...},
  "association": {"status": "ok | partial | mismatch", "detail": "..."},
  "ndr": {"status":..., "slot_count":..., "detail":...},
  "slots": [{"slot": 3, "kind": "method", "opnum": 0, "stack_size": 35,
             "parameter_count": 1, "parameter_count_status": "decoded",
             "raw_header_hex": "...", "descriptor_bytes_hex": "..."}],
  "limitations": ["..."]
}
```

`partial` 表示存根、服务器信息与 NDR 头都成立（方法数可信），但代理表未能独立确认。

## 已实现的严格性

- **虚拟零填充不当数据**：节的 `VirtualSize` 大于 `SizeOfRawData` 时，落在文件之外的 RVA 一律
  拒绝（`out_of_bounds`），不会读成零。
- **边界检查**：DOS/PE 头、节表、可选头、每个被引用的结构都做文件范围检查；节原始数据超出
  文件长度直接拒绝。
- **截断/畸形**：短文件、错误的 `e_lfanew`、非 PE32+、非 AMD64、越界节表都明确报错。
- **歧义不猜**：多个候选同时成立时返回 `ambiguous` 且 `evidence_rva` 为 `null`。
- **同 IID 的存根与代理必须区分**：代理候选的前三个内联槽必须是**文件支持的可执行代码地址**。
  否则把存根头当代理读时，那三项会是 `pServerInfo`、`DispatchTableCount`、`0`，都不是代码
  地址，必须被拒绝，而不是制造第二个“有效”代理。
- **格式偏移表必须文件支持**：偏移表按 `2 * DispatchTableCount` 字节做边界检查；只存在于
  虚拟区（加载时补零）的表不算证据。
- **不支持的 NDR 明确报错**：格式串偏移解析不到文件内、传输语法计数不合理等，返回
  `unsupported`/`ambiguous`，不填猜测值。
- **只解码有把握的字段**：过程头只解上述字节，并保留 16 字节原始头 hex。

## 结论边界（重要）

- 本工具只给**ABI 静态描述**：槽号是 opnum，不是方法名。工具从不推断方法名。
- 发现某个槽位**不证明**可以从另一个进程安全调用它，也不证明参数语义、所有权或线程模型。
- **C++ 对象 vtable 与 COM 接口分派表不是一回事。** C++ 虚函数、COM 方法、跨进程代理是三个
  不同概念；本工具只描述 MIDL 对象接口表，绝不把 C++ 对象的内存布局当作 COM 表。
- 反过来也成立：C++ 名字修饰里的“非虚”**不能**证明某个方法未通过 COM 暴露——手写的 COM
  包装层完全可以把非虚方法接到某个槽位上。因此本工具不使用修饰名推断 COM 暴露情况。
- 未覆盖：参数类型语义、双接口（dual interface）导致的方法槽位偏移、跨 DLL 间接引用、
  导入表/导出表映射、重定位表。

## 与分峰写回的关系

**本工具不实现、也没有验证“同窗口新增/删除分峰”。** 它只回答“这个 DLL 里有没有可静态确认
的接口槽位描述”这一前置问题。真正的结构修改仍需另行证明对象所有权、逐参数一致性、插入失败
回滚和实际窗口保存往返。

## 测试

`tests/test_processing_proxy_inventory.py` 用**纯合成**的最小 PE32+ fixture 验证上述行为：
正常存根/代理关联与槽位数、**NULL dispatch table 的纯解释型接口**、`nCount` 为 2 而槽位数为 6
（证明两者互不混淆）、**存根头不会被误当成第二个有效代理**（三个 IUnknown 槽指向数据时必须
拒绝）、不同 IID 不误配、截断与畸形偏移、虚拟区非文件数据、重复候选歧义、`MIDL_STUB_DESC`
不匹配、NDR 偏移越界、参数描述符不可读时计数保持 `null`。fixture 全部按字节在测试内构造，
不含任何厂商二进制或用户数据，也不硬编码安装 DLL 的地址或 29/80 数量。

测试按路径导入工具时会先把模块注册进 `sys.modules`——`dataclasses` 在模块体执行期间要通过
`sys.modules[cls.__module__]` 解析注解，未注册会在收集阶段抛 `AttributeError`。

```powershell
python -m pytest tests/test_processing_proxy_inventory.py -q
```

## 已知限制

- 结构偏移按 x64 固定布局硬编码；其他 MIDL 变体需要扩展。
- 只按 8 字节对齐扫描 VA 引用；未对齐的指针引用会漏检。
- 未解析重定位表，因此不能区分“该 VA 是运行时地址”还是恰好相等的常量。
- 双接口会改变方法槽位起点，本工具未检测。
