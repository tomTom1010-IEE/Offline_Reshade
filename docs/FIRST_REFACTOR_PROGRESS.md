# 第一次重构执行记录

目标来源：[完整执行计划](FIRST_REFACTOR_PLAN.md)。实施范围是第 1–8 步及对应自动化验证，用户验收另行进行。全程禁止 computer use；驱动变更测试继续延期。

分支：`codex/first-architecture-refactor`。起点 `98de8c10` 保存已有 0.8.3 维护成果；`.codex/`、`artifacts/` 和 `hello_world.py` 未纳入该基线。

| 步骤 | 状态 | 当前证据与剩余工作 |
|---|---|---|
| 1 状态归属与配置迁移 | 基础实现及测试完成 | Core 分离 EditorDocument、ApplicationPreferences 和 EditingSession；WinUI 绑定适配；配置 v2 迁移、备份、原子保存；设置页待应用提示。后续步骤将统一所有运行路径与操作结果 |
| 2 会话生命周期 | 实现及自动化验证完成 | Core RenderSessionController 统一启动、切图、重载、恢复、取消和释放；WinUI/独立 MCP 复用，附着 MCP 回归通过；真实进程测试验证参数保留和宿主释放 |
| 3 操作与状态反馈 | 待实施 | 需要共享操作入口、结构化结果与可见错误/空状态 |
| 4 素材入口 | 待实施 | 需要主工作区输入、来源切换和失败保留上下文 |
| 5 统一导出 | 待实施 | 需要统一导出面板、待应用处理、参数提交屏障、目标及冲突策略 |
| 6 编辑历史 | 待实施 | 已预留 IEditHistory；撤销事务、预设修改标记与关闭处理待实现 |
| 7 画布与布局 | 待实施 | 需要组件划分、自适应、可见缩放与只影响预览的对比 |
| 8 批处理与 Add-on | 待实施 | 需要选择范围、逐项结果、重试/恢复、固定设置和诊断分层 |

## 第一步验证

- `dotnet run --project tests/OfflineReShade.Core.Tests/OfflineReShade.Core.Tests.csproj -c Release --no-restore`：通过。新增 WorkspaceTests 验证草稿/生效分离、失败未确认、迟到完成失效、尺寸非法值、旧配置及便携配置迁移、重启读取、并发保存顺序、取消保存、未来/损坏配置保护。
- VS 2022 MSBuild 编译 `OfflineReShadeWinUI/OfflineReShadeWinUI.csproj`，Release/x64：通过。
- 既有 Core 输出保护、RPC 取消、预热、批处理续跑、DPI 几何、恢复预算与兼容握手测试：通过。
- 本步不变更 native RPC/preview ABI。实际窗口交互未通过 computer use 检查，也不据此声称通过用户验收。

## 第二步验证

- `RenderSessionController` 集中生命周期与请求版本；`NativeSession` 提供原生进程适配，WinUI 仅监听预览管道及资源重置事件。删除 WinUI 中重复的进程启动、RPC 包装和命令行拼接服务。
- 切图/重建前捕获当前参数；输入应用失败恢复原输入及参数；旧请求不能覆盖新请求；停止和关闭异步等待释放。窗口关闭先取消任务、保存配置并释放宿主，再关闭窗口。
- 原生宿主使用私有工作预设，防止 ReShade 的自动保存写回用户源预设；进程意外退出后可从已刷盘的工作副本恢复。源预设的显式保存/另存与修改标记仍属于第六步。
- Core SessionTests：普通切图、尺寸重建、效果重载、参数保留、失败回退、快照失败、请求替换、取消、恢复次数上限和释放均通过。
- `tools/build_offline_release.ps1 -SkipBuild -RunIntegrationTests -OutputDirectory build/refactor-step2-final`：通过。报告 `build/refactor-step2-final/verification-20261103-030601.json`；包含现有输出正确性、真实 D3D/故障注入、兼容契约和发布包校验。仅复用未修改的原生 C++ 二进制，重新编译并发布 Core、WinUI 和 MCP。
- 新增 NativeProcessTests 并接入上述构建脚本：验证实际子进程停止、重建和 Dispose 后退出；强制结束自己创建的宿主后恢复 0.62 参数值及全局效果开关；源预设字节未改变。
- 增强 run_integration.py：源图尺寸变化、显式输出尺寸变化保留 0.25 参数；损坏图片回退原尺寸及像素；源预设字节保持不变。
- 使用独立测试包 `OfflineReShade.RefactorLifecycle030601`，仅经 winapp 命令行启动并通过附着 MCP 运行 `run_integration.py`、`run_output_correctness.py`、`run_d3d_recovery.py`：全部通过。工作目录分别为 `build/refactor-step2-final/attached-integration`、`attached-correctness`、`attached-recovery`。
- 附着测试结束后按 PID 和完整路径核对并终止测试进程及其宿主，没有通过 computer use 操作窗口。真实宿主正常释放由 NativeProcessTests 证明；窗口关闭事件、键盘和视觉交互保留用户验收。
- 最后补充启动期间 Stop 命令可用性，随后 Release/x64 再次编译通过；该按钮的实际交互留待用户验收。未修改原生协议或预览 ABI。

## 完成判定

不能以第一步测试通过代替全部目标完成。每个待实施项都需要实际接入调用路径及相应测试；最终补充单图/批处理输出正确性、D3D、独立和附着 MCP 与发布校验的证据，并明确保留用户验收项。
