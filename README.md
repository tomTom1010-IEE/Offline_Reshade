# Offline ReShade

Offline ReShade is a Windows tool for applying ReShade FX effects to still
images or exported frame sequences. It is designed for Koikatu (KK) and
Koikatsu Sunshine (KKS) offline capture workflows:

- the game-side plugins export color and depth files;
- `OfflineReShadeWinUI.exe` provides the editing UI;
- `OfflineReShadePrototype.exe` hosts a full-resolution D3D11 ReShade runtime;
- preview is scaled in the UI, while screenshots and batch output use the
  full-resolution runtime.

This repository is based on ReShade, but the release package intentionally does
not include third-party `.fx` or `.fxh` shader resources. Users must select
their own ReShade effect folder in Settings.

## Quick Start

1. Install the matching game-side Offline ReShade plugins.
2. Extract the Offline ReShade release package anywhere outside the game
   folder.
3. Run `OfflineReShadeWinUI.exe`.
4. Open `Settings`.
5. Select `Game`: `KKS` or `KK`.
6. Set `Effect Folder` to your own ReShade shader folder.
7. Use either `Real Time` or `Gallery` mode.

The clean release package should include the application binaries and runtime
DLLs, but should not include:

- `.fx` / `.fxh` shader files;
- paid or private ReShade presets;
- local user settings;
- ReShade logs;
- PDB/import-library developer files.

If you have these, it means you are using a redistributed version from some unreliable source, delete it and re-download it from github.

## Required Game Plugins

Offline ReShade does not capture the game by itself. The game must export
matching color and depth files first.

Install BepInEx 5 and the normal BepisPlugins dependencies for your game, then
install the matching Offline ReShade plugin DLLs into `BepInEx\plugins`.

For the complete still-image and VideoExport workflow, check that the companion
plugin package provides these three DLL roles:

1. Screencap/ScreenshotManager:
   - KK: `Screencap.dll`
   - KKS: `KKS_Screencap.dll`
2. KK native depth bridge:
   - `OfflineDepthD3D11Bridge.dll`
   - Required for KK high-quality D3D11 device depth.
   - Put it next to `Screencap.dll`, or set its absolute path in
     `Offline ReShade Export > D3D11 bridge DLL path`.
   - KKS does not use this bridge.
3. VideoExport integration:
   - KK: `VideoExport.dll` from the KK build.
   - KKS: `VideoExport.dll` from the KKS build.

If one of these DLLs is missing or mismatched, the client may still open, but
real-time capture, gallery depth, or video frame export will not work correctly.

## Game-Side Export

### Real Time Export

In-game, press the Offline ReShade export hotkey from the modified
ScreenshotManager/Screencap plugin. The default hotkey is:

```text
LeftCtrl + F10
```

The default real-time handoff folder is:

```text
UserData\cap\OfflineReShade\
  coloroutput.png
  depthoutput.rfloat
  metadata.json
```

The WinUI client watches these files in Real Time mode. When the plugin writes a
new color/depth pair, the native ReShade runtime reloads the input without
restarting the effects runtime.

### Gallery Export

Enable archive output in the Screencap plugin to save timestamped still-image
pairs in the normal screenshot folder:

```text
UserData\cap\CharaStudio-YYYY-MM-DD-HH-MM-SS-Color.png
UserData\cap\CharaStudio-YYYY-MM-DD-HH-MM-SS-Depth.rfloat
```

Gallery mode also supports the VideoExport frame naming convention:

```text
UserData\VideoExport\Frames\<timestamp>\
  0.png
  0.depth.rfloat
  1.png
  1.depth.rfloat
  metadata.json
```

Gallery output files are named:

```text
CharaStudio-YYYY-MM-DD-HH-MM-SS-Reshade.png
0-Reshade.png
1-Reshade.png
```

The output folder is configured separately from the real-time temporary folder.

## Using the Client

### Settings

Open `Settings` before starting preview.

Important fields:

- `Game`: `KKS` or `KK`.
- `Color PNG`: real-time color input.
- `Depth File`: real-time depth input.
- `Effect Folder`: your ReShade shader folder.
- `Preset INI`: optional ReShade preset.
- `Output PNG`: real-time output path.
- `Gallery Input Folder`: folder containing still or video frame pairs.
- `Gallery Output Folder`: final gallery render output folder.
- `Render Width` / `Render Height`: leave blank to use the color image size.

The app stores separate path sets for KK and KKS. Switching `Game` restores the
last paths used for that game.

### Real Time Mode

Use Real Time mode when the game plugin is repeatedly updating
`coloroutput.png` and `depthoutput.rfloat`.

1. Select `Real Time` on the top bar.
2. Click `Start Preview`.
3. Adjust techniques and uniforms in `ReShade Controls`.
4. Click `ReShade Shot` or `Save PNG` to write the current full-resolution
   result.

### Gallery Mode

Use Gallery mode to edit a folder of exported screenshots or frames.

1. Select `Gallery` on the top bar.
2. Set `Gallery Input Folder` and `Gallery Output Folder`.
3. Click `Refresh` in the Gallery panel.
4. Click a thumbnail to load that color/depth pair into the existing runtime.
5. Adjust ReShade controls.
6. Use `ReShade Shot` / `Save PNG` for the selected item, or `Batch Apply` to
   apply the current settings to every valid gallery item.

`Frame Delay` controls how many rendered frames the batch process waits after
switching inputs. Increase it for effects that keep temporal history, path
tracing samples, or accumulation buffers.

## Depth Profiles

### KKS

KKS uses the ScreenshotManager camera render path and exports depth at the final
color size. KKS does not use client-side depth downsampling.

Current preferred depth format:

```text
depthoutput.rfloat
encoding: rfloat32_device_depth_little_endian
row order: bottom_to_top
value: Unity/D3D device depth, not linear depth
```

Legacy packed PNG depth can still be selected when needed:

```text
depthoutput.png
encoding: rgba8_unorm_32_device_depth
```

### KK

KK uses `OfflineDepthD3D11Bridge.dll` to read D3D11 device depth directly from
the render path. KK depth is always raw `.rfloat`.

The KK bridge may output depth at the color size or at 2x resolution. When 2x
depth is detected, the native runtime downsamples it once when the file is
loaded or hot-swapped, then reuses the uploaded `R32_FLOAT` depth texture every
frame.

Available KK downsample filters:

- `Max 2x2`: default, preserves foreground for reversed-Z depth.
- `Smooth Box 2x2`: average filter, smoother but can mix foreground/background
  at edges.

## Architecture

```text
Game plugin
  -> color/depth/metadata files
  -> OfflineReShadeWinUI.exe
  -> OfflineReShadePrototype.exe
  -> ReShade64.dll full-resolution D3D11 runtime
  -> GPU shared texture preview + full-resolution screenshot/output
```

Main components:

- `OfflineReShadeWinUI`: WinUI 3 UI, settings, gallery, batch apply, and
  ReShade controls.
- `OfflineReShadePrototype`: native D3D11 host process that owns the ReShade
  runtime, input textures, screenshot output, and frame loop.
- Named pipe JSON-RPC: real-time control channel from WinUI to native runtime.
  Techniques, uniforms, preset saving, screenshots, and input hot-swaps are
  applied directly to the live runtime rather than by editing a preset and
  reloading.
- `OfflineReShadePreviewBridge.dll`: GPU preview bridge used to show the
  full-resolution runtime in the WinUI panel without CPU readback.
- `ReShade64.dll`: modified ReShade runtime used by the native host.

The preview panel may be zoomed and panned, but it does not change the ReShade
runtime size. `BUFFER_WIDTH` and `BUFFER_HEIGHT` stay at the selected render
resolution, so screenshots and batch output are full resolution.

## ReShade Effects and Presets

Effects are user supplied. Set `Effect Folder` to a folder containing ReShade
`.fx` and `.fxh` files.

The clean release package intentionally does not ship shader packs. This avoids
redistributing paid/private shaders and keeps licensing separate from the
application.

Preset changes made through the UI are applied live. Use `Save Preset` when you
want to persist the current technique and uniform state.

## Building

Requirements:

- Windows
- Visual Studio 2022
- Windows App SDK dependencies restored by NuGet
- x64 build target

Build the main release targets with Visual Studio MSBuild:

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\amd64\MSBuild.exe" OfflineReShadePrototype.vcxproj /p:Configuration=Release /p:Platform=x64 /m
& "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\amd64\MSBuild.exe" OfflineReShadeWinUI\OfflineReShadeWinUI.csproj /p:Configuration=Release /p:Platform=x64 /m
```

Runtime output is written to:

```text
bin\x64\Release\
```

For distribution, copy the release output but exclude local settings, logs,
debug symbols, import libraries, and any `.fx` / `.fxh` shader resources.

## Troubleshooting

- Client opens but preview does not update:
  - confirm the game plugin is writing `coloroutput.png`, depth, and
    `metadata.json`;
  - confirm `Game` is set to the correct profile;
  - confirm the depth file format matches the selected game.
- KK depth is empty or low quality:
  - confirm `OfflineDepthD3D11Bridge.dll` is installed next to
    `Screencap.dll`, or configured by absolute path;
  - check the game log for bridge diagnostics.
- Gallery items are missing:
  - confirm each color image has a matching depth file;
  - supported still naming is `*-Color.png` + `*-Depth.rfloat`;
  - supported video naming is `<frame>.png` + `<frame>.depth.rfloat`.
- Effects do not appear:
  - confirm `Effect Folder` points to your shader folder;
  - the release package does not include shaders by design.

## License

This project is based on ReShade. ReShade is licensed under the terms of the
[BSD 3-clause license](LICENSE.md). Some ReShade source files are dual-licensed
under MIT where stated in their file headers.
