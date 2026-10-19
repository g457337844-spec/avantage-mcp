---
name: avantage-mcp
description: 使用本仓库的 Avantage MCP 读取 VGD 谱图与 VGP 项目，修改已有峰参数和约束，执行原生分峰并保存结果。实时文档操作须以原生验证能力为准。用户要求用 Thermo Scientific Avantage 分峰、读取谱图或恢复峰表时使用。
---

# Avantage XPS 处理

1.3.0 已验证当前窗口的已有峰参数修改与原路径普通保存。必须核对能力字段和逐项验证结果；背景、峰数量及约束属性集合的结构修改仍走关闭文档后的私有文件流程。

调用已连接 Avantage MCP 的 `avantage_*` 工具；客户端可能为工具名加上服务前缀。处理参数、化学归属及交付路径以用户要求为准。工具参数和环境设置见 [仓库 README](../../README.md)，真实验证范围见 [验证摘要](../../docs/validation.md)。

## 连接和谱图

- 先调用 `avantage_status`，核对安装版本、原生拟合可用性及工具能力。服务未连接时检查已有连接；需要新增安装或修改配置时，按用户授权范围执行 README 的安装步骤。
- 用 `avantage_open_spectrum` 打开绝对 Windows 路径的 `.VGD`，保存返回的 `session_id`。该会话处理私有副本，与 Avantage 当前可见网格独立。
- 对 `.VGP` 用 `avantage_open_processing_project`；同名 `.DATA` 目录须相邻。保存 `project_id`，用 `avantage_list_project_spectra` 读取列表，再用列表返回的 `spectrum_index` 调用 `avantage_open_project_spectrum`。保存其 `session_id`；已有原生峰、背景和约束会载入。先读取已有模型再进行用户指定的局部修改。项目源文件和数据目录受保护。
- 用 `avantage_get_spectrum_info`、`avantage_get_spectrum_data` 查看能量轴、强度和谱图范围，再确定背景及峰初值。不要把样例参数或数值峰标签解释为用户样品的化学归属。缺少会影响结果的模型信息时，先读取数据，再询问所需峰组或约束。

## 设置和拟合

1. 需要电荷校准时，先调用 `avantage_charge_shift`，再定义背景和峰。正 `delta_ev` 增加结合能、降低动能；使用厂商提供的能量换算，不自行用光子能量相减替代。
2. 调用 `avantage_set_background` 设置 Linear、Shirley、Smart、Tougaard 或 Simple 背景。结合能端点须递增并在谱图内。已有峰模型可直接更换背景，保留现有峰、参数、约束和元数据关联；操作会清除旧拟合报告。
3. 用 `avantage_add_peak` 提供明确的 `label`、`centre_ev`、`height` 和 `fwhm_ev`，按需用 `avantage_update_peak` 修改。G/L 比为 0–1；一次拟合的峰使用相同的 product、sum 或 convolution 组合方式。
4. 用 `avantage_set_peak_constraint` 配置用户所需固定、范围或关联约束。范围用 `value1/value2`，关联用 `reference_peak_id` 和运算符。结合能峰位关联只支持加减；删除被引用的峰前解除关联。
5. 调用 `avantage_run_fit` 执行 Avantage 原生优化。对称峰默认 `asymmetric=false`；启用非对称拖尾时，在拟合、读取曲线和导出曲线中一致设置 `asymmetric=true`。保持客户端时限大于桥接时限，桥接时限大于单次拟合时限。

拟合后用 `avantage_get_fit_model` 和 `avantage_get_model_curve` 核对峰参数、约束、`fit_executed`、`converged`、`stop_reason`、迭代次数及 RSS/RMSE。达到迭代上限不算收敛；数值收敛也不证明化学模型正确。范围外背景、总模型和残差为 `null`，不可当作零。报告所用模型与参数，以及尚未支持的化学解释。

## 实时文档与原文件保存

先核对 `native_document_lifecycle_verified=true`。1.3.0 支持原窗口修改已有峰参数及现有约束的数值、类型和链接，背景、峰数量和约束属性集合须保持不变。用 `list_processing_documents`、`list_document_spectra` 准确选择文档和谱区，再 `attach_document_spectrum`；修改或原生拟合并核对曲线后调用 `apply_fit_to_document(session_id)`。默认完整备份并普通保存到原文件，保留原窗口；保存后重新关联。工具会激活已有目标窗口，不创建结果窗口。禁止自动关闭用户窗口或通过检查点代替写回。

用户默认要求结果保存在原文件。日常参数调整使用上一段实时流程，无需关闭或重开文档。若必须增加/删除峰、替换背景或改变约束属性集合，则使用 `open_processing_project(original)` → `open_project_spectrum` → 私有修改/拟合并核对曲线 → `save_project_model(session_id)` **省略 path**。仅此文件替换流程要求目标文档关闭；用户未保存内容须先保留，不得自行丢弃。

默认原路径保存将完整备份 VGP 和 DATA，核对 `roundtrip_verified`、`raw_data_verified`、`unrelated_spectra_verified`、`grid_references_verified`、`original_backup_verified`、`published_bytes_verified` 和 `application_windows_opened=0`，记录 `backup_path` 与 `journal_path`。完成后关闭私有会话和私有项目；下一次从最新原文件重新读取。只有用户明确要副本时，才给 `save_project_model` 提供新的 path。不要自动打开任何内部暂存/检查点。用户需要查看结果时只打开对应原文件，并复用已打开的同路径窗口。

暂存与备份集中在样品目录外、同磁盘的 `<原文件磁盘>:\AvantageMCP\recovery\<目录路径摘要>\`，禁止再在样品目录创建成堆的 `.avantage-*` 文件夹。不要把恢复目录改到其他磁盘，否则会破坏事务改名的前提。清理历史文件时保留完整恢复备份，不得删除原始 `.DATA` 目录。

内部自动打开文档接口继续停用（`LIVE_OPEN_NOT_VERIFIED`），不要绕过保护。实时保存后原窗口一直保留，不要求用户重开。结构修改走关闭后的文件替换流程时，才需要正常打开原文件。不得使用旧的清空路径 SaveDocument/强制关闭/重载原型。

VGD 数据另存用 `save_spectrum`；峰表用 `export_peak_table`；模型和曲线用 `export_fit_model`、`export_model_curves`。这些导出不等于更新当前可见网格。不能把导入参数、成功传输或数值收敛作为化学归属成立的证据。

恢复 VGT 时先打开匹配的 VGD，可用 `avantage_inspect_peak_table` 查看，再用 `avantage_import_peak_table` 替换模型。JSON 用 `avantage_import_fit_model`，默认要求指纹一致；仅在用户有意复用其他谱图参数时允许不同谱图。导入只恢复参数，不执行优化，`fit_executed=false`。若要在 Avantage 界面继续处理，说明需打开匹配谱图并加载峰表；`avantage_show_data_file` 只发送 VGD 打开请求，不能证明当前网格已显示或同步。

## 可用范围与故障

已验证 Windows x64、Avantage 6.9.0.59 的一维等间隔能量谱图，一个公共背景，最多 100 个峰。VGP 支持私有项目编辑、完整文件写回和已有峰参数的实时修改/普通保存。完整项目模型保存与实时写回要求原始数据和校准轴未变；电荷校准通过独立 VGD 流程保存。显示比例 `area_ratio`、`height_ratio` 不在厂商文件中持久化；原子质量可能未知。多背景和跨谱图关联模型可能拒绝独立编辑。依赖布局的接口按 DLL 哈希启用；出现不支持构建时停止相应操作并说明需重新适配，不绕过校验或改用其他优化器冒充原生拟合。不提供硬件采集控制、多维分峰或 RSF 定量。

单次拟合失败或超时会保留父会话模型，先核对状态再决定调整参数。桥接重启后会话、项目和文档代理编号失效，需要重新打开项目与谱图、重新列出实时文档并关联谱图。写操作响应丢失时先检查输出文件，不自动重试。完成必要保存后用 `avantage_close_session` 释放会话；项目可用 `avantage_close_processing_project` 释放，独立打开的会话会保留。
