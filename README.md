# Avantage MCP

用于 Windows 上 Thermo Scientific Avantage 的本地 MCP，覆盖 VGD/VGP 读取、背景、峰参数与约束、原生分峰和完整项目保存。当前版本 **1.3.0**，适配 **Avantage 6.9.0.59、Windows x64**。

当前提供 43 个工具。已经打开的 VGP 可以在原窗口直接调整已有峰参数和现有约束、执行原生拟合，默认完整备份后保存到原文件，无需关闭和重新打开。支持两个项目间选择目标；保存后核对所有谱区原始数据、模型和网格关联，并使旧关联失效。不会自动打开检查点或结果副本。自动打开/关闭文档继续禁用；改变峰数量、背景或约束属性集合仍使用私有项目文件流程。

拟合直接调用 Avantage `PeakFitting.dll` 原生 simplex 引擎；背景、峰形和文件读写也使用厂商原生组件。每次拟合使用独立子进程，避免 MFC 拟合状态释放后再次运行崩溃。失败保留当前会话和模型；成功核对峰编号及谱图指纹，再替换参数。不依赖鼠标操作。

## 使用

仓库内的 Codex 技能入口是 [skills/avantage-mcp/SKILL.md](skills/avantage-mcp/SKILL.md)，用于指导已连接 MCP 的谱图处理和分峰流程。它随源码保存，安装脚本不会把技能安装到个人技能目录。

安装后在 Codex MCP 设置中启用 `avantage`，按客户端提示刷新连接或重新打开会话。可以直接用自然语言，例如：

> 打开 E:\数据\样品\C1s.VGD，读取数据。设置 Shirley 背景，按我提供的峰位、峰宽和关联约束运行原生分峰。检查收敛情况，分别另存 VGD、VGT、分峰曲线 CSV 和模型参数 JSON。

推荐顺序：`status` → `open_spectrum` → 可选 `charge_shift` → `set_background` → `add_peak` → `set_peak_constraint` → `run_fit` → `get_model_curve` → 保存结果。后续工具使用打开谱图返回的 `session_id`。

读取已有 VGP 项目时，先 `open_processing_project(path)`，再 `list_project_spectra(project_id)`，从返回列表选择 `spectrum_index`，用 `open_project_spectrum(project_id, spectrum_index)` 获取可编辑的 `session_id`。已有峰、背景和约束会从原生项目载入；先读取模型，再按用户要求修改，避免无意清除原有设置。VGP 与同名 `.DATA` 目录须位于同一目录；MCP 会一起复制并校验。

日常调整已经打开的谱图：`list_processing_documents` → `list_document_spectra` → `attach_document_spectrum` → 修改参数/约束或 `run_fit` → `apply_fit_to_document(session_id)`。默认 `save_to_original=true`，完整备份后在原窗口执行普通保存，无需关闭或重开文档。完成后重新关联再做下一轮修改；旧关联会被标为过期。`save_to_original=false` 仅更新当前窗口。

新增/删除峰、替换背景或更改约束属性集合等结构修改，仍使用私有项目流程：`save_project_model(session_id)` 省略 `path`，备份并写回原文件。这个文件替换流程要求目标文档关闭；它与当前窗口普通保存是不同的路径。显式提供新 `path` 才会生成用户要求的副本，且不会自动打开窗口。

实时连接使用随 Avantage 应用生命周期保留的辅助进程，避免每个 MCP 工作进程重复激活厂商工厂。正常退出 Avantage 时辅助进程自动退出。多个 MCP 进程的实时读取、项目快照和修改/保存事务互斥；冲突返回 `LIVE_OPERATION_BUSY`，不要自动重试写操作。启动通知或模态窗口未关闭时返回 `APP_MODAL_OR_STARTING`。需要禁用实时功能时设置 `AVANTAGE_DISABLE_LIVE=1`；私有文件处理不受影响。

| 文件 | 内容和用途 |
| --- | --- |
| `.VGP` + 同名 `.DATA` | 完整项目、网格、谱图、峰、背景与约束；推荐用于 Avantage 继续处理 |
| `.VGD` | 原生谱图和校准后的能量轴；不包含本 MCP 独立峰模型 |
| `.VGT` | Avantage 原生峰表，含峰、背景及约束；独立原生加载验证后发布 |
| 模型 `.json` | MCP 模型参数和谱图指纹，支持恢复及关联峰编号重映射 |
| 曲线 `.csv` / `.json` | 数据、背景、各分峰、总模型和残差 |

原文件写回将暂存、完整备份和 `journal.json` 集中放在样品目录外、同一磁盘的 `<原文件磁盘>:\AvantageMCP\recovery\<目录路径摘要>\` 中，样品目录不再堆积测试文件夹。返回 `backup_path`、`journal_path`、`published_bytes_verified=true`、`application_windows_opened=0`。只有完整原生回读验证通过才发布。检测到并发修改或占用时停止；失败不得自动重试。VGP 和 DATA 成对回滚，断电后根据日志恢复，不承诺跨文件原子性。不要删除原项目的 `.DATA` 目录。

独立 VGD/VGP 会话与当前可见网格不会自动同步。在 Avantage 中继续使用独立 VGD 结果时，打开匹配的 VGD，并用软件的峰表加载功能载入 VGT。`show_data_file` 仅发送 VGD 打开请求；`display_verified=false` 表示未核实可见网格。完整 VGP 模型另存和实时文档写回分别受独立的原生验证能力保护。

`fit_executed=true` 表示当前会话的模型执行过原生拟合。还应检查 `converged`、`stop_reason`、迭代次数和残差。达到迭代上限返回当前参数，但 `converged=false`。修改峰或约束会清除旧拟合报告。导入 JSON/VGT 只恢复参数，返回 `fit_executed=false`；VGT 没有可核实的优化执行报告。

## 安装和连接

需要正常安装并注册 COM 组件的 Avantage、Windows x64、Python 3.10+ x64 和 .NET Framework。包含 C# 源码及本机编译的桥接 EXE；不包含厂商 DLL/许可证，不修改软件安装文件。

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -RegisterCodex
```

指定运行环境：

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Python 'C:\Python312\python.exe' -AvantageHome 'C:\Program Files\Thermo Scientific\Avantage' -RegisterCodex
```

安装脚本构建桥接程序、创建项目内 `.venv`、安装锁定依赖并生成 `mcp-config.json`。`-RegisterCodex` 读取现有服务后，仅追加本服务的 TOML 配置，保留其他配置原始字节并创建备份；此服务的工具等待时间至少为 180 秒。同名其他配置会保留并报错，可使用 `-ServerName avantage-local`。采用定向编辑，避免部分 Codex CLI 版本添加 MCP 时重写其他服务配置的问题。

省略 `-RegisterCodex` 时只安装并生成配置。其他 stdio MCP 客户端可使用 `mcp-config.json` 的启动路径、参数和环境变量。`launch.cmd` 支持空格和中文路径。移动目录后重新安装并更新客户端启动路径。

配置参见 [OpenAI 官方 MCP 文档](https://learn.chatgpt.com/docs/extend/mcp?surface=cli)。服务采用 [官方 MCP Python SDK](https://github.com/modelcontextprotocol/python-sdk)，锁定并实测 `mcp==1.30.0`。

## 工具

| 工具 | 功能 |
| --- | --- |
| `avantage_status` | 版本、原生接口可用性和应用状态 |
| `avantage_application_status` | 当前应用/实验状态 |
| `avantage_list_sample_files` | 本机自带样例 |
| `avantage_open_spectrum` | 打开 VGD 工作副本 |
| `avantage_open_processing_project` | 校验并复制 VGP 与同名 `.DATA`，列出谱图 |
| `avantage_list_project_spectra` | 读取私有项目的谱图及已有峰数量 |
| `avantage_open_project_spectrum` | 打开项目内指定谱图，载入已有峰、背景和约束 |
| `avantage_close_processing_project` | 释放项目；独立打开的谱图会话保留 |
| `avantage_save_project_model` | 默认备份并保存到原 VGP；显式 path 才另存副本 |
| `avantage_list_processing_documents` | 列出实时文档；需原生验证状态启用 |
| `avantage_list_document_spectra` | 列出实时文档的谱图；需原生验证状态启用 |
| `avantage_open_processing_document` | 已停用：请手动打开原文件，避免应用文档生命周期故障 |
| `avantage_attach_document_spectrum` | 将实时谱图快照为带文档关联的会话；需原生验证状态启用 |
| `avantage_apply_fit_to_document` | 原窗口更新已有峰参数/约束，默认完整备份并保存原文件 |
| `avantage_save_processing_document` | 普通保存当前原文件，强制完整备份与回读验证 |
| `avantage_save_processing_document_in_place` | 同上；保留原窗口，禁止另存为 |
| `avantage_list_sessions` | 已打开会话 |
| `avantage_close_session` | 释放会话 |
| `avantage_get_spectrum_info` | 轴、点数及来源 |
| `avantage_get_spectrum_data` | 分页读取动能、结合能、强度 |
| `avantage_charge_shift` | 有符号结合能校准 |
| `avantage_set_background` | Linear、Shirley、Smart、Tougaard、Simple 背景 |
| `avantage_get_background` | 背景参数和范围 |
| `avantage_clear_background` | 清除背景；先清除依赖的峰模型 |
| `avantage_get_background_data` | 背景和扣背景数据 |
| `avantage_add_peak` | 创建带明确初值的原生峰 |
| `avantage_update_peak` | 修改峰字段 |
| `avantage_remove_peak` | 删除无约束引用的峰 |
| `avantage_get_fit_model` | 参数、约束和拟合报告 |
| `avantage_clear_fit_model` | 清除峰模型，保留背景 |
| `avantage_set_peak_constraint` | 固定、范围、当前值偏移、关联及关联范围 |
| `avantage_remove_peak_constraint` | 删除指定约束 |
| `avantage_run_fit` | 原生自动优化；峰编号保持一致 |
| `avantage_get_model_curve` | 分峰、总模型、残差及 RSS/RMSE |
| `avantage_export_peak_table` | 保存并独立重载验证 VGT |
| `avantage_inspect_peak_table` | 查看 VGT，不替换模型 |
| `avantage_import_peak_table` | 原子导入原生峰、背景及约束 |
| `avantage_export_fit_model` | 保存 MCP JSON 参数 |
| `avantage_import_fit_model` | 校验指纹并恢复 JSON 模型 |
| `avantage_export_model_curves` | 保存曲线 CSV/JSON |
| `avantage_save_spectrum` | 另存原生 VGD |
| `avantage_export_spectrum` | 谱图 CSV/JSON |
| `avantage_show_data_file` | 请求软件打开已保存 VGD |

## 参数与边界

路径使用 Windows 绝对路径，支持中文。默认能量尺度为结合能 eV，由厂商函数换算动能。`charge_shift` 的 `delta_ev=1` 表示结合能增加 1 eV、动能减少 1 eV；校准应在背景和峰模型之前完成。

背景范围端点使用递增结合能，必须在谱图内，并为端点平均保留足够点数。Shirley 支持迭代、收敛和平滑参数；Tougaard 支持 BC/BCD、B/C/D、Y 偏移及能量修正。已有峰模型可直接更换背景，峰编号、参数、约束及原有化学/RSF 关联会保留；失败回滚。更换背景会清除旧拟合报告。范围外背景、总模型和残差为 `null`，CSV 中留空，不应理解为零。RSS/RMSE 覆盖完整有效范围，与分页无关。

新增峰提供 `label`、`centre_ev`、`height`、`fwhm_ev`。`gaussian_lorentz` 为 0–1，G/L 组合可选 `product`、`sum`、`convolution`；一次原生拟合的所有峰须用同一组合方式。默认 `asymmetric=false`，使用非对称拖尾时，在拟合及后续曲线接口中一致设置 `asymmetric=true`。

范围约束用 `value1/value2` 表示上下限。关联约束提供 `reference_peak_id` 及运算符。结合能峰位关联支持加减；乘除需明确选择动能。循环关联会拒绝；删除被引用峰前先解除关联。

`application.connected` 是旧采集自动化接口的遥测结果；`false` 不代表处理文档接口不可用。请用 `native_live_document_bridge_verified` 和 `list_processing_documents` 核对实时处理连接。

`run_fit` 默认最多 1000 次迭代、原生收敛值 `0.0001`、独立拟合超时 90 秒。原生目标函数值由厂商算法返回；另外计算 RSS/RMSE。收敛不能直接证明化学模型正确。

JSON 模型导入默认校验能量轴、强度及能量转换元数据指纹。仅在有意复用其他谱图参数时设置 `allow_different_spectrum=true`。VGT 无此指纹，需选择匹配谱图；导入检查背景边界及参数，失败保留原模型。

当前私有会话处理流程针对**一维、等间隔 XPS 能量谱图**，最多 32 个会话、8 个 VGP 项目、每模型 100 个峰、每页 10000 点，曲线/拟合最多两百万个分峰数据点。每个会话使用一个公共背景；含不同逐峰背景或跨谱图关联的项目模型明确拒绝独立编辑。

部分原生接口依赖二进制布局，仅对已验证 SHA-256 启用：

| DLL | SHA-256 |
| --- | --- |
| `PeakFitting.dll` | `AADDFE7A956FC4EBADB08E74934035F71D3E6AC9818F8A04CBE9B8C01F8DC311` |
| `ProcessGrid.dll` | `1CC8A83B0677853BFB7A28165146CA1F0349D3E19B62D9810B337A90072D3ED9` |
| `DataCache.ocx` | `812C32A4947B2A9DFFC91CCAAAFF61C80FD7379D79706EA3B95D0520B788A00D` |
| `Peaks.dll` | `D5FD7ACFF7C49FBCDCE24DBD34EAF5A7294C434E5B6F5AA1AB031983E1F583B9` |

Avantage 更新导致哈希变化时，依赖相应布局的拟合、VGT、VGP 及实时接口停止使用，需重新适配验证。此 MCP 不操作采集硬件，也不提供多维谱图分峰或定量灵敏度因子计算。这是本地非官方适配。实时接口还校验 Avantage、处理代理和 MFC 的精确构建，见 `native/VerifiedBuild.cs`。

完整 VGP 模型保存和实时写回要求原始强度与校准轴未变；能量校准目前通过独立 VGD 流程交付。原生 VGP/VGT 不保存显示用的 `area_ratio`、`height_ratio`，重读值可能为 1；持久化验证只忽略这两项显示比例，仍核对所有实际峰参数和约束。厂商峰表未持久化的原子质量报告为未知，不能据此计算定量结果。

## 文件保护和恢复

打开时创建私有副本并记录源文件 SHA-256。保存/导出保护所有会话的源文件、工作文件及硬链接/路径别名。已有目标默认不覆盖；明确 `overwrite=true` 才覆盖普通结果文件。写入经临时文件发布。

保存返回 `source_unchanged=true/false`。源文件移动、删除或不可读时仍可保存副本，返回 `source_unchanged=null` 和 `source_check_error`，表示无法核实。

独立拟合进程退出时由 Windows 回收原生状态，临时文件随后清理；父桥接退出时子进程也停止。拟合失败/超时保留模型。同一文档保存或写回后，其他关联会话会标为 `stale=true`，继续修改需重新关联。桥接崩溃、协议错误或桥接超时使会话、项目和文档代理编号失效：重新打开项目与谱图，重新列出应用文档并关联谱图。故障响应分别列出 `invalidated_sessions`、`invalidated_projects` 和 `invalidated_documents`。仅结束本服务进程，不结束用户的 Avantage 应用。不自动重试写操作；响应丢失时先核对目标文件和应用状态。

工作副本可能保留在 `%LOCALAPPDATA%\AvantageMCP\sessions`，交付结果应显式另存。关闭会话后编号失效。

| 环境变量 | 用途 |
| --- | --- |
| `AVANTAGE_HOME` | 软件安装根目录 |
| `AVANTAGE_BRIDGE_PATH` | 桥接 EXE 路径 |
| `AVANTAGE_TIMEOUT_SECONDS` | 桥接时限，默认 120 秒 |
| `AVANTAGE_WORK_DIR` | 私有会话目录 |
| `AVANTAGE_PYTHON` | 安装用 Python |

保持客户端时限大于桥接时限，桥接时限大于独立拟合时限；较大数据需要提高时，应一起调整。

## 验证与示例

```powershell
$env:AVANTAGE_INTEGRATION = '1'
.\.venv\Scripts\python.exe -m pytest tests -q
```

真实测试覆盖源文件保护、中文路径、保存重开、能量校准、五种背景、峰与约束、对称/非对称连续拟合、同一会话重复拟合、迭代上限、限定范围、全部参数固定、失败保留模型、VGT/JSON 重载及 IPC 故障恢复。未设置环境变量时跳过依赖本机 Avantage 的测试。

当前实际 MCP 客户端发现 43 个工具。1.3.0 验证覆盖连续两轮默认实时应用并保存、独立进程回读、正常关闭重开，以及两个已打开项目之间切换并分别保存、另一个项目保持不变。旧内部 SaveDocument 路径不再使用；保存后释放并重新取得谱图对象，避免复用失效的对象列表。仓库不含用户谱图和拟合结果。

文件测试另设 `$env:AVANTAGE_VGP_FIXTURE` 为带同名 `.DATA` 的完整项目。实时保护测试还需设置 `$env:AVANTAGE_LIVE_INTEGRATION='1'`，不会自动打开或关闭应用文档。原窗口写入测试另需显式指定 `AVANTAGE_LIVE_WRITE_TEST=1`、`AVANTAGE_LIVE_WRITE_FIXTURE`，以及同名 `.mcp-test.json` 标记；该副本必须已打开，测试只修改并恢复峰标签。标记格式见验证文档。验证详情见 [docs/validation.md](docs/validation.md)。可复现原生拟合示例：

```powershell
.\.venv\Scripts\python.exe .\examples\native_fit_workflow.py --output-dir 'C:\data\avantage-demo'
```

示例使用 Avantage 自带 PET1407 C1s 谱图，峰标签仅用于数值流程演示，不作化学归属结论。

`src/avantage_mcp` 为 MCP 服务，`native` 为 C# 桥接，`scripts/build-native.ps1` 为构建入口，`tests` 为测试。官方 SDK 使用 stdio；原生桥接为 x64 .NET Framework STA 进程，日志走标准错误。
