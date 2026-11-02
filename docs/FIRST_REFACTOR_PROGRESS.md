# 第一次重构执行记录

目标来源：[完整执行计划](FIRST_REFACTOR_PLAN.md)。实施范围是第 1–8 步及对应自动化验证，用户验收另行进行。全程禁止 computer use；驱动变更测试继续延期。

分支：`codex/first-architecture-refactor`。起点 `98de8c10` 保存已有 0.8.3 维护成果；`.codex/`、`artifacts/` 和 `hello_world.py` 未纳入该基线。

| 步骤 | 状态 | 当前证据与剩余工作 |
|---|---|---|
| 1 状态归属与配置迁移 | 基础实现及测试完成 | Core 分离 EditorDocument、ApplicationPreferences 和 EditingSession；WinUI 绑定适配；配置 v2 迁移、备份、原子保存；设置页待应用提示。后续步骤将统一所有运行路径与操作结果 |
| 2 会话生命周期 | 实现及自动化验证完成 | Core RenderSessionController 统一启动、切图、重载、恢复、取消和释放；WinUI/独立 MCP 复用，附着 MCP 回归通过；真实进程测试验证参数保留和宿主释放 |
| 3 操作与状态反馈 | 实现及自动化验证完成 | Core 操作队列、结构化结果、取消和错误恢复；WinUI 可见反馈及空状态；MCP broker 2。等待输入状态模型已具备，实际监听入口在第四步接入 |
| 4 素材入口 | 实现及自动化验证完成 | 主界面打开图片/监听游戏输出，工作区管理素材、深度和预设；缺失/损坏文件等待与恢复、失败保留上下文；独立和附着 MCP 通过同一真实渲染回归 |
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

## 第三步验证

- `OperationCoordinator` 统一 WinUI、附着及独立 MCP 的参数修改、输入/预设、重载、导出和批处理互斥。参数更新排队；排他的长操作先等待已接收的修改，再拒绝新的修改。取消和停止等待真实任务完成，批处理恢复输入结束后才释放操作。
- `ApplicationActivity` 区分运行观察、当前操作、最后结果和持久提示。成功、警告、取消、失败和忙碌拒绝有结构化结果；轮询不会擦除错误。空画布依赖有效帧状态，不依赖亮度；黑色作品保持成功并附带警告。
- WinUI 常规命令和动态参数控件统一反馈到 InfoBar；显示原因、适用的重试、定位目录和详情。移除顶部同时显示 Ready/Disconnected 的方式，无批处理任务时收起其进度区域。只有预览传输、表面尺寸、原生 Add-on 页签选择等显示操作仍直接调用宿主；Add-on 用户输入经过共享操作层。
- `get_operation_status` 在断连和操作进行中可用；`cancel_operation` 支持普通操作，附着 MCP 经第二条控制连接取消正在等待响应的请求。MCP 的错误与多媒体预览均携带结构化操作结果；没有批任务时 `cancel_job` 不取消其他操作。
- 保存预设先导出到独立临时文件，确认实际写入非空文件后才替换目标；静默保存失败不破坏旧文件。修复重复加载同一路径预设时没有重新读取磁盘的问题，继续使用私有工作预设保护源文件。UI 的完整保存/另存及未保存语义继续在第六步实施。
- Automation broker 升级为 2，并同步生成常量、接口锁及 MCP/维护文档；native RPC 5、offline API 1 和 preview ABI 3 保持不变。
- 新增 `OperationTests`：验证参数先于导出提交、忙碌拒绝、取消/停止期间的任务追踪、预设失败原子保护及重试、错误保留、黑帧语义、双客户端 broker 状态与取消、结构化错误传递。`SessionTests` 增加相同路径预设显式重载。
- 增强真实 `run_integration.py`：验证预览和导出结果结构、黑色警告、损坏输入的错误来源、重复载入预设、导出进行中的状态查询/取消及取消后的再次导出。
- 首先运行包含原生重编译的 `tools/build_offline_release.ps1 -RunIntegrationTests -OutputDirectory build/refactor-step3`，通过。完成后续托管层修正后，最终运行 `-SkipBuild -RunIntegrationTests -OutputDirectory build/refactor-step3-verified`，通过；仅复用本轮重编译且未再修改的 C++ 组件，重新发布 WinUI/Core/MCP。
- 最终报告：`build/refactor-step3-verified/verification-20261103-035148.json`。包：`build/refactor-step3-verified/OfflineReShade-0.8.3-win-x64-20261103-035148`；ZIP SHA256：`d8085f9f5100a4e85d895fc42e20c224a1e3f3a6e79777ca9de442ee00ec19c1`。包含输出正确性、缓存失效、D3D/故障注入、实际宿主生命周期、接口拒绝、完整性及 CI 规则校验。
- 通过命令行启动独立测试包 `OfflineReShade.RefactorOperations035148`，附着会话 `84ec73af0a9048b6bd0f890b64f8e8e0`；`run_integration.py`、`run_output_correctness.py`、`run_d3d_recovery.py` 均通过。结果目录位于上述最终输出目录的 `attached-integration`、`attached-correctness`、`attached-recovery`。
- 额外只读协议查询确认：缺失贴图引起的批处理失败仍保留在 `notice`，正常帧/状态轮询不覆盖该错误。按路径核对后结束本轮测试 UI 进程 15488 与子进程 38656，测试包已注销。
- 全程没有使用 computer use。窗口视觉、实际按钮/键盘操作和关闭窗口交互留待用户验收；第三步尚未把素材入口搬出 Settings，也未实现缺失文件的主动监听入口，这部分按计划属于第四步。发布包未对外发布。

## 第四步验证

- 主界面加入 `Open image` 和 `Listen for game output`；当前已应用图片、深度和预设持续可见。工作配置移到 `Input, depth and preset`，Settings 保留资源目录、图库默认目录、预览与高级偏好。顶部使用 CommandBar，次要命令进入溢出菜单。两个导出按钮仍保留，按第五步统一。
- Core `InputPairing` 统一单图和图库的游戏输出、截图与视频帧配对；支持无深度 PNG，多种匹配时要求明确选择。选择或输入校验失败不会把旧图改名为新素材；成功打开图库外图片会清除旧缩略图选择。深度配置切换不再暗中替换素材路径。
- `InputArrivalMonitor` 等待完整、稳定且可读的初始输入。缺失文件保持等待，访问失败显示具体文件和原因，损坏配对只在文件签名变化后重试。监听等待期间拒绝编辑/导出旧画面；停止监听保留最后有效图像、参数和未应用字段；手动打开输入取消旧监听，迟到文件不会覆盖新选择。
- WinUI 与 MCP 共用 `listen_input` / `stop_listening`，`get_operation_status` 区分 `waitingInput` 和 `appliedInput`。停止原生监听无需重新打开源文件。Automation broker 升级为 3，生成常量、接口锁和 MCP/维护文档同步；native RPC 5、offline API 1、preview ABI 3 不变。
- 修复发布包默认 Effects 路径受启动工作目录影响的问题；UI 应用时将输入、效果、预设和输出的相对路径按程序目录解析。成功应用后同步草稿及当前深度配置，保留旧配置读取能力。中英文使用说明同步新入口。
- Core `InputWorkflowTests` 覆盖配对歧义、KK/raw 限制、无深度素材、相对路径、缺失配对、文件占用、损坏输入、变化后重试、监听覆盖、取消和旧结果导出拒绝；现有 Core 全套通过。
- 新增 `tests/run_input_workflow.py` 并接入构建流水线，以真实 MCP/native 验证首次缺失输入、损坏配对回退、原参数像素保持、修复后的尺寸切换、停止和手动覆盖监听。
- 包含原生重编译的 `tools/build_offline_release.ps1 -RunIntegrationTests -OutputDirectory build/refactor-step4` 通过。完成图库选中状态修正后，最终执行 `-SkipBuild -RunIntegrationTests -OutputDirectory build/refactor-step4-verified`，通过；只复用本轮已重编译且未修改的 C++ 组件。
- 最终报告：`build/refactor-step4-verified/verification-20261103-042015.json`。包：`build/refactor-step4-verified/OfflineReShade-0.8.3-win-x64-20261103-042015`；ZIP SHA256：`77c36def19d9c35f47129878d6d2558b33ab7a55e90af4758fa25a843cfa2006`。包含完整性、接口、CI、实际 D3D、输出正确性、批缓存和宿主释放验证。
- 仅经命令行启动独立测试包 `OfflineReShade.RefactorInput042015`，附着会话 `9d2ea137cf9249409009221792bc6b52`；`run_input_workflow.py`、`run_integration.py`、`run_output_correctness.py`、`run_d3d_recovery.py` 全部通过。结果在最终输出目录的 `attached-input`、`attached-integration`、`attached-correctness` 和 `attached-recovery`。
- 代码审查修复启动目录依赖、深度配置同步、旧图库选择及已应用后残留待应用提示；现有动态面板和控件可访问性整理继续在第七步完成。构建使用仓库固定工具链，没有升级依赖。
- 按完整可执行路径核对后结束本轮 UI 进程 12240 与宿主 24252，确认测试进程为零、测试包已注销。全程没有使用 computer use，也没有操作用户现有程序。实际按钮、键盘、视觉布局及窗口关闭仍属于用户验收；发布包没有对外发布。

下一步为第五步：统一导出面板和目标冲突策略、待应用配置处理，以及 UI 参数提交屏障；不能以 Core 队列已排空代替尚未入队的滑块更新已提交。

## 完成判定

不能以第一步测试通过代替全部目标完成。每个待实施项都需要实际接入调用路径及相应测试；最终补充单图/批处理输出正确性、D3D、独立和附着 MCP 与发布校验的证据，并明确保留用户验收项。
