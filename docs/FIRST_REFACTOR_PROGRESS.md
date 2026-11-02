# 第一次重构执行记录

目标来源：[完整执行计划](FIRST_REFACTOR_PLAN.md)。实施范围是第 1–8 步及对应自动化验证，用户验收另行进行。全程禁止 computer use；驱动变更测试继续延期。

分支：`codex/first-architecture-refactor`。起点 `98de8c10` 保存已有 0.8.3 维护成果；`.codex/`、`artifacts/` 和 `hello_world.py` 未纳入该基线。

| 步骤 | 状态 | 当前证据与剩余工作 |
|---|---|---|
| 1 状态归属与配置迁移 | 基础实现及测试完成 | Core 分离 EditorDocument、ApplicationPreferences 和 EditingSession；WinUI 绑定适配；配置 v2 迁移、备份、原子保存；设置页待应用提示。后续步骤将统一所有运行路径与操作结果 |
| 2 会话生命周期 | 实现及自动化验证完成 | Core RenderSessionController 统一启动、切图、重载、恢复、取消和释放；WinUI/独立 MCP 复用，附着 MCP 回归通过；真实进程测试验证参数保留和宿主释放 |
| 3 操作与状态反馈 | 实现及自动化验证完成 | Core 操作队列、结构化结果、取消和错误恢复；WinUI 可见反馈及空状态；MCP broker 2。等待输入状态模型已具备，实际监听入口在第四步接入 |
| 4 素材入口 | 实现及自动化验证完成 | 主界面打开图片/监听游戏输出，工作区管理素材、深度和预设；缺失/损坏文件等待与恢复、失败保留上下文；独立和附着 MCP 通过同一真实渲染回归 |
| 5 统一导出 | 实现及自动化验证完成 | 单图统一导出面板；显式应用后导出、参数提交屏障、原子发布及冲突策略；真实截图 Add-on 事件与独立/附着 MCP 回归通过 |
| 6 编辑历史 | 实现及自动化验证完成 | Core 统一 FX 事务、撤销/重做/复位、保存基准、另存与未保存决策；精确浮点和布尔向量往返；独立及附着 MCP、失败回滚及发布验证通过，窗口交互仍留给用户验收 |
| 7 画布与布局 | 实现及自动化验证完成 | 效果/素材/画布组件、可调与自适应面板、物理像素缩放、预览对比、主题及键盘/无障碍入口；真实 GPU/CPU 和独立/附着 MCP 回归通过，视觉及操作体验仍待用户验收 |
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

## 第五步验证

- 主工具栏的两个单图按钮合并为 `Export image`。独立 `ExportPanel` 显示素材、最终像素尺寸、目标路径和冲突策略；默认 ReShade screenshot，高级选项明确提供 Direct PNG 和预热帧数，失败不会切换截图方式。输出路径编辑移入导出面板。
- UI 文件冲突默认使用编号另存，可明确选择成功后覆盖或遇冲突报错。完成后显示实际结果路径，提供打开图片和定位文件夹；失败提示留在面板内，之前的成功结果标为 `Last successful export`。模态窗口按异步操作完成状态保持可见，支持取消进行中的导出。
- `ImageExportRequest` 支持 `applyInput` 和 `expectedInput`。有待应用配置时，按钮明确显示 `Apply changes and export`；应用和导出持有同一次共享排他操作，失败应用不能导出原图冒充新图。MCP 未明确提交 `applyInput` 时会拒绝带待应用 UI 配置的导出，状态查询同时提供 `pendingConfiguration` 及其校验错误。
- `DeferredEditQueue` 接管滑块/Add-on 控件的延迟更新，在后续修改、保存、切图或导出进入操作队列前同步提交最新值并提供完成屏障；提交失败会使依赖操作明确失败。后来的输入框或 MCP 修改不会被更早的延迟滑块值覆盖。原生 Add-on 鼠标移动在后续输入或操作之前提交。
- Core `ImageExport` 将已通过 native 验证的结果暂存到目标目录，检查实际写入后才发布成品。`replace`、`rename`、`fail` 处理输出竞争；编码失败、静默未写入和提交前取消保持已有结果。保留原有编译、纹理、深度、输入变化和源文件保护。输出完成后的取消不能撤销已发布文件，沿用 MCP 文档中的说明。
- 结果反馈使用实际编号文件名；应用后导出的 `sourcePath` 指向请求的新素材。合法黑色输出仍为 warning，不会被拦截。UI 视觉、按钮、文件打开及键盘操作仍留待用户验收。
- Automation broker 升级为 4，同步 MCP 工具参数、生成常量、接口锁及中英文文档。native RPC 5、offline API 1、preview ABI 3 保持不变；没有依赖或上游升级。
- `ExportWorkflowTests` 覆盖最后一次延迟编辑、错误提交、即时编辑与 MCP 顺序、待应用/非法配置、应用导出的互斥、失败回退、过期输入绑定、两种截图方法、同名文件竞争、取消和旧成品保护。既有 Core 全套继续通过。
- 新增测试专用 `OfflineReShade.CaptureContract.addon64`，只安装到测试效果目录，发布包不包含它。`run_export_workflow.py` 验证默认路径实际触发 `reshade_screenshot`，Direct PNG/预览不触发该事件；检查整幅输出像素、编号策略、尺寸应用、参数保留、过期输入拒绝和损坏输入回退。这证明所测截图事件契约，不代表所有第三方 Add-on 的完整兼容性。
- 首次全量构建 `tools/build_offline_release.ps1 -RunIntegrationTests -OutputDirectory build/refactor-step5` 通过。结果路径与来源提示修正后，最终执行 `-SkipBuild -RunIntegrationTests -OutputDirectory build/refactor-step5-verified`，全部通过；只复用本轮已重编译且未变更的 native 组件。
- 最终报告：`build/refactor-step5-verified/verification-20261103-044421.json`。包：`build/refactor-step5-verified/OfflineReShade-0.8.3-win-x64-20261103-044421`；731 个发布文件，ZIP SHA256：`42e6ab1ee1ceefafc29ba053e51c165fa91c5e194789247656d28442842b8942`。包含输出正确性、批缓存、真实 D3D/故障恢复、宿主生命周期、接口拒绝、CI 规则和发布完整性验证。
- 通过命令行启动隔离测试包 `OfflineReShade.RefactorExport044421`，会话 `f2200e7c79cf4310bf2d5fc10039a547`；`run_export_workflow.py`、`run_input_workflow.py`、`run_integration.py`、`run_output_correctness.py`、`run_d3d_recovery.py` 全部通过。证据在最终输出目录的对应 `attached-*` 子目录。按完整路径核对后终止测试 UI 36908 和宿主 21348，确认进程为零、测试包注销。
- 代码审查修复延迟更新覆盖新编辑、导出结果定位旧文件、来源标记旧素材及对话框构建异常未捕获。全程没有 computer use，未操作用户现有会话，未对外发布。

前三个结构边界及三个高优先级问题已完成主流程接入和自动化验证，达到计划的第一个实施交付节点；这不替代用户验收。

## 第六步验证

- `EffectEditHistory` 在共享操作队列上记录 FX 参数、复位、效果/全局开关、顺序和预处理修改。WinUI 与附着 MCP 共用历史，独立 MCP 维护自己的实例。只有成功应用的变化进入历史，失败的多参数请求恢复此前内容；恢复失败明确标记状态不可信，并禁用不安全的撤销和保存。保存基准按内容比较，撤销回已保存内容时恢复正确标记。
- `begin_edit` / `end_edit` 将一次连续操作合成一条记录，拖回原值不产生空记录；事务期间其他写入明确拒绝，取消可以释放遗留事务并保留已应用内容的撤销记录。普通切图和尺寸重建保留历史；外部 FX 或结构变化使不匹配的旧历史失效，并保留说明。
- WinUI 增加撤销、重做、参数复位、未保存标记、保存及另存入口。数值文本仅在真正修改后提交，最后一次文本编辑与滑块编辑共同进入后续操作屏障；非法数值不能让导出使用旧值冒充成功。提供 `Restore controls from renderer` 处理失败的待提交控件值。Ctrl+Z / Ctrl+Y / Ctrl+S 在文本编辑之外调用共享命令，文本框保留自身编辑行为；完整快捷键和布局整理仍属于第七步。
- 保存源预设、另存并绑定新源、导出独立副本分别实现。使用同目录临时文件验证后原子发布，保存失败不清除修改标记或损坏旧文件；另存不重启宿主。增加 `[OfflineReShade] EffectsEnabled` 元数据，明确加载预设时读取；普通重建继续保留当前全局开关。原生自动保存仍使用私有工作 INI。
- 加载预设、切换到其他预设的输入/监听、应用后导出及关闭窗口接入未保存规则。MCP 默认拒绝丢弃，显式支持 save/discard 及决策 revision；WinUI 提供保存/放弃/取消。关闭时在阻止新操作的边界再次核对 revision，避免弹窗期间的新修改被丢弃。不可读取或断连状态允许明确放弃以恢复，不能假称保存成功。最终释放不再额外保存无用快照，避免快照失败阻碍关闭。
- 历史读取和预设保存只等待 FX 状态，不依赖当前图片能够成功渲染，修复损坏的监听素材阻止用户保存参数或打开新图恢复的问题。截图和成品导出仍使用原有完整输入/编译/贴图/深度检查。
- Native 新增紧凑 `get_edit_state`。修复 RPC 及预设 INI 的六位小数截断，使小浮点值在撤销、保存和重建后准确往返；支持混合布尔向量，拒绝非有限/越界数值。修复删除一个预处理定义时误删后续定义的问题，恢复保留其他覆盖项。读取并恢复实际覆盖项，避免把默认值当成显式覆盖保存。
- 着色器声明为 `nosave` 的值作为只读信息呈现；数组、隐藏和 source 驱动参数不属于可编辑契约。第三方 Add-on 私有状态明确声明不在这套预设保存/撤销能力内，未承诺完整支持。原生 Add-on 兼容入口继续保留。
- Native RPC 升为 6、automation broker 升为 5，同步生成契约、接口锁及 MCP/中英文说明；offline API 1、preview ABI 3 与上游/依赖固定版本保持不变。
- 新增 `HistoryTests` 覆盖事务、互斥、撤销/重做、保存基准、无操作拖动、混合布尔向量、多参数失败回滚、取消回滚、状态不可信、源文件保护、另存、决策过期、损坏图片下保存及关闭前新修改保护。生命周期测试增加全局开关元数据、源路径重绑定和释放时快照写入不可用的场景。
- 新增 `run_edit_history.py` 并接入流水线，使用真实宿主/MCP 检查精确浮点和 INI 往返、布尔向量、拖动事务、像素结果、复位、另存保护、尺寸重建后历史、未保存拒绝和损坏预处理编译回滚。既有回归继续覆盖输出、缓存、D3D、截图事件和宿主生命周期。
- 首次全量构建遇到 MSVC D8040 子进程通信错误；使用 `_CL_=/MP1` 完成原生重编译。后续只读值限制改动单独重新编译 `OfflineReShadePrototype.vcxproj`。最终 `tools/build_offline_release.ps1 -SkipBuild -RunIntegrationTests -OutputDirectory build/refactor-step6-delivery` 全部通过；仅复用本轮已完成编译且未再修改的 C++ 产物，托管程序重新发布。
- 最终完整报告：`build/refactor-step6-delivery/verification-20261103-053500.json`。包：`build/refactor-step6-delivery/OfflineReShade-0.8.3-win-x64-20261103-053500`；731 文件，ZIP SHA256：`cccc3c6ec5422cd3ae87000183214a5294ec13ccff6e3d389cb7940c2afdc077`。应用代码在这次校验后没有修改。
- 经命令行启动隔离包 `OfflineReShade.RefactorHistory053500`，附着会话 `c5690b570876489094e964ad431dff82`。六组附着测试全部通过，证据分别为 `attached-edit_history`、`attached-export_workflow`、`attached-input_workflow-verified`、`attached-integration-verified`、`attached-output_correctness-verified`、`attached-d3d_recovery-verified` 目录及相应日志。
- 附着输入测试首次从仍在等待的新监听切换测试预设时，正确触发了旧会话的未保存拒绝；补齐该测试的显式 discard 后，附着输入回归通过，独立输入回归另在 `input-workflow-final` 通过。这是测试夹具决策的调整，没有改动已校验的应用二进制。
- 审查补齐文本提交屏障、取消/停止后的本地事务释放、只读能力提示、断连放弃路径和关闭时的状态核对。按完整路径核对后结束本轮 UI 27348 和宿主 39240，确认作用域内进程为零、测试包已注销。
- 全程没有 computer use，没有操作用户现有窗口，没有对外发布。实际拖动、快捷键、保存选择器、弹窗与关闭交互仍需用户验收，自动化测试不替代这部分结论。

## 第七步验证

- 将 MainWindow 的效果/原生 Add-on 显示、素材配置与画布分别移入 `EffectsPanel`、`InputPanel`、`PreviewCanvas`。各组件管理自己的控件、事件和显示资源释放，窗口保留会话入口、弹窗、全局快捷键及布局协调；继续复用 Core 的生命周期、编辑历史和操作屏障。
- 使用原生 SplitView：效果栏可以隐藏，并从标题菜单用滑块调整 280–640 DIP 宽度；空间不足时覆盖画布，宽窗口内联。图库默认收起，从主命令溢出菜单打开。素材展开区域整体限高并滚动，避免将画布推出窗口。初始窗口按 DPI 缩放且限制在显示器工作区内。
- Core `PreviewViewport` 定义 Fit 和真实 100%：一个渲染像素对应一个物理显示像素，允许小图在 100% 下小于适应窗口。滚轮围绕指针位置缩放，拖动和方向键平移；尺寸变化继续钳制图像可见范围。画布显示比例、渲染尺寸和预览尺寸，降采样 CPU 预览明确提示细节限制；不修改输出设置。
- 原生宿主新增临时 `set_preview_comparison`：GPU 共享纹理选择已应用的原始颜色纹理，CPU 预览选择同一输入像素。两种导出及预览截图仍读取效果结果。Core/MCP 的显示操作不提交待编辑控件、不修改 FX 开关、不进入历史；`previewOriginal` 同步到 WinUI，宿主重建后归零。
- 效果栏展开状态、宽度及 System/Light/Dark 主题属于应用偏好；旧配置保留原值并获得默认布局。动态文本和分组改用主题资源/原生 Expander，提供高对比度背景资源。第三方原生 Add-on 图像仍受其自身主题能力限制。
- 加入快捷键说明、打开/导出/保存/撤销/搜索/Fit/100%/对比入口、画布键盘平移，以及效果排序菜单作为拖拽的键盘替代。文本输入和原生 Add-on 输入保留自己的按键行为。补充控件名称和 ID，MappedSlider 暴露真实范围和值的读屏接口并消除内部归一化滑块的重复焦点。未调用 UIAutomation 或执行实际键盘模拟。
- Native RPC 升为 7、automation broker 升为 6，生成常量、接口锁及 MCP/中英文文档同步；offline API 1、preview ABI 3、上游与依赖版本保持固定。
- `PreviewTests` 覆盖六种 DPI、极小和极端长宽比、100% 物理像素映射、指针缩放、平移/尺寸变化、自适应侧栏、偏好迁移/往返及比较操作不触发编辑提交。全部 Core 测试通过。
- 新增 `run_preview_workflow.py`；扩展现有无窗口 D3D 测试程序，读取真实宿主共享纹理。验证原图/效果图切换在 GPU 与 1920×1080 CPU 降采样预览上正确；2560×1440 的两种成品导出、1280×720 预览截图仍为效果图；编辑状态、历史、修改标记和源预设保持不变，重建后对比复位且撤销可用。
- 首次全量构建已成功重编译 ReShade、原生宿主和预览桥，随后测试采样器因缺少 `<string>` 头文件而失败；补齐测试头文件后，修正旧集成夹具写死的协议号，并按预览截图的既有尺寸和历史查询的独立操作 ID 校准新增断言。实际产品 C++ 在全量编译后没有修改。
- 最终执行 `tools/build_offline_release.ps1 -SkipBuild -RunIntegrationTests -OutputDirectory build/refactor-step7-delivery`，21 项检查全部通过；只复用本轮已重编译且未变更的产品 C++，重新发布托管程序并重建测试程序。构建前后源码快照一致，包含全部既有输出、缓存、截图事件、D3D/故障恢复、宿主生命周期、兼容拒绝、CI 与完整性验证。
- 最终报告：`build/refactor-step7-delivery/verification-20261103-060144.json`。包：`build/refactor-step7-delivery/OfflineReShade-0.8.3-win-x64-20261103-060144`；734 文件，ZIP SHA256：`06bb9d83ecb8d7cc4ea805dfa4b3de95258a5cdd9b1a3cdcb710f00ee3d81b4d`。
- 通过命令行启动隔离包 `OfflineReShade.RefactorCanvas055948`，会话 `e558edce295f4e3588b555b1c9992fe6`。核对其核心二进制和三个组件 XBF 与最终交付包哈希相同；七组附着测试 `preview_workflow`、`edit_history`、`export_workflow`、`input_workflow`、`integration`、`output_correctness`、`d3d_recovery` 全部通过，证据在最终输出目录的 `attached-*` 文件夹及日志。
- 审查补齐隐藏效果栏时的焦点空值保护、GPU 模式切换后的恢复预算、Add-on DPI 通知以及 MCP 对比状态同步。按完整路径核对后终止本轮 UI 22028 和宿主 34156，确认作用域内进程为零、测试包已注销；没有操作用户现有会话或对外发布。
- 960×720 DIP 及以上窗口、100–200% 桌面 DPI 的实际布局，按钮/拖动/快捷键、焦点、主题/高对比度、读屏及窗口关闭仍保留用户验收。全程没有 computer use，自动化几何与像素测试不代替这些结论。

下一步为第八步：批处理范围选择、预检、逐项结果、重试/恢复、固定任务效果与输出设置、结束后恢复完整编辑上下文，以及 Add-on 常用控制与兼容/诊断分层。特别需要修复跨预设批处理只恢复原输入、未恢复原未保存效果上下文的问题。

## 完成判定

不能以第一步测试通过代替全部目标完成。每个待实施项都需要实际接入调用路径及相应测试；最终补充单图/批处理输出正确性、D3D、独立和附着 MCP 与发布校验的证据，并明确保留用户验收项。
