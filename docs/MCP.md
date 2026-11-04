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

### Shared operation results (automation broker 7)

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

### FX history and preset state

`get_edit_history` returns `canUndo`, `canRedo`, `isModified`, `revision`, labels,
the active `transactionId`, and capability/warning messages. `get_status` and
`get_operation_status` include its cached summary as `editHistory`. WinUI and
attached MCP share this history; independent MCP owns a separate session.

`set_parameters` is one atomic edit: if an update fails, earlier updates in that
request are restored. `undo`, `redo`, `reset_parameter`, technique order/state,
the global effects switch and preprocessor overrides participate in history.
Preprocessor changes await recompilation; failures restore the previous state.
Rollback failure is an explicit error that disables unsafe history until a known
preset is loaded. Out-of-band native changes clear incompatible history with a warning.

For a continuous gesture, call `begin_edit`, pass its `transactionId` on each FX
mutation, then call `end_edit`. Other writers are rejected while it is active;
`cancel_operation` releases an abandoned transaction and retains undo for values
already applied. A WinUI slider drag uses the same mechanism. Unsupported arrays,
source-driven/hidden uniforms and shader-managed `nosave` values are not editable
through this contract. Third-party Add-on private state has its own save/history rules.

`save_preset` with no path writes the external source INI. Supplying `path` performs
Save As and rebinds the source without restarting. `export_preset_copy` writes a copy
without rebinding or clearing the original unsaved marker. Native autosaves remain
in the session's private working INI. Saving adds `[OfflineReShade] EffectsEnabled`
metadata; ordinary presets without it default to enabled when explicitly loaded.
Image export never marks a preset saved. Float values round-trip through RPC and INI.

Loading another preset (including same-path explicit reload), opening/listening
with a changed preset, or `export_image.applyInput` with a changed preset defaults
to `unsavedChanges: "reject"`. Choose `"save"` or `"discard"` explicitly; `savePath`
can provide a Save As destination. Pass the `revision` from `get_edit_history` as
`expectedRevision` to reject a decision made before subsequent edits. Decision
fields belong to the operation, not to persisted `RenderInput` or batch fingerprints.
WinUI offers Save / Discard / Cancel before replacement and close. Saving failure
keeps edits and prevents replacement. If a control value was rejected, correct it,
reset that parameter, or use **Restore controls from renderer** before exporting.

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
| `list_effects` | Paginated uniform metadata, current values and shader defaults; techniques with their annotations |
| `search_effect_source`, `get_effect_source` | Search and read the shader sources of the session's effect directory (read-only, bounded) |
| `set_parameters` | Set uniforms using `effect.fx::uniform` IDs |
| `set_technique`, `set_effects_enabled` | Change technique or global effect state |
| `set_preview_comparison` | Compare original/processed preview without changing exported effects |
| `reorder_techniques` | Change execution order |
| `load_preset`, `save_preset`, `save_preset_as`, `export_preset` | Load, save, save as, or export a separate preset copy |
| `render_preview` | Return a GUI-free PNG image to the model |
| `export_image` | Export a full-resolution PNG using either save path |
| `start_batch`, `get_job`, `cancel_job` | Start, inspect or cancel a batch |
| `preflight_batch`, `resume_batch`, `retry_failed_batch` | Review inputs or continue a saved task |

`set_parameters` takes an `updates` array, for example:

```json
{"updates": [{"id": "Example.fx::Exposure", "value": 0.5}]}
```

Vector values are arrays. Use IDs and ranges returned by `list_effects`; values
are real shader units. The scalar/vector editor does not expose uniform arrays.

`start_batch.items` is an array of input objects as shown above. Optional arguments:
`warmupFrames` (default 5), `reShadeShot` (true), `resume` (true), `retries` (1),
and `conflictPolicy` (`replace`).
The job returns immediately. Poll `get_job` for running/completed/cancelled/failed.
Cancellation preserves completed outputs. The first valid output directory receives
the saved task and an optional shared `.OfflineReShade.batch.json` cache.
Resume reuses a file only when the job/settings
signature, source content and output hash match. The signature includes the actual
global Effects state, output dimensions, adapter identity, preset, FX/include/texture content and renderer
binaries. Every item is validated before considering a checkpoint; effects reload from
disk before rendering. Add-ons with private state conservatively disable cross-job reuse.
Output paths must be unique and must not overlap any input or effect dependency.
Items may use different presets, effect directories and dimensions. Each item's
settings are frozen when the task starts; matching the current preset captures
live unsaved FX edits. See [Frozen batch tasks](#frozen-batch-tasks-broker-7) for
per-item results, saved-task recovery and context restoration.

## FX knowledge for agents

Metadata alone rarely explains how a shader uses a value, so the server exposes
three more sources of knowledge. The server's MCP `instructions` summarize the
workflow and point to the guide.

**Agent guide.** The resource `offline-reshade://guide/agent` (`text/markdown`) is
[AGENT_GUIDE.md](AGENT_GUIDE.md). It covers the isolate, preview and stack loop, depth
conventions, ordering heuristics and known traps.

**Technique annotations in `list_effects`.** Techniques report these fields, which are
omitted when the annotation is absent:

| Field | Source annotation | Meaning |
| --- | --- | --- |
| `label`, `ui_tooltip` | `ui_label`, `ui_tooltip` | Display text. Authors often state setup and order rules here. |
| `hidden: true` | `hidden` | Hidden from ReShade's UI. |
| `forceEnabled: true` | `enabled` | Enabled by the shader at load, regardless of the preset. |
| `timeoutMs` | `timeout` | Disables itself after this many milliseconds of rendering. One-shot passes use it. |
| `readOnly: true` | `nosave` | `set_technique` rejects it. |
| `enabledInScreenshot: false` | `enabled_in_screenshot` | In-game screenshots skip it. Offline exports capture the rendered frame and include it. |

**Uniform defaults in `list_effects`.** Uniforms report `defaultValue`, the shader
initializer that `reset_parameter` restores, in the same shape as `value`. A
renderer without the optional `ReShadeGetOfflineUniformDefault` export omits it.

**Shader source tools.** `search_effect_source` and `get_effect_source` read the
source when metadata does not explain a parameter: squared sliders, blend formulas,
depth handling. They read only from the effect directory of the applied input, or of
the input a listener is waiting for, in both independent and attached modes.

- **`search_effect_source(pattern, effectName, regex, caseSensitive, contextLines, maxResults)`**
  - Searches `.fx`, `.fxh`, `.addonfx`, `.h`, `.hlsl`, `.hlsli` and `.inc` files.
  - The pattern is a literal by default. With `regex: true` it is a .NET regular
    expression run by the linear-time engine, so backreferences and lookarounds are
    not supported.
  - `effectName` limits the search to that FX file and the headers it includes.
    Includes are resolved approximately as ReShade does: the including file's directory
    first, then the effect directory and its subdirectories.
  - Results are `file:start-end` excerpts with numbered lines; `>` marks the matching
    lines. At most 100 matching lines are returned.
- **`get_effect_source(file, startLine, lineCount)`**
  - Reads up to 400 numbered lines.
  - `file` is a path relative to the effect directory, an absolute path inside it, or a
    unique file name.
- **Containment**
  - Paths that resolve outside the effect directory are refused, as are non-shader
    files and alternate data streams.
  - Links, hidden entries and system entries inside the directory are not followed.
  - Files over 4 MiB are skipped, and each result is capped at 32,000 characters.

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

Native RPC is now **7**, automation broker **7**, custom offline DLL API **1**,
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


## Preview-only comparison

`set_preview_comparison(original: true)` shows the applied color input in the live
WinUI/GPU/CPU preview; `false` restores the processed image. `get_status` reports
`previewOriginal`. Comparison does not submit pending UI edits, change FX global
enablement, enter undo history, mark a preset modified, or change output dimensions.
It is transient and resets when the native runtime is rebuilt. `export_image` and
preview-capture tools continue to return processed pixels even while comparison is
active. For an intentional output without effects, use `set_effects_enabled`.

Native RPC 7 and broker 7 are required; this does not change the upstream ReShade
API or preview bridge ABI.


### Frozen batch tasks (broker 7)

`preflight_batch(items, warmupFrames, reShadeShot, conflictPolicy)` returns every
item, invalid reasons, counts and output directories without changing the editor.
This is file/settings validation; decoding, FX compilation, textures and depth
are checked by the native renderer before exporting each item.

`start_batch` accepts `conflictPolicy`: `replace`, `rename`, `fail`, or `skip`.
The MCP default remains `replace`; WinUI defaults to `rename`. `resume: true`
allows fingerprint-verified cache reuse. This is separate from `skip`, which
leaves an existing file untouched without claiming its contents are correct.
Optional `expectedRevision` and `expectedInput` reject a stale review.

Starting captures immutable preset copies, per-item output settings and effect
resource hashes. An empty or matching source preset uses the live unsaved FX;
an explicitly different preset uses that preset's frozen contents. Tasks are
stored at `<output>/.OfflineReShade.jobs/<id>/job.json` with their preset INIs.
Keep that directory together when retaining a task. Schema version 1 is required.
Source preset changes cannot change a saved task; changed effect dependencies
require starting a new task. Third-party Add-on private state is not serialized,
so its outputs are not eligible for reuse between attempts.

`get_job` exposes `manifestPath`, per-item `items` (index, input, state, outputPath,
error, warning, attempts, sourceFingerprint, outputHash), and separate `exported`, `reused`, `skipped`, `failed`
and `cancelled` counters. `completed` counts processed terminal entries, not
successful images. One failed item does not stop later items. Overall `failed`
can therefore contain successful outputs. A task remains `running` through
restoration of the original editor; a restoration failure is explicit.

Each task retains its own fingerprints and output hashes; deleting or replacing
the shared `.OfflineReShade.batch.json` cache cannot erase its completion evidence.

`resume_batch(path)` reads the saved task, rechecks output fingerprints/hashes and
continues using its frozen settings. `retry_failed_batch(path)` retries only failed
or invalid entries, preserving other results. Fix missing/corrupt inputs first;
ambiguous gallery depth associations require fixing the selection and starting a
new task. `cancel_job` preserves the task file and completed outputs. Retry/resume
can be called from a fresh MCP process or from WinUI's **Resume saved task** picker.

The original input, source preset identity, unsaved FX, global effects flag,
preview comparison and pending configuration are restored after completion,
failure or cancellation. FX history remains unchanged after verified restoration.
An originally disconnected editor stays disconnected. Private Add-on state is
outside this guarantee, as with normal preset loading and session reconstruction.
Outputs are rendered to temporary files and verified again before publication;
changed inputs/dependencies cannot overwrite a previous artwork and be reported
as success. Legal black images remain successful exports with a warning.
# Independent Add-on interfaces (RPC 8)

`list_addons`, `list_addon_services`, `set_addon_parameter`, and `invoke_addon_action` expose declared business capabilities without opening native UI. `press_parameter` invokes momentary FX buttons. See [Add-on service contracts, supported Marty scope and tests](ADDON_SERVICES.md). Existing Marty DLLs do not implement the new service ABI; host adapters expose their public companion parameters and a version-verified subset of private preset state.

## Verified Marty private actions

`list_addon_services` now includes version-verified Marty private preset adapters when the installed DLL/FX pair matches the tested profile. Use the declared `invoke_addon_action` arguments for ReGrade+ `set_curve`, ReLight `set_light`, or LUT Manager `select_lut_from_preset`. These operate without ImGui and reconstruct internal GPU state by reloading effects. Private edits are outside FX undo/unsaved markers; save an explicit preset copy. See [the exact supported subset and remaining blockers](ADDON_SERVICES.md#installed-marty-support).
