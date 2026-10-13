---
name: avantage-mcp
description: 使用本仓库的 Avantage MCP 处理 XPS 能量谱图，包括能量校准、背景、峰参数与约束、原生分峰及 VGD/VGT 结果保存。用户要求用 Thermo Scientific Avantage 分峰、读取谱图或恢复峰表时使用。
---

# Avantage XPS 处理

调用已连接 Avantage MCP 的 `avantage_*` 工具；客户端可能为工具名加上服务前缀。处理参数、化学归属及交付路径以用户要求为准。工具参数和环境设置见 [仓库 README](../../README.md)，真实验证范围见 [验证摘要](../../docs/validation.md)。

## 连接和谱图

- 先调用 `avantage_status`，核对安装版本、原生拟合可用性及工具能力。服务未连接时检查已有连接；需要新增安装或修改配置时，按用户授权范围执行 README 的安装步骤。
- 用 `avantage_open_spectrum` 打开绝对 Windows 路径的 `.VGD`，保存返回的 `session_id`。该会话处理私有副本，与 Avantage 当前可见网格独立。
- 用 `avantage_get_spectrum_info`、`avantage_get_spectrum_data` 查看能量轴、强度和谱图范围，再确定背景及峰初值。不要把样例参数或数值峰标签解释为用户样品的化学归属。缺少会影响结果的模型信息时，先读取数据，再询问所需峰组或约束。

## 设置和拟合

1. 需要电荷校准时，先调用 `avantage_charge_shift`，再定义背景和峰。正 `delta_ev` 增加结合能、降低动能；使用厂商提供的能量换算，不自行用光子能量相减替代。
2. 调用 `avantage_set_background` 设置 Linear、Shirley、Smart、Tougaard 或 Simple 背景。结合能端点须递增并在谱图内。已有峰模型时，先在用户授权的模型修改范围内清除模型，再替换背景。
3. 用 `avantage_add_peak` 提供明确的 `label`、`centre_ev`、`height` 和 `fwhm_ev`，按需用 `avantage_update_peak` 修改。G/L 比为 0–1；一次拟合的峰使用相同的 product、sum 或 convolution 组合方式。
4. 用 `avantage_set_peak_constraint` 配置用户所需固定、范围或关联约束。范围用 `value1/value2`，关联用 `reference_peak_id` 和运算符。结合能峰位关联只支持加减；删除被引用的峰前解除关联。
5. 调用 `avantage_run_fit` 执行 Avantage 原生优化。对称峰默认 `asymmetric=false`；启用非对称拖尾时，在拟合、读取曲线和导出曲线中一致设置 `asymmetric=true`。保持客户端时限大于桥接时限，桥接时限大于单次拟合时限。

拟合后用 `avantage_get_fit_model` 和 `avantage_get_model_curve` 核对峰参数、约束、`fit_executed`、`converged`、`stop_reason`、迭代次数及 RSS/RMSE。达到迭代上限不算收敛；数值收敛也不证明化学模型正确。范围外背景、总模型和残差为 `null`，不可当作零。报告所用模型与参数，以及尚未支持的化学解释。

## 保存和恢复

根据用户交付要求另存到独立路径，已有目标只有明确允许覆盖时才使用 `overwrite=true`：

- `avantage_save_spectrum`：VGD 谱图及校准能量轴，独立峰模型不嵌入 VGD。
- `avantage_export_peak_table`：原生 VGT，含峰、背景与约束；返回独立原生重载验证结果。
- `avantage_export_fit_model`：MCP JSON 参数及谱图指纹。
- `avantage_export_model_curves`：分峰、背景、总模型和残差的 CSV/JSON。

恢复 VGT 时先打开匹配的 VGD，可用 `avantage_inspect_peak_table` 查看，再用 `avantage_import_peak_table` 替换模型。JSON 用 `avantage_import_fit_model`，默认要求指纹一致；仅在用户有意复用其他谱图参数时允许不同谱图。导入只恢复参数，不执行优化，`fit_executed=false`。若要在 Avantage 界面继续处理，说明需打开匹配谱图并加载峰表；`avantage_show_data_file` 只发送 VGD 打开请求，不能证明当前网格已显示或同步。

## 可用范围与故障

已验证 Windows x64、Avantage 6.9.0.59 的一维等间隔能量谱图，一个公共背景，最多 100 个峰。拟合与 VGT 接口按 DLL 哈希启用；出现不支持构建时停止这些操作并说明需重新适配，不绕过校验或改用其他优化器冒充原生拟合。不提供硬件采集控制、当前网格自动同步、多维分峰、RSF 定量或 VGP 项目编辑。

单次拟合失败或超时会保留父会话模型，先核对状态再决定调整参数。桥接重启后会话编号失效，需要重新打开谱图。写操作响应丢失时先检查输出文件，不自动重试。完成必要保存后用 `avantage_close_session` 释放会话。
