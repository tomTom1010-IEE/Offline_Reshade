# Offline ReShade MCP

The release includes `Mcp/OfflineReShade.Mcp.exe`, a local MCP stdio server built
with the official C# SDK. It exposes the same clean export and batch operations
used by WinUI. No model API key is required by Offline ReShade itself.

## Connect a compatible MCP client

Register the server's absolute executable path in the MCP client. An example:

```json
{
  "mcpServers": {
    "offline_reshade": {
      "command": "C:\\Tools\\OfflineReShade\\Mcp\\OfflineReShade.Mcp.exe",
      "args": ["--native", "C:\\Tools\\OfflineReShade\\OfflineReShadePrototype.exe"]
    }
  }
}
```

The generated `mcp-config.example.json` contains paths for the build's staging
directory. Adjust them after moving or extracting the distribution. The process
uses stdout exclusively for MCP messages and stderr for diagnostic logs.

## Work with the current WinUI preview

1. Start `OfflineReShadeWinUI.exe`. Open an image, or attach first to open/listen through MCP.
2. Call `list_sessions` to discover local UI instances.
3. Call `attach_session` with the chosen session ID. You can also pass
   `--session <id>` when launching the MCP server.
   If Windows packaging redirects the discovery directory, copy the `MCP session ID`
   shown in WinUI Settings and attach with that ID directly. Pipe access still
   requires the same Windows user.
4. Call `get_status` and wait until `loading` is false before querying effects.
5. Call `list_effects`, preferably filtering by FX filename.
6. Use `set_parameters` / `set_technique`, then `render_preview` to see the result.
7. Export with `export_image` or save settings with `save_preset`.

Attached MCP clients share the UI's single native connection through an automation
broker restricted to the current Windows user. While an export/batch owns the
session, other mutations are rejected and conflicting UI controls are disabled.
Read-only status queries remain available. Closing an attached MCP client leaves
the UI preview running.

### Shared operation results (automation broker 4)

WinUI and both MCP modes now use one mutation queue. Parameter updates are serialized;
an exclusive operation (open, preset load/save, reload, export or batch) drains previously
accepted updates and rejects new mutations until completion. A batch remains active
through restoring the original input, not only until its submission returns.

Mutating tools return `structuredContent` and equivalent JSON text. Completed operations
include `operation` with `id`, `operation`, `status`, `code`, `message`, `sourcePath`,
`targetPath` and applicable recovery `actions`. Status is `succeeded`, `warning`,
`cancelled`, `failed` or `rejected`; an active request reports `running`. Errors use
MCP `isError: true`; intentional black output is a successful tool response with
`operation.status: warning`, and retains the existing `warning` field.

`get_operation_status` exposes runtime observation, active operation, latest result and
persistent notice even without an open runtime. `get_status` includes the same `activity`.
`cancel_operation` cancels accepted work; attached clients use a separate control connection
so cancellation is not queued behind the request it must interrupt. Cancellation waits
for native response draining and any required restoration before the session is editable.
It cannot undo an output file already atomically written by the native host.

`save_preset` validates a temporary exported INI before replacing the destination;
silent native write failures do not report success or overwrite the previous preset.
Explicitly loading the same preset path reloads disk contents into the private working
copy. Ordinary input switches continue preserving unsaved parameters.

### Export configuration and destination

`export_image` defaults to ReShade screenshot capture (`reShadeShot: true`). Set
`reShadeShot: false` explicitly for direct PNG without screenshot add-on events.
No automatic fallback occurs between the two capture methods.

`conflictPolicy` accepts `replace` (the MCP default for existing clients), `rename`
(add a numbered filename), or `fail`. WinUI defaults to `rename`. Rendering and
validation finish before the output is atomically published. The result's `path`
is the actual final filename, including any suffix; failed or cancelled work
does not replace an existing output. A file created concurrently is respected
by both `rename` and `fail`.

Before mutations, attached sessions submit outstanding debounced UI parameter
updates. Export waits for those submissions and fails if they fail. A newer text
or MCP change cannot be overtaken by an older delayed slider update.

`get_status` and `get_operation_status` expose `pendingConfiguration` with `input`
and `error`, separately from `appliedInput`. An export with pending UI configuration
fails unless the caller explicitly supplies `applyInput` (a complete RenderInput).
It applies that configuration and exports under one operation lock, retaining the
previous context if opening fails. Invalid draft dimensions are reported, not
silently converted to source size. Optional `expectedInput`, copied from the
status's `appliedInput`, rejects an export if the applied input has since changed.

```json
{
  "path": "C:\\Captures\\Rendered\\shot.png",
  "conflictPolicy": "rename",
  "warmupFrames": 5,
  "reShadeShot": true
}
```

### Listening for game output

`listen_input` takes the same paths, depth settings and dimensions as `open_input`;
it always enables watching. Color/depth files may be absent when the listener is
accepted. The effects directory and any specified preset must already exist.
Its response means the listener was registered, not that a new image was applied.

`get_operation_status.waitingInput` reports the requested input and its `waiting`,
`unreadable`, `opening` or `invalid` state, with a readable error when available.
`appliedInput` separately identifies the last connected renderer input; `showEmpty`
stays true while waiting so the previous image is not presented as the new one.
The listener opens only a stable readable pair, keeps the previous context after
failure, and retries damaged input only when its file signature changes. Editing
and export requests are rejected while the initial pair is pending.

After successful opening, native watching handles subsequent updates and export
validation. `stop_listening` cancels pending arrival and disables native watching
without discarding the last applied image or its adjustments. `open_input`
supersedes pending listening. `cancel_operation` also cancels initial waiting;
use `stop_listening` to stop watching an already applied input.

## Independent rendering

Without attachment, `open_input` starts an owned native process. Example arguments:

```json
{
  "colorPath": "C:\\Captures\\shot-Color.png",
  "depthPath": "C:\\Captures\\shot-Depth.rfloat",
  "effectDir": "C:\\Tools\\OfflineReShade\\Effects",
  "outputPath": "C:\\Captures\\Rendered\\shot.png",
  "depthFormat": "raw",
  "depthProfile": "kks",
  "width": 0,
  "height": 0
}
```

Depth is optional for color-only effects. Width and height must both be zero to
use source dimensions or both be positive for a fixed output size. Raw depth is
validated at source color dimensions (or 2x in KK mode), then resampled. Changing
source dimensions in automatic mode restarts the native session and restores its
saved preset. Each session keeps its own configuration, preset, logs and screenshot
temporary directory under `%LOCALAPPDATA%/OfflineReShade/Sessions/work`.

In 0.8.1, `open_input` also accepts `watchInput` (default false). A watched resolution
change rebuilds the session on export; WinUI also rebuilds during preview polling.
`get_status` reports `busy` while a render operation or rebuild holds the session;
wait for it to become false before issuing another mutation. A reconnecting session
may report `reconnecting: true` with dimensions temporarily zero.
Invalid watched inputs block exporting the old image. Raw depth must contain finite
normalized values in [0, 1]; no implicit clamping or NaN replacement is performed.
Required effect compilation/creation failures and unloaded texture dependencies
fail export explicitly. An intentional all-black render succeeds with a `warning`
in the export result. Existing output files are replaced only after successful encoding.

In 0.8.2, native protocol 4 additionally reports `adapterLuid`, `adapterName`,
`previewSynchronization`, `cpuPreview`, `previewError`, `recoveryRequired` and
`deviceError`. A lost device blocks exports until the owning Core/WinUI session
rebuilds it. Automatic recovery is limited to three attempts per 30 seconds; a
persistent failure returns an error. Recovery preserves the global Effects state.
`cpuPreview` describes display transport, not the rendering backend: effects still
run through D3D11. GPU sharing failures can switch the display to CPU without
changing output dimensions or the active preset.

Source and render dimensions must fit both 16384 pixels per axis and
64 × 1024² total pixels. RAM/VRAM budget checks may reject smaller images on a busy
or lower-memory device. They estimate base resources and headroom; arbitrary FX
allocations are still checked by the export health validation. No implicit reduction
of requested output size is performed. Preview surfaces have separate limits of
8192 per axis and 16 × 1024² pixels; CPU preview defaults to a 1920-pixel longest
edge. The preview composition transform accounts for DPI independently of output.

## Tools

| Tool | Purpose |
| --- | --- |
| `list_sessions`, `attach_session` | Discover/attach to a running WinUI session |
| `open_input` | Open or switch a color/depth pair |
| `listen_input`, `stop_listening` | Wait for game output; stop watching while retaining the current image |
| `get_operation_status`, `cancel_operation` | Inspect structured activity and pending/applied input; cancel work |
| `get_status` | Loading, dimensions, frame count and input generation |
| `list_effects` | Paginated uniform metadata/current values and techniques |
| `set_parameters` | Set uniforms using `effect.fx::uniform` IDs |
| `set_technique`, `set_effects_enabled` | Enable effects or compare before/after |
| `reorder_techniques` | Change execution order |
| `load_preset`, `save_preset` | Load an INI or export current settings |
| `render_preview` | Return a GUI-free PNG image to the model |
| `export_image` | Export a full-resolution PNG using either save path |
| `start_batch`, `get_job`, `cancel_job` | Start, inspect or cancel a batch |

`set_parameters` takes an `updates` array, for example:

```json
{"updates": [{"id": "Example.fx::Exposure", "value": 0.5}]}
```

Vector values are arrays. Use IDs and ranges returned by `list_effects`; values
are real shader units. The scalar/vector editor does not expose uniform arrays.

`start_batch.items` is an array of input objects as shown above. Optional arguments:
`warmupFrames` (default 5), `reShadeShot` (true), `resume` (true), `retries` (1).
The job returns immediately. Poll `get_job` for running/completed/cancelled/failed.
Cancellation preserves completed outputs. The first output directory receives
`.OfflineReShade.batch.json`. Resume skips a file only when the job/settings
signature, source content and output hash match. The signature includes the actual
global Effects state, output dimensions, adapter identity, preset, FX/include/texture content and renderer
binaries. Every item is validated before considering a checkpoint; effects reload from
disk before rendering. Add-ons with private state conservatively disable cross-job reuse.
Output paths must be unique
and must not overlap any input. Each batch uses one effect directory, preset and
output-size policy inherited from its item configuration.

## Native CLI and diagnostics

```powershell
.\OfflineReShadePrototype.exe --color C:\Captures\color.png `
  --depth C:\Captures\depth.rfloat --effect-dir .\Effects `
  --output C:\Captures\render.png --work-dir C:\Captures\Session `
  --warmup-frames 120 --timeout-seconds 600
```

The CLI waits for compilation and then counts real completed frames. On timeout
or failure it returns a nonzero exit code. Previews and output are currently
8-bit PNG; HDR/EXR and game-specific missing render inputs are outside this version.

## Build and verify

```powershell
.\tools\build_offline_release.ps1 -RunIntegrationTests
```

This builds native components, publishes self-contained WinUI/MCP runtimes,
checks forbidden files and required runtime files, runs core and GPU/MCP integration
checks (including D3D pixel sharing, scaling and fault recovery), and writes a ZIP
plus SHA-256 and file manifest to `build/offline-release`.
Python with Pillow is required for the image checks. To create an incremental ZIP,
add `-BaselineArchive C:\Downloads\previous-release.zip`.

The fault tests opt in using process-local `OFFLINE_RESHADE_TEST_SHARED_FAILURE`
and `OFFLINE_RESHADE_TEST_DEVICE_LOSS_MARKER` environment variables. Normal runs
do not set these. A marker simulates the device-failure path without resetting the
system GPU; it does not replace hardware driver-reset testing.

## Maintenance contract (0.8.3)

Native RPC is now **5**, automation broker **3**, custom offline DLL API **1**,
and preview bridge ABI **3**. These are internal component contracts, separate
from MCP's negotiated stdio protocol. Connecting or attaching validates the
contract and pinned upstream before any mutation; update the whole package together.

`get_diagnostics` is read-only and works before `open_input`. It reports expected
versions, actual runtime/adapter/preview health when connected, package provenance,
and integrity of seven core binaries. `runtimeError` explains disconnected state;
package status is `ok`, `mismatch`, `invalid`, or `unpackaged` for a development build.
It does not reload effects, change settings, or reset the graphics driver.
Use `python tools/verify_offline_package.py <release.zip>` for complete archive
verification. See [maintenance baseline and upgrade procedure](MAINTENANCE.md).
