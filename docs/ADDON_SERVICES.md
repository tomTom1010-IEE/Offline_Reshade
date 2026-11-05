# Add-on interfaces and confirmed edits

Native RPC and automation broker version 8 distinguish queued ImGui edits from confirmed application. ReShade API 20, upstream 6.7.3 and the preview ABI are unchanged.

## Compatibility controls

ImGui control identities include the process session, window and native ImGui ID stack. The host rejects missing/hidden/disabled controls and malformed or out-of-range values. A raw `set_addon_imgui_value` response means **queued**, not applied. `list_addon_imgui_capture.receipts` exposes `queued`, `applied` (with the observed widget value), `rejected`, `expired` or `cancelled`. Core waits for confirmation; cancellation/timeout removes any pending request. A consumed button action confirms callback delivery, not arbitrary asynchronous work initiated by the Add-on.

The bridge still depends on ImGui callbacks and cannot provide stable business identities for opaque private objects. Complex draw-list controls require the native compatibility panel.

WinUI labels mirrored widgets the way ImGui shows them: text after `##` is an ID and is hidden, icon-font glyphs (private-use code points) are dropped, and a text line drawn just before an unlabeled widget becomes that widget's label. A multi-value widget whose hidden ID names its parts, such as ReLight's `##TempTint`, gets one heading per value. Some add-ons, including ReLight, ReGrade+ and Parallax DOF, draw their editor only while their overlay is open. The compatibility section offers an **Open editor** button for these, which selects that overlay natively without showing the native surface; it closes when the Add-ons page is hidden and reopens when it returns. Loaded DLLs and supported event registrations do not imply verified rendering or complete UI support. Parallax DOF explicitly requires IGCS camera input unavailable from a static image.

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

The installed Marty DLLs do not export service ABI v1. Their public companion-FX parameters remain available through host adapters:

- LUT Manager: enhanced LUT quality, preview, chroma blend and luma blend.
- ReGrade+: LUT sampling mode.

These public parameters participate in shared FX undo and preset saving. A second, version-verified adapter now reads and changes selected **private preset fields**, without invoking ImGui. It is enabled only for the exact locally tested DLL/FX SHA-256 pairs in `MartyPrivateServices.cs`; an unknown or ambiguous version retains public parameters but disables private actions with `privateAdapterError`. A real ABI v1 provider always takes precedence.

`list_addon_services` returns `scope=verified_preset_bridge`, current `state`, action argument descriptions and explicit limitations for a matching installation. The existing `invoke_addon_action` MCP tool dispatches:

| Add-on / action | Arguments and supported behavior |
| --- | --- |
| ReGrade+ / `set_curve` | `{"channel":"RGB","points":[[0,0],[0.5,0.8],[1,1]]}`. Channels RGB/R/G/B, 2–10 points. Input endpoints 0 and 1; inputs must remain strictly increasing after 8-bit quantization. Reads return the quantized points. |
| ReLight / `set_light` | `{"slot":0,"position":[0.5,0.5,0.02],"radius":0.1,"intensity":1,"temperatureTint":[0,0]}`. Slots 0–63, sphere lights only. XY and depth coordinate are normalized; the shader cubes the depth coordinate before projection. Intensity 0 disables a slot without changing other slot identities. Depth is required; enabled Launchpad must precede enabled ReLight. |
| ReLight / `select_light` | `{"slot":1}`. Chooses the light that the add-on's own Light Properties editor changes; `-1` clears the choice. The slot must be in use (0 to the light count − 1). It leaves the image unchanged. `state.selectedLight` reports the current choice. |
| LUT Manager / `select_lut_from_preset` | `{"presetPath":"D:/presets/warm.ini","lutDirectory":"D:/LUTs"}`. Reads only the saved LUT descriptor from this preset, leaving all other FX settings unchanged. Stages PNG/BMP and optional LUTs.csv into the private session library, then selects the saved atlas reference at **tile 0**. `lutDirectory` can be omitted when the session already has the required library. |

Private actions snapshot the complete working preset, validate input, reload effects to reconstruct the vendor's internal data, wait for rendered frames, validate export prerequisites, and compare a fresh preset export against the requested fields. Merely writing hidden uniforms is insufficient: tests observed unchanged output until reconstruction. For LUT selection, even a matching descriptor is insufficient: this DLL leaves an unknown ID in the preset while silently binding a neutral fallback. The adapter additionally requires the last selection attempt in the **newly appended vendor log** to explicitly resolve the atlas. Missing, truncated or fallback receipts fail closed. This log wording is part of the exact DLL profile, not a general vendor API. Reported `state` is stored preset data, not a live enumeration of the vendor's object graph.

`select_light` is lighter. ReLight caches its selection and reads the hidden `SELECTED_LIGHT_INDEX` field only when its effect reloads; a live write alone leaves the editor on the old light. The adapter therefore writes the field through the native `set_hidden_uniform` method, which accepts only hidden, preset-backed, non-`source` uniforms and is never forwarded from MCP or UI requests. The host saves the working preset first, so other unsaved add-on edits survive. It then reloads only `MartysMods_RELIGHT.fx`, which takes a few frames from the shader cache, and confirms the stored value by readback. ReLight compacts its slots when a light is deleted, so slot numbers can change after Delete Light.

Cancellation, missing atlas or a readback mismatch fails the operation and restores the prior preset and LUT library. If restoration itself fails, the recovery snapshot is retained, FX history is invalidated, and export remains blocked until a known preset is explicitly loaded. Neither source presets nor original vendor files are edited.

Private actions are **not FX undo entries and do not set the FX unsaved marker**. Use `export_preset_copy` to save their preset fields explicitly; a LUT preset must travel with the matching LUT files. They do not imply general private-state batch, history or persistence guarantees. The WinUI page identifies argument-bearing actions as MCP operations instead of displaying buttons that would submit empty arguments. The exception is `select_light`. In a game you pick the light to edit by clicking its marker, which the preview does not forward. So, with the ReLight editor open, the mirrored Light Management section lists the lights (number, color, exposure, X/Y) under Create New Light. Choosing one calls `select_light`, and the mirrored Light Properties then edit that light. This is not a new visual curve editor or light gizmo; the markers themselves still appear only in the native overlay.

### Remaining concrete limitations

- **Arbitrary LUT filename selection / favorites:** the installed DLL exposes a two-word atlas identifier and a GPU texture semantic, but no enumeration, filename-to-ID resolver or favorites API. The verified preset route can select an already saved reference; it cannot derive a supported identifier for an arbitrary new file. Nonzero atlas tile indices are rejected because the selected atlas's dimensions cannot be independently established through this interface. A vendor catalog/select API (or a separately owned LUT loader) is required for those operations.
- **Other light models and color modes:** the verified writer handles sphere lights and temperature/tint. It preserves the opaque fourth packed word and does not guess spotlight/directional controls, RGB packing semantics or editor labels/gizmos. Existing packed state remains readable. A documented object schema or additional format and rendering validation is needed to extend it.
- **ReGrade+ monochrome/hue curves, wheels and scopes:** RGB/R/G/B tone curves are verified. BWHUE/monochrome curves have different semantics and boundary conditions; graphical scopes are GPU/CPU resources rather than simple parameter values. These are not advertised as supported by this adapter.
- **Version portability:** hashes intentionally fail closed when an installed DLL or FX changes. Re-run the local vendor regression and review the field layouts before adding another profile; changing a hash alone is not validation.

The vendor's [ReGrade+ guide](https://guides.martysmods.com/shaders/immerseultimate/regradeplus/) describes its native curve UI, and the [LUT Manager guide](https://guides.martysmods.com/shaders/immerseultimate/lutmanager/) describes native atlas selection. Neither inspected guide provides a programmatic private-state API. Their current general persistence/UI guidance is not treated as a binary-format contract; the adapter is based on the installed version and measured round trips.

## Verification

`tests/OfflineReShade.AddonService.Tests.vcxproj` builds a test-only Add-on with no ImGui interface. `tests/run_addon_services.py` checks actual MCP discovery, parameter writes, actions, rendered PNG pixel changes, rejected invalid writes, FX metadata and button release. The release pipeline runs this check with isolated inputs. Core tests additionally simulate a provider that acknowledges a write without applying it and require failure.

Local Marty regression evidence is under `artifacts/marty-interface-audit-20261103`: original audit, corrected control identities, missing/hidden-ID rejection and IGCS classification. This evidence uses private local vendor copies; it is not distributed as project test data. No computer use is needed for these checks.

`tests/run_marty_private_services.py` is an optional regression requiring a licensed local Marty installation, two saved tile-0 LUT references and their LUT library. It verifies actual MCP/GPU changes for composite and individual curves, sphere lights, light selection (confirmed, image unchanged), neutral/warm LUT selection, unknown-atlas rollback, version rejection and preservation of original resources. It never activates native panels, captures widgets or sends OS input. `MartyPrivateTests` additionally tests quantization collisions, preservation of unrelated settings, refused-write rollback and cancellation during dispatch. Proprietary vendor files are not part of CI or the repository.
