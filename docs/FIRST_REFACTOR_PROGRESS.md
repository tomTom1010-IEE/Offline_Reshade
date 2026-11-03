# 第一次重构执行记录

目标来源：[完整执行计划](FIRST_REFACTOR_PLAN.md)。实施范围是第 1–8 步及对应自动化验证，用户验收另行进行。全程禁止 computer use；驱动变更测试继续延期。

分支：`codex/first-architecture-refactor`。起点 `98de8c10` 保存已有 0.8.3 维护成果；`.codex/`、`artifacts/` 和 `hello_world.py` 未纳入该基线。

| 步骤 | 状态 | 当前证据与剩余工作 |
|---|---|---|
| 1 状态归属与配置迁移 | 实现及自动化验证完成 | Core 分离 EditorDocument、ApplicationPreferences 和 EditingSession；WinUI 绑定适配；配置 v2 迁移、备份、原子保存；待应用状态与后续共享运行路径、操作结果接通 |
| 2 会话生命周期 | 实现及自动化验证完成 | Core RenderSessionController 统一启动、切图、重载、恢复、取消和释放；WinUI/独立 MCP 复用，附着 MCP 回归通过；真实进程测试验证参数保留和宿主释放 |
| 3 操作与状态反馈 | 实现及自动化验证完成 | Core 操作队列、结构化结果、取消和错误恢复；WinUI 可见反馈及空状态；第四步监听和后续导出、历史、批处理均已接入，最终 broker 7 |
| 4 素材入口 | 实现及自动化验证完成 | 主界面打开图片/监听游戏输出，工作区管理素材、深度和预设；缺失/损坏文件等待与恢复、失败保留上下文；独立和附着 MCP 通过同一真实渲染回归 |
| 5 统一导出 | 实现及自动化验证完成 | 单图统一导出面板；显式应用后导出、参数提交屏障、原子发布及冲突策略；真实截图 Add-on 事件与独立/附着 MCP 回归通过 |
| 6 编辑历史 | 实现及自动化验证完成 | Core 统一 FX 事务、撤销/重做/复位、保存基准、另存与未保存决策；精确浮点和布尔向量往返；独立及附着 MCP、失败回滚及发布验证通过，窗口交互仍留给用户验收 |
| 7 画布与布局 | 实现及自动化验证完成 | 效果/素材/画布组件、可调与自适应面板、物理像素缩放、预览对比、主题及键盘/无障碍入口；真实 GPU/CPU 和独立/附着 MCP 回归通过，视觉及操作体验仍待用户验收 |
| 8 批处理与 Add-on | 实现及自动化验证完成 | 固定任务预设、完整上下文恢复、预检和逐项结果、持久化重试/续跑、Add-on 分层；Core、真实批处理、八组附着回归和最终整包 22 项检查通过 |

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

## 第八步实施与验证

- Core 增加独立的 `RenderSessionContext`，保留原输入、私有 FX 快照、全局开关、预览对比、未应用草稿和连接状态。批处理切图直接经过会话控制器，不改写 WinUI 草稿、图库选择及应用偏好；结束、失败、取消和停止均先恢复并核验原状态，恢复失败明确报错、使不可信历史失效，并保留恢复用私有副本。
- `AutomationEngine.Batch` 分离任务逻辑。启动冻结每个预设及输出规格，将任务与 INI 副本写入 `.OfflineReShade.jobs/<id>/job.json`。共享预检保留无效项；逐项处理，单项失败不停止后续输入；新导出、校验后复用、主动跳过、失败及取消分别记录。临时输出在输入和依赖复核后原子发布，不覆盖输入或效果依赖。
- 持久化结果包含路径、错误、警告、尝试次数及每项输入指纹/输出哈希；恢复读取冻结设置并复核输出指纹及哈希，失败项可以单独重试。源预设修改不改变既有任务；效果依赖改变要求建立新任务。不透明 Add-on 状态不承诺快照恢复，也不允许跨次缓存复用。
- WinUI 合并旧 Batch Save/Batch Shot，提供全部/选中范围、输出目录、后缀、冲突策略及高级选项；路径检查与实际渲染检查明确区分。结果面板显示逐项状态、定位、重试和恢复入口；刷新图库后更新命令可用性。Add-on 常用 WinUI 控件前置，原生兼容界面、诊断和捕获调试默认折叠。
- MCP 增加 `preflight_batch`、`resume_batch`、`retry_failed_batch`，扩展 `start_batch` 的冲突策略和决策版本检查、`get_job` 的逐项结果；broker 升为 7，Native RPC 7、offline API 1、preview ABI 3 和固定上游/依赖不变。
- Core 全套通过。新增 `BatchTests` 覆盖跨预设恢复、未保存参数/历史/草稿、持久化重试续跑、篡改输出、冲突策略、合法黑图警告、依赖变化、取消、停止态恢复及快照写入失败；`SessionTests` 增加源预设删除后的恢复与恢复失败重试。
- 新增真实宿主 `run_batch_workflow.py` 并纳入构建流水线。首轮新增夹具误用了不存在的 MCP 工具名 `get_runtime_info`，改为 `get_status` 后跨预设/混合尺寸、逐项失败、冻结设置、续跑、重试和恢复验证通过。WinUI 中使用当前固定 SDK 支持的 Expanding/Collapsed 事件，并延后更新原生覆盖层显示。
- 完整输出正确性回归发现：ReShade 对编译失败效果导出的新快照会遗漏其启用意图，批处理重载后可能导出无效果原图。本轮修复 offline 模式保存：保留无法编译/创建的必需 technique 及其预设内容，同时保存已编译但被禁用效果的参数。修复后的两种导出、批处理及恢复后再次导出均明确拒绝损坏效果；黑图与明确禁用效果保持原语义。相关真实回归增加恢复后拒绝及禁用效果参数往返检查。
- 额外跨进程续跑检查发现公共检查点被其他任务更新后会丢失旧任务的完成证据；已将指纹与输出哈希保存到各自任务文件，并增加删除公共缓存、重启 MCP 后仍复用完整结果的回归。
- ReShade、宿主和预览桥已在首轮全量重编译；新增快照修复另经固定工具链重编译 `ReShade.vcxproj`，日志为 `build/refactor-step8/native-snapshot-fix-build.log`。单独编译首次遗漏 SolutionDir，补齐后通过。后续 `-SkipBuild` 仅复用上述未再修改的产品 C++，重新发布托管应用。
- `build/refactor-final/verification-20261103-065345.json` 的 22 项检查通过。命令行启动隔离包 `OfflineReShade.RefactorBatch065345`，会话 `1e24bbe30b3f43a2bb357aaffc0ea51f`；`batch_workflow`、`preview_workflow`、`edit_history`、`export_workflow`、`input_workflow`、`integration`、`output_correctness`、`d3d_recovery` 八组附着测试全部通过。日志及工作目录位于 `build/refactor-final/attached-*`。包括删除公共缓存、重新启动 MCP 客户端后仍复用全部任务结果。
- 按完整路径核对并结束隔离 UI 30528 及宿主 35944，确认作用域内进程为零、测试 AppX 已注销；未操作用户现有会话。故障注入由测试专用 marker 触发，没有真实更新显卡驱动。
- 文档复核修正旧 MCP 概览中“每批只能使用同一预设”的过时描述，并给原架构/交互建议增加历史快照说明。随后执行 `tools/build_offline_release.ps1 -SkipBuild -RunIntegrationTests -OutputDirectory build/refactor-delivery`，最终 22 项检查再次全部通过。
- 最终报告：`build/refactor-delivery/verification-20261103-065926.json`。交付包：`build/refactor-delivery/OfflineReShade-0.8.3-win-x64-20261103-065926.zip`，736 个发布文件，SHA256：`df76bad8895231ab10875c221097cfe112308916354496115087fbf961205793`。构建前后源码快照均为 `cb77e73206d1b2e7c006e49b2af68a144312d5d1dd4403ac63515a8e0f4ec4ea`；ZIP 校验通过，493 个 EXE/DLL/XBF/PRI 与八组附着测试所用版本逐一哈希相同。
- 总体验证证据保存在 `build/refactor-delivery/final-artifact-audit.json`。完成整包校验后只补充本执行记录，未再改动产品实现。包内 provenance 如实记录构建时的本地源码快照；此包用于本地用户验收，未推送或对外发布。
- 按 WinUI 代码审查规则检查本轮 MVVM 职责、绑定更新、事件释放、主题、命令错误、路径保护和列表虚拟化，未发现阻塞项。新增面板沿用项目现有 MVVM 和英文文案；全界面本地化不是本轮目标。

## 完成判定

第 1–8 步实现及其自动化验证已完成，三个结构边界、三个高优先级问题和三个中优先级问题均有实际调用路径及验证依据：

| 计划要求 | 实施与验证依据 |
|---|---|
| B2 状态归属、草稿/已应用/已保存分离 | EditingSession、EditorDocument、ApplicationPreferences；WorkspaceTests、SessionTests 与失败回退回归 |
| B1 会话生命周期及三入口一致性 | RenderSessionController；真实进程生命周期、独立/附着 MCP、尺寸重建与故障恢复测试 |
| B3 共享操作、互斥、取消和反馈 | OperationCoordinator、ApplicationActivity；OperationTests、真实并发查询/取消及失败状态测试 |
| 高：主工作流程入口 | 工作区打开素材/监听；损坏输入回退、等待与更新、停止监听的独立/附着回归 |
| 高：统一导出 | 单一导出面板、显式应用后导出、编辑提交屏障；截图事件、冲突策略、原子发布和成品像素测试 |
| 高：状态与错误反馈 | 空状态与任务结果呈现；损坏效果/贴图/深度明确失败，合法黑色作品成功并给出警告 |
| 中：编辑及保存语义 | FX 事务历史、撤销/重做/复位、未保存决策及安全保存；精确参数往返、失败回滚和重建保留测试 |
| 中：画布与布局 | 三个面板拆分、溢出/折叠/可调布局、100% 与 Fit、预览对比；DPI 几何及 GPU/CPU 像素回归 |
| 中：批处理与 Add-on | 任务预检、逐项结果、冻结设置、持久化恢复、原编辑上下文恢复；真实续跑/缓存篡改/取消回归，Add-on 常用与兼容/诊断入口分层 |
| 整体回归与交付 | 最终流水线 22 项检查、八组 WinUI 附着回归、接口及上游锁定校验、ZIP 完整性与二进制一致性核验 |

各阶段分别形成 Git 提交；第八步将批处理、完整上下文恢复及本轮发现的快照正确性修复作为独立提交。全程没有 computer use、UIAutomation、截图验收或模拟键鼠操作，没有升级上游或依赖，没有对外发布。

仍保留给用户验收：首次使用的理解成本、960×720 DIP 及以上窗口和 100–200% DPI 下的实际布局、滑块/快捷键/焦点、主题与读屏、文件选择器、未保存提示和窗口关闭交互，以及具体第三方 Add-on 的实际操作。不透明 Add-on 私有状态仍按接口能力处理，不能承诺完整撤销/快照。真实 NVIDIA 驱动更新后的兼容测试继续按既定安排延期；故障注入通过不替代该测试。

## 截图反馈后的界面修正（2026-11-03）

用户随后明确要求截图并修复发现的界面问题，本轮因此使用 computer-use 查看和操作独立测试实例；前述重构阶段未使用 computer use 的记录保持原意。

- 素材区折叠为一行摘要，展示文件名、深度关联、预设、监听及修改标记；详细路径和输入配置移入展开内容。历史警告仍可见，空 FPS/警告和闲置编译进度不再占位。
- 空画布只保留标题和顶部操作说明，打开/监听/取消统一使用顶部已有入口。无有效图像时收起画布工具和比例，并阻止无图状态下的缩放操作；收到有效图像后恢复正常比例和分辨率信息，不以像素亮度判断有效性。
- 固定工具链 Release/x64 发布与包完整性校验通过：`build/ui-polish/verification-20261103-152004.json`。本轮没有重复运行全部重构回归；相关的 WinUI 附着 `run_preview_workflow.py` 通过，记录为 `build/ui-polish/attached-preview.log`，覆盖预览对比、导出像素、未保存标记、重建及撤销历史。
- 实窗检查通过：初始空状态不再出现 37600% 或重复打开按钮；素材展开内容可访问；加载测试图后摘要、比例与尺寸正确，Ctrl+1 显示 100%；Ctrl+Y/Ctrl+Z 正确添加/清除摘要中的未保存标记。测试后正常关闭窗口，确认该实例进程和注册均已释放。此检查不替代完整 DPI/主题/读屏验收。
- 更新包：`build/ui-polish/OfflineReShade-0.8.3-win-x64-20261103-152004.zip`，SHA256 `9b23e588714da3759dc4de7fc77f24a06eb0d8b558af70e2327f5c42e2141b16`。未更改原生渲染、输出或 MCP 契约，未对外发布。

## 再次实际 UI 检测与截图归档（2026-11-03）

- 按用户要求使用实际窗口、鼠标和键盘检查。产品提交为 `f69e8382`，使用上述发布包的两个隔离副本；每轮开始时均无用户设置文件。第一轮测试无图启动；第二轮从全新配置启动后，通过系统选择器首次载入合成图片。测试副本单独加入 `Audit.fx`，未修改产品二进制。
- 本地截图和可浏览报告：[UI 检测档案](../artifacts/ui-audit-20261103/index.html)。共 52 个捕获阶段、60 张 PNG（含独立弹层）；覆盖空界面、输入/深度/尺寸配置、首次载图、FX、单图导出、图库、批处理及恢复、Add-on、设置、未保存提示、保存选择器、错误、监听入口、日志、预览和快捷键。结构化结论位于同目录 `report.json`，过程截图按实际内容注明，未确认的错误详情弹层没有标为通过。
- 单图导出成功；批量 3 个导出、0 个失败，纯黑图只警告；恢复同一任务后 3 个复用、0 个失败。四个成品均为 960×640，默认 Exposure=1 时与对应输入 RGB 像素完全一致，没有 UI 或原生日志混入本轮成品。损坏图片明确失败并保留此前有效输入；保存预设与取消关闭提示实际操作通过。
- 视觉验收未全通过：批处理展开校验详情后底部按钮裁切；设置缺少明确返回入口，输出路径变更触发“应用后导出”文案，已完成批任务仍占据顶部区域。首轮干净启动两次捕获到黑色客户端，按 Tab 后显示，第二轮未复现，尚未区分绘制、激活和捕获时机；不能等同成品黑屏。有图轮调试日志含 first-chance CLR/C++/访问冲突记录，无堆栈、未观察到未处理崩溃，需单独定位，不能直接归因。
- 测试后正常关闭隔离实例，确认作用域内进程与 AppX 注册为零；原 `settings.json` 已恢复且 SHA256 与备份一致。原始日志、恢复校验、测试素材和输出随本地档案保存。本轮未修改应用代码、未对外发布；完整 DPI/主题/读屏、真实游戏监听、第三方 Add-on 和 NVIDIA 驱动更新测试仍未覆盖。

## Marty FX / Add-on 接口检查（2026-11-03）

- 按用户要求全程不使用 computer use；通过隐藏原生宿主的语义 RPC、MCP `tools/list`、日志和代码检查，没有启动 WinUI、模拟键鼠或进行窗口截图。使用本机 `OfflineReShade-win-x64-20260711-114311` 内 Marty 套件的隔离副本，以及最新 `build/ui-layout/OfflineReShade-0.8.3-win-x64-20261103-220726` 原生二进制。完整结果和复现脚本见 [Marty 接口检查档案](../artifacts/marty-interface-audit-20261103/index.html)。
- 18 个 FX 编译成功，列出 21 个 technique、199 个公开参数，8 类代表参数写入/读回/恢复通过；4 个 Add-on 均加载。但常用控件不能独立覆盖 Marty：未激活原生面板时仅捕获说明/许可证，离屏原生面板激活后才出现业务控件；ReLight、ReGrade+、Parallax DOF 均存在自绘回退。ReLight 的 3 类控件写回及恢复通过，LUT Manager 在提供测试 LUT 后选择与导出像素变化通过；这些结果不等于完整独立界面已通过。
- 明确复现 ReLight 折叠分组后 6 个控件 ID 被其他参数复用，以及不存在的控件 ID 仍返回成功。另发现 ReGrade+ 的 `combo_begin` 无输入注入且被 WinUI 过滤，FX button/color 的控件语义不完整，兼容性状态没有体现 Parallax DOF 的 IGCS 外部依赖。实际 MCP 列出 33 个工具，尚无 Add-on 业务工具。
- 建议先修稳定控件身份、写入确认和分层能力状态，再补 FX/通用控件语义，最后按受支持的业务接口建立独立 Add-on 适配并接入 MCP。现有 DLL 未提供通用业务参数的命名导出，不能承诺仅修改 WinUI 即可替代全部原生自绘界面。原始 53 个文件哈希不变，测试二进制与最新包一致。本轮仅检查和记录，既有 UI 修改保留；没有修复、重新构建或发布产品，也没有进行视觉验收或 IGCS 游戏会话测试。

## Add-on 修复与跨设备交付（2026-11-03）

- 先提交已验收的布局和自动尺寸修改，`f2a6e295` 已推送 `codex/first-architecture-refactor`。制作个人迁移包，包含应用、Marty 18 个 FX 及依赖、4 款 Add-on、原 Marty 预设/颜色专用预设/测试预设和 5 张示例 PNG（另含一份合成配对深度）。设置由解压目录重新生成；显式导入时备份目标设备原设置。本机原设置未改，迁移目录实测 4 个 Add-on 加载和 CLI 导出通过。
- 第一阶段：ImGui 控件 ID 纳入会话和原生 ID 栈，校验目标、类型、数量、有限值、范围及禁用状态；回执区分 queued/applied/rejected/expired/cancelled。Core 等待读回并清理取消/超时的排队请求。Marty 实测确认原来的 6 个串位消失，隐藏/不存在 ID 被拒绝；Parallax DOF 显示 IGCS 依赖。
- 第二阶段：FX 按钮改为瞬时动作，颜色参数增加颜色选择器并保留精确数值入口，传递提示/说明/单位；修复 BeginCombo 输入和解析、窗口分组、禁用状态及折叠头重复分支。ReGrade+ Scopes 已通过接口展开并列出选项。没有 computer use 或视觉验收。
- 第三阶段：实现独立服务 ABI v1、WinUI 独立服务区，以及 MCP 的能力查询、独立参数写入、声明动作和 FX 按钮工具。测试 Add-on 没有 ImGui 界面，实际 MCP → 原生 → GPU 导出像素验证通过。Marty 的 LUT Manager/ReGrade+ 公开 companion FX 参数提供限定适配，写回与 FX 撤销通过；私有 LUT 选择、灯光对象、调色曲线仍需作者接口或独立实现，未宣称完成这些私有功能。契约见 [ADDON_SERVICES.md](ADDON_SERVICES.md)。
- RPC/broker 升为 8，固定新的 SDK 头文件哈希；上游、驱动和依赖版本不变。全量发布回归 24 项通过：`build/addon-services-delivery/verification-20261103-231255.json`。其后仅修复派发时取消的清理边界，并让有原生服务的提供方优先于 companion 适配；核心测试（含派发取消）、实际独立服务/MCP/GPU 回归和 Marty 写回/撤销再次通过。
- 最终程序包：`build/addon-services-handoff/OfflineReShade-0.8.3-win-x64-20261103-231615.zip`，SHA256 `d749c95cc627c16196ddc65afeb8d614616a77aefdfcb1cb12aa2ab08f38dbec`。MSVC 全量重编译曾遇 D8040，使用单编译进程后成功；后续 SkipBuild 只复用已编译且未再修改的 C++。一次构建因源码在验证期间变化被来源校验拒绝，稳定源码重跑后通过。所有失败记录保留，没有把失败构建作为交付结果。

## Marty 私有接口进一步接入（2026-11-03）

- 继续遵守不使用 computer use；本轮也没有启用原生面板、转录 ImGui 控件、发送键鼠或启动 WinUI。通过本机 DLL/FX、官方说明、隔离预设和实际 MCP/GPU 输出检查。发现 ReLight 灯光及 ReGrade+ 曲线有私有预设编码，但仅修改编码不会重建内部资源；完整效果重载后才产生正确像素变化。
- 增加严格 DLL/FX SHA-256 版本门槛的独立预设适配。MCP 原有 `list_addon_services` / `invoke_addon_action` 可查询和调用 `set_curve`（RGB/R/G/B、2–10 节点）、`set_light`（64 个稳定槽位、球形光、色温/色调）和 `select_lut_from_preset`（已保存 atlas 引用、tile 0、隔离资源目录）。真正实现服务 ABI 的 Add-on 仍优先；未知版本保留公开 FX 控件并明确禁用私有写入。没有修改/复制厂商算法到公共代码，也没有发布私有 FX/DLL。
- LUT 异常测试发现：未知 atlas ID 保留在预设中，插件却静默回退中性 LUT。因此读回相同 ID 不足以确认选择；适配额外要求本轮效果重载后，固定版本插件日志明确确认资源已找到。日志缺失、截断、回退或读回不一致都失败。取消/失败会恢复预设与 LUT 目录；恢复也失败时保留恢复文件并阻止导出，直到显式加载已知预设。
- 单元测试通过：曲线量化碰撞/越界、无关设置保留、确认写入、拒写恢复、派发取消、恢复失败导出保护，以及有效/缺失/回退 LUT 回执。实际 MCP 测试 `tests/run_marty_private_services.py` 通过：总曲线和三个单通道的像素变化/复位，灯光增加/关闭，Neutral/Warm LUT 切换，未知 atlas 失败后的画面恢复，以及 FX 版本不匹配拒绝。结果保存在 `build/marty-private-probe/verification/result.json`，原始 Marty 文件哈希未变。
- 测试中修复了切换预设后过早判定 companion schema 的等待问题；测试脚本曾误用 native 的 `set_preset` 工具名，已改为 MCP 的 `load_preset` 并重跑。未知 atlas 曾在仅检查字段时误报成功，增加资源加载回执后异常回归通过。这些失败没有被视为验收成功。
- 尚不覆盖任意新 LUT 文件名到内部 atlas ID 的解析、收藏、非零 atlas tile、其他灯光模型/RGB 编辑、黑白/色相曲线、色轮及图形示波器；具体缺少的契约与操作限制见 [ADDON_SERVICES.md](ADDON_SERVICES.md)。私有动作不进入 FX 撤销和未保存标记；需显式导出预设副本，LUT 文件同时携带。本次补齐独立接口与 MCP，没有声称实现可视化曲线编辑器或灯光 gizmo。
- 最终 Core 测试全部通过，WinUI Release/x64 编译通过；现有无 ImGui 测试 Add-on 的 MCP/参数/动作/GPU 输出回归再次通过。按 WinUI 代码审查约定检查本次改动：业务处理留在 Core，界面仅展示不可用原因和带参数动作的 MCP 入口说明，避免空参数按钮；没有运行 UI 自动化、视觉或新驱动测试。原生 C++、上游、接口版本和依赖均未改变。
