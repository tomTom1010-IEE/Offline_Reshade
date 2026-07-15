# Offline ReShade 中文使用说明

<p align="center">
  <img src="offlineReshadelogo.png" alt="Offline ReShade logo" width="480">
</p>

> 这是使用者说明，只覆盖安装、导出、客户端使用和排错。开发、构建和架构细节请看英文 README。

Offline ReShade 是一个 Windows 工具，用于把 ReShade FX 效果应用到已经导出的静态图片或视频帧序列。它面向 Koikatu (KK) 和 Koikatsu Sunshine (KKS) 的离线截图流程：

- 游戏端插件导出 color 和 depth 文件；
- `OfflineReShadeWinUI.exe` 提供编辑界面；
- `OfflineReShadePrototype.exe` 承载全分辨率 D3D11 ReShade runtime；
- UI 中显示缩放预览，截图和批处理输出使用全分辨率 runtime。

发布包不会包含第三方 shader 或 add-on。发布包提供空的 `Effects`、`Textures` 和 `Addons` 文件夹，用户可以直接按默认相对路径安装自己的资源。

## 快速开始

1. 安装与你的游戏匹配的 Offline ReShade 游戏端插件。
2. 把 Offline ReShade 客户端发布包解压到游戏目录之外的任意位置。
3. 运行 `OfflineReShadeWinUI.exe`。
4. 打开 `Settings`。
5. 选择 `Game`：`KKS` 或 `KK`。
6. 把 `.fx` / `.fxh` 放入 `Effects`，把效果需要的图片资源放入 `Textures`，把 `.addon64` 放入 `Addons`。也可以在 Settings 中选择外部 shader 文件夹。
7. 使用 `Real Time` 或 `Gallery` 模式。

干净的发布包应该只包含程序二进制和 runtime DLL，不应包含：

- `.fx` / `.fxh` shader 文件；
- 第三方 `.addon64` DLL；
- 付费或私有 ReShade preset；
- 本地用户设置；
- ReShade 日志；
- PDB/import-library 等开发文件。

如果你下载到的包里包含这些内容，说明它可能不是可靠来源的发布包，建议删除后从 GitHub 重新下载。

## 必需的游戏端插件

Offline ReShade 客户端本身不会直接捕获游戏画面。游戏端必须先导出匹配的 color 和 depth 文件。

请先为对应游戏安装 BepInEx 5 和常规 BepisPlugins 依赖，然后把匹配的 Offline ReShade 插件 DLL 放入 `BepInEx\plugins`。

完整的静态截图和 VideoExport 工作流需要以下三类 DLL：

1. Screencap/ScreenshotManager：
   - KK：`Screencap.dll`
   - KKS：`KKS_Screencap.dll`
   - KK 有两个 Screencap 发布变体：
     - `KK Stable Path`：较轻量的 D3D11 bridge 路径，建议优先尝试。
     - `KK Compatibility Bridge`：会在多次 capture 之间重新确认并修复 D3D11 context hook。如果 stable 包能正常加载但无法生成 `depthoutput.rfloat`、只有第一张图有深度，或 VideoExport 偶发缺 depth，请换用这个版本。
   - 不要混用两个 KK 包中的 DLL。也就是说，不要把 stable 的 `Screencap.dll` 搭配 compatibility 的 bridge DLL，反过来也一样。请安装同一个 release package 里的匹配组合。
2. KK native depth bridge：
   - `OfflineDepthD3D11Bridge.dll`
   - KK 高质量 D3D11 device depth 必需。
   - 把它放在 `Screencap.dll` 旁边，或在插件设置 `Offline ReShade Export > D3D11 bridge DLL path` 中填写它的绝对路径。
   - KKS 不使用这个 bridge。
3. VideoExport 集成：
   - KK：使用 KK build 的 `VideoExport.dll`。
   - KKS：使用 KKS build 的 `VideoExport.dll`。

如果其中任意 DLL 缺失或版本不匹配，客户端可能仍能打开，但实时预览、gallery depth 或视频帧导出可能无法正常工作。

## 游戏端导出

### 实时导出

在游戏中按修改版 ScreenshotManager/Screencap 插件的 Offline ReShade 导出热键。默认热键是：

```text
LeftCtrl + F10
```

默认实时交接目录是：

```text
UserData\cap\OfflineReShade\
  coloroutput.png
  depthoutput.rfloat
  metadata.json
```

WinUI 客户端的 Real Time 模式会监听这些文件。当游戏插件写入新的 color/depth pair 时，native ReShade runtime 会重新载入输入，不需要重启 effects runtime。

### Gallery 导出

在 Screencap 插件中开启 archive output 后，会在普通截图文件夹保存带时间戳的静态图片 pair：

```text
UserData\cap\CharaStudio-YYYY-MM-DD-HH-MM-SS-Color.png
UserData\cap\CharaStudio-YYYY-MM-DD-HH-MM-SS-Depth.rfloat
```

Gallery 模式也支持 VideoExport 的帧命名方式：

```text
UserData\VideoExport\Frames\<timestamp>\
  0.png
  0.depth.rfloat
  1.png
  1.depth.rfloat
  metadata.json
```

Gallery 输出文件会命名为：

```text
CharaStudio-YYYY-MM-DD-HH-MM-SS-Reshade.png
0-Reshade.png
1-Reshade.png
```

输出目录与实时临时目录分开设置。

## 使用客户端

### Settings

开始预览前请先打开 `Settings`。

重要字段：

- `Game`：选择 `KKS` 或 `KK`。
- `Color PNG`：实时 color 输入。
- `Depth File`：实时 depth 输入。
- `Effect Folder`：ReShade `.fx` / `.fxh` 所在文件夹；默认值是程序目录下的 `Effects`。
- `Preset INI`：可选的 ReShade preset。
- `Output PNG`：实时输出路径。
- `Gallery Input Folder`：包含静态截图或视频帧 pair 的文件夹。
- `Gallery Output Folder`：gallery 最终渲染输出目录。
- `Render Width` / `Render Height`：留空则使用 color 图像尺寸。

程序会为 KK 和 KKS 分别保存路径设置。切换 `Game` 时会恢复该游戏上次使用的路径。

### FX、Texture 与 Add-on 相对路径

发布版中的相对路径以 `OfflineReShadeWinUI.exe` 所在目录为基准。使用默认设置时，推荐目录结构如下：

```text
OfflineReShade-win-x64-<version>\
  OfflineReShadeWinUI.exe
  Effects\
    Shader.fx
    IncludeFile.fxh
    Subfolders\...
  Textures\
    LUT.png
    Noise.png
  Addons\
    Example.addon64
```

- `Effects`：放置 `.fx`、`.fxh` 及其子文件夹。Effect 扫描是递归的，不要求所有 shader 都直接放在根目录。
- `Textures`：放置 LUT、noise、mask、blue-noise 和其他 shader 图片资源。
- `Addons`：放置 ReShade `.addon64` DLL。默认安装建议使用程序根目录的这个文件夹。扫描不递归，`.addon64` 必须直接位于实际的 AddonPath 中，不能藏在下一级文件夹。

Texture 会按以下顺序递归搜索：

1. `<Effect Folder>\Textures`
2. `<Effect Folder 的父目录>\Textures`
3. `<程序目录>\Textures`

因此也兼容常见的 `reshade-shaders\Shaders` + `reshade-shaders\Textures` 结构：把 `Effect Folder` 指向 `Shaders` 即可。

Add-on 会优先从 `<Effect Folder>\Addons` 加载；该目录不存在时，使用 `<程序目录>\Addons`。同时存在时只会启用其中一个 AddonPath，因此请以 `Add-ons` 页面实际显示的 `AddonPath` 为准。如果使用默认 `Effects`，建议始终把 `.addon64` 放在发布包根目录的 `Addons` 中，避免与 shader 混在一起。

新增、删除或替换 `.addon64` 后必须停止并重新启动 Preview。顶部的 `Reload` 只重新编译 FX，不会重新载入 add-on DLL。

### 滑条精细控制

FX 参数和 WinUI Add-on 参数中的数字滑条共用两种全局精细控制：

- 把鼠标放在数字滑条上，按住 `Ctrl` 滚动鼠标滚轮，可在 `x1`、`x2`、`x4`、`x8` 之间切换。向上滚增大倍率，向下滚减小倍率。倍率越高，同样的参数变化需要拖动更长的鼠标距离，更适合细调。切换时窗口中央会短暂显示当前倍率。
- 按 `L` 可让所有数字滑条在普通线性映射和对称对数映射之间切换。对称对数模式会给接近 0 的数值分配更多滑条空间，同时支持正值和负值。启用后每个数字滑条旁都会显示 `L`，窗口中央也会显示模式提示。

倍率应在开始拖动前设置，拖动过程中不需要滚轮。对称对数模式只改变鼠标位置到滑条范围的映射，不会改变参数的真实数值；输入框、ReShade runtime、截图和 preset 仍使用原始参数值。倍率与对数模式会自动保存，并在下次启动客户端时恢复。

### Add-on 界面使用

1. 把 `.addon64` 放入 `Addons`，并按照 add-on 自带说明把配套 `.fx` 和 texture 分别放入 `Effects` 与 `Textures`。
2. 点击 `Start Preview`，然后打开左侧 `ReShade Controls` 中的 `Add-ons` 选项卡。
3. 查看 `AddonPath`，确认程序实际扫描的是你预期的目录。
4. 查看 `Load diagnostics`。这里会列出每个发现的 add-on，以及 loaded、disabled、failed 或 skipped 状态。DLL 或依赖加载失败时，再展开 `Add-on initialization log`。
5. `Native add-on overlays` 下方的选项卡对应 add-on 注册的原生窗口。选择一个选项卡并保持 `Show` 开启，即可在 WinUI 面板中显示和操作原版 ImGui 界面。
6. `WinUI Add-on Controls` 会把能够重建的按钮、checkbox、单值或多值 slider、combo、文本、颜色、选项卡和折叠区域转换成 WinUI 控件，并实时控制当前 add-on。
7. 遇到 custom drawing、plot、texture widget、复杂 popup、画面内选点或 WinUI 按钮不能完成的交互时，使用 `Open native add-on panel` 作为原生界面 fallback。

Add-on 详情还会显示它注册的事件和离线兼容性。依赖 runtime、effect、uniform、technique、screenshot、present 或 overlay 的 add-on 可以在离线宿主中工作；依赖游戏实时 draw call、pipeline resource、场景对象、motion stream 或游戏专用 hook 的 add-on 即使加载成功，也可能只能使用一部分功能。

部分 Add-on 还会安装配套 FX technique。先在 `Add-ons` 选项卡配置 Add-on，再回到 `FX` 启用对应 technique。按照 Add-on 作者说明拖动已启用 FX 的标题，把 launchpad/pre-pass 放在需要消费其数据的效果之前。顺序和参数确认后点击 `Save Preset` 保存。

### Real Time 模式

当游戏插件正在重复更新 `coloroutput.png` 和 `depthoutput.rfloat` 时，使用 Real Time 模式。

1. 在顶部栏选择 `Real Time`。
2. 点击 `Start Preview`。
3. 在 `ReShade Controls` 中调整 techniques 和 uniforms。
4. 点击 `ReShade Shot` 或 `Save PNG` 保存当前全分辨率结果。

### Gallery 模式

Gallery 模式用于处理已经导出的截图或帧序列。

1. 在顶部栏选择 `Gallery`。
2. 设置 `Gallery Input Folder` 和 `Gallery Output Folder`。
3. 点击 Gallery 面板中的 `Refresh`。
4. 点击一个缩略图，把对应的 color/depth pair 载入现有 runtime。
5. 调整 ReShade 控制项。
6. 对当前项目使用 `ReShade Shot` / `Save PNG`，或使用 `Batch Apply` 将当前设置应用到所有有效项目。

`Frame Delay` 控制批处理切换输入后等待多少个渲染帧。对于有 temporal history、path tracing samples 或 accumulation buffers 的效果，可以适当增大这个值。

## Depth Profiles

### KKS

KKS 使用 ScreenshotManager camera render 路径，并以最终 color 尺寸导出 depth。KKS 不需要客户端侧 depth downsampling。

当前推荐 depth 格式：

```text
depthoutput.rfloat
encoding: rfloat32_device_depth_little_endian
row order: bottom_to_top
value: Unity/D3D device depth, not linear depth
```

必要时仍可选择旧的 packed PNG depth：

```text
depthoutput.png
encoding: rgba8_unorm_32_device_depth
```

### KK

KK 使用 `OfflineDepthD3D11Bridge.dll` 从渲染路径直接读取 D3D11 device depth。KK depth 始终是 raw `.rfloat`。

KK bridge 可能输出 color 尺寸的 depth，也可能输出 2x resolution depth。检测到 2x depth 时，native runtime 会在载入或热替换时下采样一次，然后复用上传后的 `R32_FLOAT` depth texture。

可选的 KK downsample filter：

- `Max 2x2`：默认选项，对 reversed-Z depth 更能保留前景。
- `Smooth Box 2x2`：平均滤波，更平滑，但可能在边缘混合前景和背景。

## 故障排查

- 客户端能打开，但预览不更新：
  - 确认游戏插件正在写入 `coloroutput.png`、depth 和 `metadata.json`；
  - 确认 `Game` 选择了正确的游戏；
  - 确认 depth 文件格式与当前游戏 profile 匹配。
- KK depth 为空或质量很低：
  - 确认 `OfflineDepthD3D11Bridge.dll` 放在 `Screencap.dll` 旁边，或已在设置中填写绝对路径；
  - 如果使用 `KK Stable Path` 不生成 depth，换用 `KK Compatibility Bridge`；
  - 关闭 KK 画质中的 Anti Aliasing / MSAA 后再测试；
  - 优先使用 Studio camera/lens 视角；
  - 查看游戏日志和 `d3d11_depth_probe.log`。
- Gallery 项目缺失：
  - 确认每张 color 图都有匹配的 depth 文件；
  - 静态截图命名应为 `*-Color.png` + `*-Depth.rfloat`；
  - 视频帧命名应为 `<frame>.png` + `<frame>.depth.rfloat`。
- 效果没有出现：
  - 确认 `Effect Folder` 指向你的 shader 文件夹；
  - 发布包默认不包含 shader，这是设计如此。
- FX 能编译但提示 texture 缺失，或启用后画面变黑：
  - 把缺失资源放入 `Textures`、`<Effect Folder>\Textures`，或 Effect Folder 同级的 `Textures`；
  - 在 FX 日志中查看具体缺失的文件名。
- Add-on 没有出现：
  - 停止 Preview，把 `.addon64` 放入程序根目录的 `Addons`，再启动 Preview；
  - 在 `Add-ons` 选项卡检查 `AddonPath`、`Load diagnostics` 和初始化日志；
  - 安装 add-on 要求的 Microsoft Visual C++ runtime 或配套 DLL；
  - 依赖游戏实时渲染数据的 add-on 可能不兼容离线宿主。
