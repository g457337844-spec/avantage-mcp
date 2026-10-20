# 后续任务：完整模型写回，而非再次隔离入口

用户明确要求由 Codex 统筹、DSH 执行基础实现。新选定路由为 Magpie / workbuddy-ai/deepseek-v4.1-flash / max。只有 Relay 当前实例目录和运行快照都确认该路由后才派发；旧 current-task 中的直连 SG 选择已过时。

核心验收：原项目的指定谱图原先没有拟合峰，也能接收完整模型；原始计数与能量轴不变，其他谱图和网格关联不变，新增峰的参数、背景、约束正确，正常显示、保存、重新读取一致。最终使用原文件，不创建一堆用户可见文档。

先做一个有限的只读切片：追踪 native/ProcessingDocuments.cs、ApplyLiveModelCreate.cs、NativePeakTablePersistence.cs、ProjectPersistence.cs、DirectLiveParameters.cs。查清 ProcessingDocument.Pointer 实际来自哪个 COM 接口，实验创建路径却把它作为 CProcessGridDoc 原生 C++ this 指针传给导出函数是否成立；用代码位置、接口声明或反汇编证据回答，不凭名称推断。区分 COM 接口指针、进程内 C++ 对象地址、跨进程代理，不能把它们互换。

同时确定厂商公开的 COM 导入/创建方法或其他受支持的同窗口结构修改入口；找不到时明确说明证据缺口。不要重新启用 ApplyLiveModelCreating，不把三峰数量一致当成模型验证。

第二阶段只在 Codex 复核第一阶段证据后实施：先构建与真实应用分离的、同一谱图原生导出再导入的阳性对照，再测试结构变化和所有权。不得用 Ni2p 模板伪造 C1s 峰表、不得直接修改 OLE 二进制、不得把未经验证的 VGT 导入用户窗口。

范围仅本源码 native、src、tests、scripts、docs；排除 .venv、用户样品、verification 中的样品副本、密钥和账号配置。保留全部现存改动。不连接或终止真实 Avantage，不改安装目录、ACL、锚点或凭据，不提交推送。需要额外动作时向 Codex 返回具体请求。公共资料可按需要查，关键外部主张附 URL。

本文件是待派发任务说明，不能作为任务已派发、已执行或功能已完成的证明。
