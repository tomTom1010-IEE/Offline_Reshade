# Add-on interfaces and confirmed edits

Native RPC and automation broker version 8 distinguish queued ImGui edits from confirmed application. ReShade API 20, upstream 6.7.3 and the preview ABI are unchanged.

## Compatibility controls

ImGui control identities include the process session, window and native ImGui ID stack. The host rejects missing/hidden/disabled controls and malformed or out-of-range values. A raw `set_addon_imgui_value` response means **queued**, not applied. `list_addon_imgui_capture.receipts` exposes `queued`, `applied` (with the observed widget value), `rejected`, `expired` or `cancelled`. Core waits for confirmation; cancellation/timeout removes any pending request. A consumed button action confirms callback delivery, not arbitrary asynchronous work initiated by the Add-on.

The bridge still depends on ImGui callbacks and cannot provide stable business identities for opaque private objects. Complex draw-list controls require the native compatibility panel. Loaded DLLs and supported event registrations do not imply verified rendering or complete UI support. Parallax DOF explicitly requires IGCS camera input unavailable from a static image.

## Independent business interface

An Add-on may export `OfflineReShadeAddonServiceV1` as declared in [the SDK header](../include/offline_addon_service.hpp). This interface executes on the runtime owner thread without opening ImGui. It provides `get`, `set` and `action` using UTF-8 JSON. The returned buffer remains owned by the Add-on and is limited to 1 MiB. `get` must be read-only. Writes must validate parameters, complete synchronously and return actual state or an error. Native callbacks run in-process with the same trust boundary as ordinary ReShade Add-ons.

Example `get` response:

```json
{"schemaVersion":1,"parameters":[{"id":"gain","label":"Gain","type":"float","uiType":"slider","min":0,"max":1,"value":[1]}],"actions":[{"id":"reset","label":"Reset gain"}]}
```

`set` request: `{"id":"gain","value":[0.25]}`. `action` request: `{"id":"reset","arguments":{}}`. An error is `{"error":"description"}`. Numeric and boolean values use arrays matching the declared shape. IDs must identify business objects and parameters independently of display order. Core validates ID, shape, type and declared bounds before dispatch, then performs a fresh readback. Float values allow the corresponding float32 representation; integer values are compared exactly. A mismatch fails the operation. Providers remain responsible for atomic writes; the host does not roll back opaque private state or retry failed actions automatically.

The WinUI Add-ons page offers a separate independent service section. Refresh reads the latest state; generic parameter controls and declared actions use this business interface. Actions requiring extra arguments can be called through MCP. Private Add-on state is not included in FX undo, preset snapshots or batch cache guarantees.

MCP tools:

- `list_addons`: loading, dependencies, event support and service availability.
- `list_addon_services`: independent schemas and values; per-service errors are preserved.
- `set_addon_parameter(addon, id, value)`: validated write with readback.
- `invoke_addon_action(addon, id, arguments)`: declared action.
- `press_parameter(id)`: momentary boolean FX action, released after a rendered frame.

## Installed Marty support

The installed Marty DLLs do not export service ABI v1. Two host adapters expose only their existing public companion-FX contracts, marked `scope=companion_fx`, `privateControls=false`:

- LUT Manager: enhanced LUT quality, preview, chroma blend and luma blend.
- ReGrade+: LUT sampling mode.

These adapters participate in shared FX undo and preset saving. They do not expose private LUT selection, grading curves or ReLight light objects. ReLight and Parallax DOF remain explicitly outside independent business control. Implementing those capabilities requires a supported provider interface or a separately implemented and validated effect; capturing ImGui widgets does not satisfy that requirement.

## Verification

`tests/OfflineReShade.AddonService.Tests.vcxproj` builds a test-only Add-on with no ImGui interface. `tests/run_addon_services.py` checks actual MCP discovery, parameter writes, actions, rendered PNG pixel changes, rejected invalid writes, FX metadata and button release. The release pipeline runs this check with isolated inputs. Core tests additionally simulate a provider that acknowledges a write without applying it and require failure.

Local Marty regression evidence is under `artifacts/marty-interface-audit-20261103`: original audit, corrected control identities, missing/hidden-ID rejection and IGCS classification. This evidence uses private local vendor copies; it is not distributed as project test data. No computer use is needed for these checks.
