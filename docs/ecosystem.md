# 科研光谱处理与一键绘图

这三个独立项目围绕同一个目标协作：以 Origin Spectra MCP 为核心，让研究者通过自然语言将光谱数据整理成可编辑的 Origin 图页。

| 项目 | 职责 |
| --- | --- |
| [Origin Spectra MCP](https://github.com/g457337844-spec/origin-spectra-mcp) | 核心 MCP 服务；通用 Origin 自动化、XPS/XRD 绘图、外观预设和数据回读检查 |
| [Avantage MCP](https://github.com/g457337844-spec/avantage-mcp) | 在已安装的 Avantage 中读取谱图、设置背景和峰模型、调用原生拟合并导出曲线 |
| [Plotter](https://github.com/g457337844-spec/plotter) | Windows 数据整理、预览、样品选择和绘图入口；也提供 MCP 工具 |

## 现有能力与集成目标

Origin Spectra MCP 已整合 Plotter 的部分解析与绘图流程，不要求同时启动 Plotter。独立 Plotter 也能直接调用 Origin；两者是可选择的绘图入口，并非必须串行运行的三个软件。

Avantage MCP 已提供曲线导出；两个绘图项目支持表格/文本数据导入。跨工具工作流需要核对导出列、能量轴、单位、背景及分峰含义，必要时显式映射或转换。当前没有经过端到端验证的跨三个项目统一“一键”命令。

集成目标是让 MCP 客户端协调“读取已确认的 Avantage 处理结果 → 整理/校验数据 → Origin 原生绘图 → 回读核对 → 导出图像”。这是一键绘图目标，不代表自动决定峰数、化学态或自动证明拟合结果正确。

## 验收方式

使用合成或获准公开的数据，记录源数据和导出列定义，检查结合能方向、点数、单位与缺失值；核对绘图工作簿逐点数据，保存可编辑图页和导出图片。记录真实软件版本与未通过的步骤。只有完整流程实测通过后，才把跨工具一键联动标为已验证。

## 软件与数据边界

需要用户自行安装并合法使用 Origin/OriginPro、Thermo Scientific Avantage。项目不提供商业软件、许可证、厂商 DLL 或第三方谱图库。示例和问题报告请使用合成或允许公开的数据。
