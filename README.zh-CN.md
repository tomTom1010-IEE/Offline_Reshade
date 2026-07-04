# Offline ReShade 中文使用说明

> 这是使用者说明，只覆盖安装、导出、客户端使用和排错。开发、构建和架构细节请看英文 README。

Offline ReShade 是一个 Windows 工具，用于把 ReShade FX 效果应用到已经导出的静态图片或视频帧序列。它面向 Koikatu (KK) 和 Koikatsu Sunshine (KKS) 的离线截图流程：

- 游戏端插件导出 color 和 depth 文件；
- `OfflineReShadeWinUI.exe` 提供编辑界面；
- `OfflineReShadePrototype.exe` 承载全分辨率 D3D11 ReShade runtime；
- UI 中显示缩放预览，截图和批处理输出使用全分辨率 runtime。

发布包不会包含第三方 `.fx` 或 `.fxh` shader。用户需要在 Settings 里选择自己的 ReShade effect 文件夹。

## 快速开始

1. 安装与你的游戏匹配的 Offline ReShade 游戏端插件。
2. 把 Offline ReShade 客户端发布包解压到游戏目录之外的任意位置。
3. 运行 `OfflineReShadeWinUI.exe`。
4. 打开 `Settings`。
5. 选择 `Game`：`KKS` 或 `KK`。
6. 把 `Effect Folder` 设置为你自己的 ReShade shader 文件夹。
7. 使用 `Real Time` 或 `Gallery` 模式。

干净的发布包应该只包含程序二进制和 runtime DLL，不应包含：

- `.fx` / `.fxh` shader 文件；
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
- `Effect Folder`：你的 ReShade shader 文件夹。
- `Preset INI`：可选的 ReShade preset。
- `Output PNG`：实时输出路径。
- `Gallery Input Folder`：包含静态截图或视频帧 pair 的文件夹。
- `Gallery Output Folder`：gallery 最终渲染输出目录。
- `Render Width` / `Render Height`：留空则使用 color 图像尺寸。

程序会为 KK 和 KKS 分别保存路径设置。切换 `Game` 时会恢复该游戏上次使用的路径。

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