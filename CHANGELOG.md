# Offline ReShade 0.8.3

- Pin the actual ReShade upstream base/tree, all submodule commits, .NET SDK/runtime and internal interface versions in one checked baseline. Native version metadata no longer increments on every local build.
- Enforce NuGet locked restores and pin CI actions, Windows image generation and the test wheel. CI now gates packaging on source/interface checks, Core, graphics, output correctness and mixed-component regressions; failed runs preserve diagnostics.
- Reject incompatible ReShade DLLs, native RPC endpoints and automation brokers before mutations. Preview bridge ABI mismatches use the existing CPU fallback. Native RPC is now version 5, broker 1, offline DLL API 1 and preview bridge ABI 3.
- Add MCP `get_diagnostics` for expected/actual versions, runtime health, package provenance and seven core binary hashes, including sessions without an open input.
- Release manifests include actual working-tree source identity, dependency lock hashes, toolchain metadata and command verification reports. A ZIP verifier checks every file and rejects incomplete/corrupt packages; batch checkpoints include the product version.
- Driver-update and additional hardware qualification are deferred until the owner's planned local NVIDIA update. No driver/reset tests are added by this step.

# Offline ReShade 0.8.2

- GPU preview now opens the rendering host's adapter by LUID and synchronizes both shared preview surfaces with keyed mutexes. Completed frames are cached locally for responsive repainting.
- Zoom samples within a fixed viewport. DPI composition transforms prevent double scaling/cropped images; GPU and CPU previews share aspect-ratio and pan bounds, including thin panoramas.
- Preview errors trigger bounded display-resource rebuilds, then CPU fallback. Native device errors remain visible over RPC and block export until the owner recreates the runtime. Recovery permits at most three attempts in 30 seconds and preserves the global Effects state.
- Source/output images are limited to 16384 pixels per axis and 64 × 1024² pixels, with conservative available RAM/VRAM checks. Preview surfaces are bounded separately; CPU preview defaults to at most 1920 pixels on its longest edge. These preview limits never resize final output.
- Protocol 4 reports adapter identity, transport and device/preview errors. Batch fingerprints include adapter identity. The preview bridge uses a versioned creation entry point; update the complete package together.
- Added real D3D sharing/pixel tests, extreme dimensions, DPI geometry checks and injected failure/recovery tests. Physical driver resets and a broader multi-GPU hardware matrix remain unverified; driver-update work was subsequently deferred by the owner.

# Offline ReShade 0.8.1

- Export validates required effects, texture loading and normalized finite depth before reporting success.
- Both PNG paths preserve existing output on failure; entirely black images remain valid and return a warning.
- Watched input resolution changes request a session rebuild instead of silently scaling to the old size. Invalid replacements block stale exports.
- WinUI releases old shared preview resources during rebuild and stops preview callbacks on close.
- Batch resume validates every input and enabled effect, reloads FX from disk, and hashes source content, preset, global effect state, dimensions, shaders/includes, loaded texture dependencies and renderer binaries. Add-ons with opaque state disable cross-job reuse.
- Native protocol 3 requires the matching ReShade DLL health exports. MCP `open_input` accepts `watchInput`.
- Output-correctness regression tests run during verified packaging. See `docs/STABILITY_PLAN.md` for the remaining D3D and maintenance work.

# Offline ReShade 0.8.0

- Both PNG and ReShade screenshot exports exclude GUI using a separate render target.
- Exports wait for FX compilation and actual rendered warmup frames. The native CLI
  supports `--warmup-frames`, `--timeout-seconds` and an isolated `--work-dir`.
- Raw depth is validated against source image dimensions and resampled to output
  dimensions. Gallery/automation sessions restart at source size when dimensions
  change; explicit output dimensions remain fixed.
- Batch export uses an immutable input list, edit locking, progress, cancellation,
  retries and a checkpoint with input fingerprints and output SHA-256 hashes.
- FX lists are virtualized, parameters are created on expansion, and effects can
  be searched by filename or parameter name.
- The portable distribution includes both .NET and Windows App SDK dependencies.
  Client packaging creates a file/hash manifest and optionally an incremental ZIP.
- Added a C# MCP stdio server with session discovery/attachment, independent native
  sessions, effect inspection, parameter editing, presets, image previews and
  cancellable batch exports.

This version retains the existing PNG/R32_FLOAT depth and D3D11 workflow. Third-party
shaders and add-ons are installed separately. Temporal effects still require an
appropriate warmup frame count and the input data expected by the effect.
