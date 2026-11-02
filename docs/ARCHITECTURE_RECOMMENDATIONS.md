# 架构修改意见

记录日期：2026-11-03。基于 Offline ReShade 0.8.3 的源码与实际窗口审查。本文保留重构前的建议快照；当前实施与验证状态见[第一次重构执行记录](FIRST_REFACTOR_PROGRESS.md)，不以本文的问题描述判断现版本状态。

目标是统一会话生命周期、编辑状态和操作结果，使 WinUI 与 MCP 的行为保持一致，并降低后续修改界面的成本。交互问题单独记录在[目前最需要改善的地方](UX_IMPROVEMENTS.md)。已有维护成果见[稳定性修复计划](STABILITY_PLAN.md)和[维护基线](MAINTENANCE.md)。

## 保留的基础

- 保留独立的原生 D3D/ReShade 宿主及进程隔离。
- 保留 Core 中共享的导出、批处理和输入校验能力。
- 保留 RPC、接口版本校验、诊断能力及 MCP 接入。
- 保留预览显示与最终输出分辨率的分离、GPU/CPU 预览路径。
- 保留现有输出正确性保障：损坏效果或异常输入明确失败，合法全黑作品只提示，批处理严格校验缓存匹配。

这些调整可以逐步完成，无需整体重写或更换 UI 框架。驱动变更测试仍等待本机 NVIDIA 驱动更新，本轮不扩展该方向工作。

## 一 统一会话生命周期

### 当前问题

WinUI 的 `MainWindowViewModel.OpenAutomationInputAsync`、`StartPreviewCoreAsync` 与 Core 的 `NativeSession.OpenAsync` 分别承担启动、连接、切换输入、重启和设备恢复的一部分逻辑。两条路径有重复判断，以后修复一侧容易遗漏另一侧。

代码入口：

- [MainWindowViewModel.cs](../OfflineReShadeWinUI/ViewModels/MainWindowViewModel.cs)
- [NativeSession.cs](../OfflineReShade.Core/NativeSession.cs)
- [PrototypeProcessService.cs](../OfflineReShadeWinUI/Services/PrototypeProcessService.cs)

### 修改建议

在 Core 中统一会话控制器，负责进程生命周期、连接与兼容性检查、输入切换、重载、恢复预算、取消及资源释放。WinUI 管理显示表面、焦点和用户交互；独立 MCP 复用相同的会话规则。

显式记录当前已应用的输入及其版本，区分用户正在修改的配置与宿主实际使用的配置。渲染、编译和导出任务分别建模，避免单一状态字段无法表达并行存在的状态。

### 验收标准

- 同一输入切换、重载与故障恢复场景，通过 WinUI 和独立 MCP 执行具有一致结果。
- 恢复次数、取消行为和资源释放规则只有一份业务实现。
- 状态能准确指向实际生效的输入，无法把旧画面解释为新输入的成功结果。

## 二 分开当前作品与应用偏好

### 当前问题

当前图片、深度、预设、输出尺寸与应用配置混在 Settings 中。设置自动保存，但部分修改需要重载或重建才会应用，界面难以说明“已保存”和“已生效”的区别。当前尚缺完整的编辑历史与预设未保存状态。

代码入口：[SettingsViewModel.cs](../OfflineReShadeWinUI/ViewModels/SettingsViewModel.cs)及 `MainWindowViewModel.OnSettingsPropertyChanged`。

### 修改建议

- 编辑会话保存当前素材及深度关联、效果链、参数、预设快照、输出规格、未保存标记及历史记录。
- 应用偏好保存默认目录、界面偏好、预览传输偏好及自动化配置。
- 参数即时预览；需要重新加载的配置先验证，再明确应用，并显示待应用状态。
- 支持撤销、重做、保存及另存预设；一次滑块拖动合并为一次历史操作。
- 明确切换素材、预设和关闭窗口时如何保留或处理未保存修改。

第三方 Add-on 的不透明内部状态按其接口能力处理，不承诺所有原生插件操作都能撤销。

### 验收标准

- 用户能区分当前作品修改、已保存预设和应用默认值。
- 切图、重建会话或通过 MCP 改参数后，界面状态保持同步。
- 一次连续拖动可以一次撤销；保存预设后正确清除对应的未保存标记。
- 非法尺寸等输入就地报错，不静默回退为默认值。

## 三 统一操作与错误反馈

### 当前问题

Core 已共享导出和批处理流程，但部分 WinUI 参数操作直接调用 RPC。忙碌状态、参数刷新和错误呈现分散在多个层次。通用 `AsyncRelayCommand` 捕获异常后仅写入调试输出，而其他路径使用日志或对话框，用户获得的反馈不一致。

代码入口：[AutomationEngine.cs](../OfflineReShade.Core/AutomationEngine.cs)、[AsyncRelayCommand.cs](../OfflineReShadeWinUI/Mvvm/AsyncRelayCommand.cs)。

### 修改建议

让 WinUI 与 MCP 的参数修改、预设、输入切换和导出通过同一操作层，统一编辑互斥、取消、结构化错误、历史记录和状态变更通知。UI 根据结果展示原因、恢复操作与详细日志，MCP 返回相应的结构化结果。

逐步使用明确的请求和结果类型，减少界面散落的 RPC 方法字符串与 JSON 字段解析；第三方效果及 Add-on 元数据仍保留必要的动态扩展能力。

### 验收标准

- 保存预设、重载、切图和导出失败均有明确可见的反馈。
- 相同操作通过 WinUI 和 MCP 执行遵循一致的校验及互斥规则。
- 正常取消、警告和真正失败能够区分；全黑提示保持警告语义。

## 四 按职责拆分界面与原生宿主

主窗口目前集中承担动态效果控件、Add-on 控件、预览呈现、输入事件和错误显示。建议在统一状态和操作层之后，逐步拆成效果面板、预览画布、素材列表、导出任务面板及 Add-on 适配组件。

原生宿主后续可按控制协议、输入资源、导出和预览传输拆分模块。文件大小本身不是重写理由，应以职责边界和可验证行为为依据。保留原生 Add-on 界面的兼容入口。

代码入口：[MainWindow.xaml.cs](../OfflineReShadeWinUI/Views/MainWindow.xaml.cs)、[offline_reshade_prototype.cpp](../tools/offline_reshade_prototype.cpp)。

## 实施顺序与回归要求

1. 为打开素材到导出的主流程统一状态、错误结果和最小会话控制能力，配合交互文档中的高优先级问题实施。
2. 分离编辑会话与应用偏好，接入预设修改状态及撤销、重做，然后按职责迁移界面组件。
3. 完善批处理任务呈现、Add-on 适配和诊断；原生宿主模块拆分按实际维护需求推进。

每次迁移都保留现有接口契约和输出正确性回归。涉及契约变化时按维护基线同步版本及兼容性检查。重点验证 WinUI、附着 MCP、独立 MCP 的相同操作，以及取消、恢复、批处理续跑和关闭释放；不以界面重排代替功能回归。
