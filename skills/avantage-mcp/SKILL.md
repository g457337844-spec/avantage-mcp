---
name: avantage-mcp
description: 使用本仓库的 Avantage MCP 读取 VGD 谱图与 VGP 项目，修改已有峰参数和约束，执行原生分峰并保存结果。实时文档操作须以原生验证能力为准。用户要求用 Thermo Scientific Avantage 分峰、读取谱图或恢复峰表时使用。
---

# Avantage XPS 处理

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

## 实时文档

实时文档适配已在受支持构建上验证。先核对 `avantage_status` 中 `native_live_document_bridge_verified`；只有其值明确为 `true` 才能执行下面流程。遇到 `LIVE_DOCUMENT_BRIDGE_NOT_VERIFIED` 应说明此构建没有启用实时写回，不将私有会话编辑或 VGT 导出报告为当前窗口修改成功，不绕过保护直接调用实验接口。

能力启用后的流程：`avantage_list_processing_documents` 选择明确的 `document_id` → `avantage_list_document_spectra` 选择 `spectrum_index` → `avantage_attach_document_spectrum` 获取带文档关联的 `session_id` → 使用峰、约束和拟合工具编辑 → `avantage_apply_fit_to_document` → 核对原生读回验证 → `avantage_save_processing_document` 另存完整文档。独立打开 VGP/VGD 的会话不能假定拥有实时文档关联。应用中的文档发生变更时应重新读取，不能无条件覆盖。

`apply_fit_to_document` 会保存检查点和完整结果副本，并让 Avantage 加载结果；核对 `live_readback_verified`、`saved_project_verified`、`grid_bindings_verified` 和返回路径。结果会在全新文档/视图打开，避免旧网格缓存。其它关联会话可能标为过期，应重新关联。对话要求实际修改当前软件时，应完成这一步，不能停在私有会话编辑。文档另存目标文件与同名 `.DATA` 目录须不存在；成功后 Avantage 的当前文档路径改为新副本。另存后继续修改应重新关联，保留用户在软件里的更新。保存和重新打开验证后再报告持久化完成。写操作响应丢失时先检查应用和目标文件，不自动重试。

## 保存和恢复

根据用户交付要求另存到独立路径，已有目标只有明确允许覆盖时才使用 `overwrite=true`：

- `avantage_save_spectrum`：VGD 谱图及校准能量轴，独立峰模型不嵌入 VGD。
- `avantage_save_project_model`：来自 VGP 的谱图会话可将其模型另存到完整项目副本。须保持源项目打开，状态中 `native_project_save_verified` 明确为 `true`，且目标 VGP 与同名 `.DATA` 目录不存在。核对原生重新加载验证；未经验证状态不会发送写入。生成项目副本不等于当前应用窗口已更新。
- `avantage_export_peak_table`：原生 VGT，含峰、背景与约束；返回独立原生重载验证结果。
- `avantage_export_fit_model`：MCP JSON 参数及谱图指纹。
- `avantage_export_model_curves`：分峰、背景、总模型和残差的 CSV/JSON。

恢复 VGT 时先打开匹配的 VGD，可用 `avantage_inspect_peak_table` 查看，再用 `avantage_import_peak_table` 替换模型。JSON 用 `avantage_import_fit_model`，默认要求指纹一致；仅在用户有意复用其他谱图参数时允许不同谱图。导入只恢复参数，不执行优化，`fit_executed=false`。若要在 Avantage 界面继续处理，说明需打开匹配谱图并加载峰表；`avantage_show_data_file` 只发送 VGD 打开请求，不能证明当前网格已显示或同步。

## 可用范围与故障

已验证 Windows x64、Avantage 6.9.0.59 的一维等间隔能量谱图，一个公共背景，最多 100 个峰。VGP 支持完整项目读写与实时模型修改。完整项目模型保存与实时写回要求原始数据和校准轴未变；电荷校准通过独立 VGD 流程保存。显示比例 `area_ratio`、`height_ratio` 不在厂商文件中持久化；原子质量可能未知。多背景和跨谱图关联模型可能拒绝独立编辑。依赖布局的接口按 DLL 哈希启用；出现不支持构建时停止相应操作并说明需重新适配，不绕过校验或改用其他优化器冒充原生拟合。不提供硬件采集控制、多维分峰或 RSF 定量。

单次拟合失败或超时会保留父会话模型，先核对状态再决定调整参数。桥接重启后会话、项目和文档代理编号失效，需要重新打开项目与谱图、重新列出实时文档并关联谱图。写操作响应丢失时先检查输出文件，不自动重试。完成必要保存后用 `avantage_close_session` 释放会话；项目可用 `avantage_close_processing_project` 释放，独立打开的会话会保留。
