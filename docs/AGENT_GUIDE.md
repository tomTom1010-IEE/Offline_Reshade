# Offline ReShade agent guide

This guide is for agents that tune ReShade FX through the Offline ReShade MCP server.
The server also serves it as the resource `offline-reshade://guide/agent`. Facts
about a particular shader come from `list_effects` and from the shader source; this
guide covers the conventions and habits that hold across effects. Notes about named
effects describe the versions that were tested. Confirm them in the source before you
rely on them.

## Workflow: isolate, preview, stack

1. **Open.** Call `open_input` with absolute paths, or `attach_session` to work on the
   WinUI image. Wait until `get_status` reports `loading: false` and `busy: false`.
2. **Survey.** Call `list_effects` with `effectName` for each candidate effect. Read the
   technique annotations first, because authors put setup and order rules there. Then
   read the uniforms by `category`.
3. **Baseline.** Call `render_preview` before enabling anything new. While iterating,
   keep previews small (`maxWidth` 640–1024).
4. **Isolate.** Enable one technique. If the effect has a debug view, turn it on and
   check that the effect sees depth and covers the right region, then turn it off again.
   Look for uniforms or combo items named Debug, Show or Visualize, or a `*DEBUG*`
   definition.
5. **Tune.** Change a few parameters per step and call `render_preview` after each step.
   When the result is surprising, change one thing at a time.
6. **Stack.** Add the next effect only when the current one looks right. Then re-check
   the earlier effects, especially after adding one that changes exposure or tone.
7. **Finish.** Set the final order with `reorder_techniques`. Save with `save_preset` or
   `export_preset_copy`, and export at full resolution with `export_image`.

To make several edits one undo entry, wrap them in `begin_edit` and `end_edit`. To
compare an effect on and off, toggle its technique and render both states.
`set_preview_comparison` changes only the live WinUI canvas; `render_preview` still
returns the processed image.

## Reading `list_effects`

Each technique has `id`, `effectName`, `name` and `enabled`. Annotations add:

| Field | Meaning |
| --- | --- |
| `label`, `ui_tooltip` | Display name and description. Authors put setup and order rules here, for example "enable and move to the top". |
| `hidden` | ReShade hides the technique from its own UI. It is usually a helper pass. |
| `forceEnabled` | The shader enables it at load and ignores the preset. |
| `timeoutMs` | The technique disables itself after this many milliseconds of rendering. If you enable it, it runs once and then reads back as disabled. |
| `readOnly` | Shader-managed (`nosave`). `set_technique` rejects it. |
| `enabledInScreenshot: false` | The author keeps it out of in-game screenshots; it is usually an overlay or HUD. Offline ReShade exports capture the rendered frame, so `export_image` includes it in both capture modes. Disable it if it must not appear in the final image. |

Each uniform has these fields:

- `value` and `defaultValue`: arrays in shader units, even for scalars. `defaultValue`
  is the shader initializer that `reset_parameter` restores.
- `type`, `rows`, `columns`, `uiType`, and `items` (combo labels by index). A `uiType` of
  `button` is pressed with `press_parameter`.
- `min`, `max`, `step`: the UI range only. `set_parameters` does not clamp; the author
  did not test values outside it.
- `label`, `category`, `ui_tooltip`, `ui_text`, `ui_units`, `readOnly`.

Each preprocessor definition has a `value` and a `defaultValue`. Effects often expose
quality levels and debug switches as definitions. `set_preprocessor_definition`
recompiles the effect, and a value that fails to compile is rolled back.

## When metadata is not enough: read the source

Labels and tooltips rarely say how the code uses a value. To find out:

- `search_effect_source(pattern, effectName)` finds a name in the effect file and the
  headers it includes. It returns `file:start-end` excerpts, with `>` marking the
  matching lines.
- `get_effect_source(file, startLine, lineCount)` reads more around a match, such as
  the whole function.
- Look for:
  - non-linear use: `x * x`, `pow`, `exp2`
  - clamps: `saturate`
  - how the result blends: `lerp(color, result, amount)`, additive or multiplicative
  - the color space: linear, gamma or ACEScg
  - depth comparisons
- Useful searches: a uniform's name; a technique's name, to find its passes;
  `RESHADE_DEPTH`, for depth handling; an effect's `#define` options.

Both tools read only the active session's effect directory and only shader files. They
do not follow links inside that directory, and their output is capped. Shader code is
often licensed: use it to understand behaviour, and do not reproduce it.

## Depth

- **Input format.** Depth input is device depth. With `depthFormat: "raw"` it must be
  float32 values in [0, 1], with rows stored bottom to top. Its size is the color
  resolution (`depthProfile: "kks"`) or twice it (`"kk"`, which is downsampled).
- **Linearization.** ReShade linearizes depth using these macros, with a near plane of 1:
  - `RESHADE_DEPTH_INPUT_IS_REVERSED`: 1 by default, so near is 1 and far is 0.
  - `RESHADE_DEPTH_INPUT_IS_LOGARITHMIC`: 0 by default.
  - `RESHADE_DEPTH_INPUT_IS_UPSIDE_DOWN`: 0 by default.
  - `RESHADE_DEPTH_LINEARIZATION_FAR_PLANE`: 1000 by default.
- **Linear depth.** For device depth produced with near plane 1 and far plane F, the
  linearized depth is (z − 1) / (F − 1). It is 0 at the camera and 1 at the far plane.
  Overrides of these macros appear as `PRESET` definitions in `list_effects`.
- **Focus and distance sliders** usually take linear depth or a remap of it. For example,
  iMMERSE Depth of Field squares its manual focus value, so the focus slider should be
  √(linear depth of the subject).
- **Finding the subject's depth.** No tool reads depth at a pixel. Use a debug view that
  shows depth or the focus plane, and bisect the value with `render_preview`.
- **Depth-based effects** (AO, GI, DOF, fog) need a depth input. Without one they have
  nothing meaningful to work with.

## Effect order

`reorder_techniques` takes the complete list of technique IDs in execution order; the
first runs first. Author rules in `label` or `ui_tooltip` override these heuristics.
Without such rules, a sensible order is:

1. Setup passes that other effects read. For example, iMMERSE Launchpad prepares data
   for the other iMMERSE effects and must be enabled and placed at the top.
2. Anti-aliasing, while the image is still clean.
3. Occlusion and lighting that should look like part of the scene: AO, GI and relighting.
4. Depth of field.
5. Bloom, glare and exposure.
6. Color grading, tonemapping and LUTs. iMMERSE SOLARIS goes right before ReGrade.
7. Sharpening and other finishing steps.
8. Film grain and noise last.

The helper techniques of one effect, such as a histogram pass, keep the order the effect
declares.

## Traps

- **Non-linear sliders.**
  - A label never says whether the code squares or remaps its value. On a squared slider
    the low end changes little and the top end changes a lot.
  - Search the source before you decide that a slider does nothing.
  - Squared in the tested iMMERSE versions: RTGI Bounce Lighting Intensity
    (`RT_IL_AMOUNT`), SOLARIS Bloom Intensity (`HDR_BLOOM_INT`), Depth of Field Focal
    Distance (`RAW_FOCUS_PLANE_DEPTH`).
- **Shader-managed techniques.**
  - Do not toggle `hidden`, `forceEnabled` or `timeoutMs` techniques.
  - Example: iMMERSE SMAA's `MartysMods_AntiAliasing_Prepass` is hidden, force-enabled
    and has `timeoutMs: 1`. It runs once after loading and then reads back as disabled.
    This is expected.
- **Setup order.**
  - An effect that depends on a setup pass renders wrong output when that pass is
    disabled or placed after it.
  - In testing, iMMERSE RTGI placed before Launchpad produced blown-out bands.
- **Game-tuned defaults.**
  - GI and AO defaults assume a dim game frame. On an image that is already lit they can
    wash it out.
  - On bright test scenes, iMMERSE RTGI looked better with Ambient Level about 0.8 and
    Bounce Lighting Intensity about 3.5 than with the defaults (0.3 and 10).
- **Vignette parameters vary between effects.** In iMMERSE ReGrade:
  - The mechanical vignette radius is where the image reaches full black.
  - The falloff starts at radius × (1 − 0.2 × blurriness).
  - With the default shape, the radius is measured so that the image corners are at 1.
- **Debug views.** They are ordinary parameters or definitions and are saved in presets.
  Turn them off before saving or exporting.
- **Preview versus export.**
  - `render_preview` is downscaled. Judge grain and sharpening at a larger `maxWidth` or
    in the export.
  - Exports contain every enabled technique, including `enabledInScreenshot: false`
    overlays that in-game ReShade would leave out of a screenshot.
- **Add-on state.**
  - Some of a look lives in Add-on state rather than in FX uniforms: iMMERSE ReGrade+
    curves, ReLight lights, the LUT Manager selection.
  - Read it with `list_addon_services`. Change it with `set_addon_parameter` or
    `invoke_addon_action`.
  - These edits are outside FX undo and the unsaved marker. Export a preset copy to keep
    them.
