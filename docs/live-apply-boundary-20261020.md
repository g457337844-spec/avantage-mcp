# 实时修改边界复核与修复（2026-10-20）

由 Codex 督导 DSH 的 workbuddy-ai/deepseek-v4.1-flash-sg/max 完成一轮只读审查及限定实现；所有调用都经 Harness Relay MCP。Codex 独立复核、测试、编译和部署。

审查确认两个实际问题：空原谱会自动进入实验新增峰路径；该路径仅验证峰数就报告参数和文档一致性已验证，且插入过程异常没有统一回滚。此前 Python 的工具合同仍称结构变化会被拒绝。这些问题不能通过把返回标志写成 true 解决。

本轮处理：公开 ApplyFitToDocument 始终转入 ApplyLiveParameters。实验 ApplyLiveModelCreating 源码保留，但没有调用者，不新增启用开关。原有峰数与空模型规则抽到无 COM 依赖的 LiveParameterPolicy，由生产验证路径调用；背景、约束和保存规则保留。

DSH 只修改四个授权文件：native/ProcessingDocuments.cs、native/DirectLiveParameters.cs、native/LiveParameterPolicy.cs、tests/test_live_parameter_policy.py。Codex 的基线文件指纹比对确认范围；随后仅为测试子进程补充 CREATE_NO_WINDOW，避免出现额外控制台。

验证：175 passed，37 skipped；完整原生编译通过。新增五项测试实际编译并执行生产策略源文件，验证空到非空、增峰、减峰、空到空拒绝，以及同峰数正数允许。Codex 额外检查编译后的入口 IL，确认其仅转发到 ApplyLiveParameters；源码中实验创建方法仅剩定义。未运行用户窗口写回、真实创建失败注入或拟合验证，不把跳过项当作通过。

二进制与匹配符号已安装到本目录 native/AvantageBridge.exe 和 .pdb，部署哈希已核对。原生旧映像保留为 previous-20261020，既有进程未终止。本聊天随后成功启动新原生进程并读取状态；Avantage 当时正在运行且有响应。其他已运行的连接需要自然重连后才加载新二进制。

备份与本轮构建位于 C:\Users\Administrator\Documents\Codex\outputs\avantage-live-boundary-20261020。未修改用户样品、Avantage 安装目录、ACL 或凭据；未提交或推送。

限制：这次是隔离危险入口，不代表“同窗口新增/删除峰”已经实现。已有峰参数修改继续沿用原窗口路径；结构变化目前仍使用完整私有 VGP+DATA 流程并在原文档关闭后写回。后续若要启用真正的同窗口结构修改，必须分别证明对象所有权、逐参数与其他谱图一致性、插入失败回滚，以及实际窗口保存往返；不能仅凭峰数或编译成功启用。
