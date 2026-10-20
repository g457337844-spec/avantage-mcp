# Avantage MCP 协作交接

用户要求 Codex 负责方向、拆解、审查与验收，DeepSeek Harness 负责基础勘查、范围明确的修改和测试，以减少主模型重复工作。用户已授权开始此协作，并提醒基础 MCP 又有改动，必须重新检查。

## 目录和现场

- 当前源码及本机环境：E:\Desktop\文件\工具\avantage-mcp，版本字符串 1.4.0，无 .git。所有既存改动须保留。
- 已验证基线：C:\Users\Administrator\Documents\Codex\2026-10-13\ni-zh\outputs\avantage-mcp，1.3.1。不能覆盖为旧版本。
- 新一轮改动前文件指纹：C:\Users\Administrator\Documents\Codex\outputs\avantage-mcp-coordination-20261019\preexisting-source-manifest.json。
- 早先 1.4.0 审查报告：C:\Users\Administrator\Documents\Codex\outputs\avantage-mcp-1.4.0-review-20261019.md。此报告早于后来新增的原生建峰修改，应重新核验，不可直接当成当前结论。
- 旧 DSH 桌面对话标题 avantage，session-f86bfef7-411d-47a3-b2df-61b2295ac631。最近停在 VGT 生成失败、应用崩溃后的说明，没有派发新任务。

## 已知方法和问题

此前实际交付 Cu2p 的路径是已有峰参数实时修改/普通保存，以及结构变化时完整 VGP+DATA 的私有保存、备份和写回。手工拼接 Ni.vgt 变成 C1s 文件不是此前交付采用的方法。

原版 VGT 验证是 MCP 原生导出再内部回读，不等于已验证 Avantage 菜单导入。不要扩大验证结论。旧 DSH 近期尝试从 Ni.vgt 重建 CPeakItem、CFittedPeakItem、XRefs 与背景，混用了不同谱区作为对照，且发生应用崩溃。错误文本不同不能证明更接近成功；字段看起来合理不能证明格式已完整解析。应先建立同谱图、同峰表的阳性对照，再最小定位问题。

当前 native 目录出现 ApplyLiveModelCreate.cs，并改过 ProcessingDocuments.cs、DirectLiveParameters.cs、ProcessingProject.cs、NativePeakTablePersistence.cs；这些后续改动尚未由主 Codex 验收。尤其核对结构修改是否被提前启用、异常回滚、原窗口生命周期、指针释放和元数据关联。

旧 1.4.0 审查确认过：清理只保护 list_sessions 而漏掉打开项目；查询活动会话失败后仍继续清理；attach 把 VGP 文件名当谱图名；应用未启动仍报实时可用。149 passed/37 skipped 是当时普通测试结果，不代表后续修改通过或跳过的集成测试通过。

## Harness 路由

当前 Relay MCP 0.2.19-local.1 正常连接 Web 实例 127.0.0.1:3081，其 DSH_HOME 为 E:\Desktop\文件\工具\DeepSeek连接\运行数据。用户正在看的桌面 DSH 是另一实例，数据在 C:\Users\Administrator\.dsh，不能混为一个会话。

对当前源码调用 list_workspace_sessions/start_service 均返回 workspace is not registered in Harness。先通过受支持的工作区登记或窄范围配置解决，只允许这个项目。不得挪用其他项目的注册身份。不得用临时 shell/Node/Python 客户端调用或轮询 Relay RPC，不读取凭据或复制登录令牌。

可用模型目录明确列出 workbuddy-ai/deepseek-v4.1-flash 为 Free now，默认 reasoningEffort=max。旧桌面对话使用带费用倍率的 deepseek-v4.1-flash-sg，不要未经必要改用该路线。

## 首轮工作与验收

1. 由 Codex 新建受控 DSH 会话，先派一个只读任务：核对基础 MCP 及近期原生改动，给出至多三项可复核问题和同谱图 VGT 最小对照测试方案。
2. Codex 独立核实后，再按一个问题一次修改的粒度授予 workspace-write；限定源码和 tests，不并行编辑同一文件。
3. 原文件保存、原生回读、GUI 导入必须分别验证。数值拟合与价态归属也分别报告。
4. 不自动打开多个 Avantage 结果窗口，不在样品目录堆积文件。测试使用项目外隔离副本。
5. 不删除真实备份/私有现场，不修改用户谱图、峰值库或 Avantage 安装目录，不终止应用或持久连接锚点，不在用户窗口加载手拼 VGT。
6. 不提交、推送、上传源码或样品，不修改凭据、权限或 .git 内部。公开资料研究可按任务需要进行，关键外部论据附 URL。
7. 第一个 DSH 任务只读，不应重演危险实验；不可执行项如实报告 not_applicable 并交主 Codex 决策。

用户希望最终修改结果保存在原文件、同峰数操作保留窗口。当前任务是修复 MCP 和验证，不重新调整样品分峰。
