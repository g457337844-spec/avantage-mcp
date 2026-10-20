# 2026-10-19 本机优化记录

本轮在现有 1.4.0 源码上修复基础行为，保留之前的原生建峰等改动。未重新拟合或写入用户样品，未验证所有既存原生改动。

- 清理保护：原生进程在独立谱图和完整 VGP 项目的私有目录持有独占租约文件。Python 清理器跨进程检查租约，只回收已释放且超过时间阈值的目录。没有租约的旧目录保留并计入 `unverified_folders` / `kept_unverified`，不能仅凭时间判断可以删除。
- 活动查询失败、响应格式错误时，清理直接报错，不再按“没有活动会话”继续执行。
- 状态区分 `live_processing_supported` 与 `live_processing_available`；后者要求 Avantage 当前正在运行。原生版本另保留为 `native_bridge_version`，便于诊断版本不一致。

验证：161 passed，37 skipped。新增测试覆盖实际 C# 租约的跨进程排他性、完整项目和谱图的独立释放、旧目录保留、查询失败和异常响应时拒绝清理、应用未运行的状态判断。完整原生程序编译通过；额外以安装目录的 Glasses1.VGD 样例验证“打开时保留、关闭后回收”，样例 SHA-256 未变。真实用户窗口写回与拟合测试未执行，不应据此声称它们已通过。

新版二进制已部署到 native/AvantageBridge.exe；为保留仍运行的旧进程，旧映像暂留 native/AvantageBridge.previous-20261019.exe。刷新 MCP 连接加载新 Python 和新原生程序，Avantage 本身无需退出。部署前源文件及旧二进制备份在 C:\Users\Administrator\Documents\Codex\outputs\avantage-optimize-20261019。

首轮 DeepSeek Harness 的派发和读取已接通，但普通 Flash 返回 WorkBuddy 429 限流，没有产出审查或修改。以上首轮修复及验证均由 Codex 完成，当时未切换收费模型。

## 用户选择 SG 后的协作验收

用户明确选择 WorkBuddy 的 deepseek-v4.1-flash-sg（×0.03），该路由可执行任务。接口不接受显式 reasoningEffort=max，本轮用模型默认设置，未证实等同于界面 Max。

DeepSeek 只读审查提出三条发现。Codex 复核采纳“运行但无响应仍被判可用”；另外两条关于子进程目录父路径和 C# 数值 0 的结论是误报，已退回，DeepSeek 明确撤回。未据此改原生拟合或背景逻辑。

授权 DeepSeek 只修改 server.py 和 test_tool_inventory.py。新增响应状态及分场景提示；无响应或响应状态未知时不再宣称 live 可用。DSH 测试执行受到沙箱阻断，它请求修改项目及父目录 ACL，该申请由 Codex 拒绝；未批准权限变更。Codex 接手执行测试，首轮发现一个分支错误（false 被归为 unknown），退回 DeepSeek 修正并保留原失败断言。

最终 Codex 独立验证：170 passed，37 skipped。基线文件指纹比对确认 DeepSeek 仅改两个授权文件。README 和本记录由 Codex 在任务终止后同步。真实用户窗口写回、拟合等跳过项目仍未执行，不能当作本轮已验证。代码已保存到现有部署目录，已运行的 MCP 进程需要下次重连才能加载新增 Python 状态逻辑。
