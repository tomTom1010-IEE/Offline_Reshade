# 第一次重构执行记录

目标来源：[完整执行计划](FIRST_REFACTOR_PLAN.md)。实施范围是第 1–8 步及对应自动化验证，用户验收另行进行。全程禁止 computer use；驱动变更测试继续延期。

分支：`codex/first-architecture-refactor`。起点 `98de8c10` 保存已有 0.8.3 维护成果；`.codex/`、`artifacts/` 和 `hello_world.py` 未纳入该基线。

| 步骤 | 状态 | 当前证据与剩余工作 |
|---|---|---|
| 1 状态归属与配置迁移 | 基础实现及测试完成 | Core 分离 EditorDocument、ApplicationPreferences 和 EditingSession；WinUI 绑定适配；配置 v2 迁移、备份、原子保存；设置页待应用提示。后续步骤将统一所有运行路径与操作结果 |
| 2 会话生命周期 | 待实施 | 需要合并 WinUI/NativeSession 的启动、切图、恢复、取消、释放，保留未保存参数 |
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

## 完成判定

不能以第一步测试通过代替全部目标完成。每个待实施项都需要实际接入调用路径及相应测试；最终补充单图/批处理输出正确性、D3D、独立和附着 MCP 与发布校验的证据，并明确保留用户验收项。
