# Maintenance baseline

Baseline introduced in Offline ReShade 0.8.3 on 2026-11-03.

## Source and component identity

`maintenance/baseline.json` is the source of truth. ReShade is built from this fork,
with upstream base `0eff32e237578591a44946a0e0f9581620c026b4` from
<https://github.com/crosire/reshade>. The nearest upstream release is `v6.7.3`
(`4a50d1eddace85734871d91792ff214f13f66c01`); the base is a later commit, **not** an
unmodified v6.7.3 release. The lock records its tree and all 11 submodule commits.
Do not replace ReShade64.dll with an official download: this application uses
custom offline entry points in addition to the public ReShade API.

| Contract | Version | Enforcement |
|---|---:|---|
| ReShade public add-on API | 20 | Header/layout fingerprint and upstream identity |
| Custom offline DLL API | 1 | Host checks DLL export before creating a runtime |
| Native JSON RPC | 5 | Core and WinUI handshake before mutations |
| WinUI automation broker | 4 | MCP attachment handshake, including upstream/native dependencies |
| Preview bridge ABI | 3 | WinUI checks export before creating a panel |

An incompatible renderer blocks rendering/export with an explicit error. An
incompatible preview bridge follows the existing CPU preview fallback. An old
broker cannot be attached silently. Product patch versions may interoperate only
when every checked interface and the upstream identity match. MCP's negotiated
stdio protocol is separate from these internal version numbers.

Generated constants (`include/offline_contract.hpp`, `OfflineReShade.Core/BuildContract.cs`,
`maintenance/OfflineVersion.props`, `global.json`) are verified, never silently
rewritten by a build. Native file version is fixed at 6.7.3.0; its product string
identifies the Offline ReShade version. Local rebuild count no longer changes it.

## Build and CI

Required baseline: VS 2022 / v143, Windows SDK 10.0.26100.0, .NET SDK 9.0.315,
self-contained .NET runtime 8.0.28, Python 3.12.4 x64. NuGet restores use locked
mode; Pillow and the GLAD generator's Jinja2/MarkupSafe wheels are version/hash pinned
in an isolated build environment. GLAD uses local specifications and reproducible
generation without fetching moving remote specifications. GitHub Actions use
full commit SHAs and `windows-2022`. VS servicing patches and hosted image updates
are recorded in the manifest, not claimed to be frozen or bit-for-bit reproducible.

The baseline disables native whole-program optimization (`/GL`) after repeated
MSVC 14.44 C1001 failures during link-time code generation, including a clean rebuild.
Regular Release optimization remains enabled; shader compilation settings are unchanged.
This can reduce cross-module C++ optimization opportunities. Re-enabling it requires
a reviewed toolchain update and repeatable clean builds, not an automatic retry.

```powershell
python tools/verify_offline_baseline.py
python -m pip install --require-hashes --only-binary=:all: -r tests/requirements.lock.txt
./tools/build_offline_release.ps1 -RunIntegrationTests
python tools/verify_offline_package.py <release.zip>
```

The `Offline ReShade maintenance` workflow replaces the inherited ReShade setup
distribution workflow. PRs and branch pushes first run the source/interface gate,
release verifier unit tests and Core tests. The dependent Windows package job
checks workflow syntax with hash-pinned actionlint, rebuilds WinUI, MCP and native components, runs the existing output/graphics suites
plus mixed-version rejection checks, then validates the complete ZIP. It uploads
packages only after success; build logs and test command reports survive failures.
These are automated software checks, not NVIDIA driver-update qualification.

CI requires a clean source tree and checks `offline-vX.Y.Z` tags against the
baseline. Branch protection should require `Source and interface baseline` and
`Verified Windows x64 package`; repository settings are not changed by the script.
This workflow uploads build artifacts; it does not automatically publish a release.

Local development packages can record `sourceDirty=true`. Their provenance includes
the HEAD commit, SHA256 of actual relevant working-tree build inputs (including
untracked project files), dependency lock hashes, upstream/submodule identities,
and actual toolchain versions. This prevents a dirty package from masquerading as
an exact build of HEAD. `-SkipBuild` is marked explicitly and is for local iteration;
CI always builds native code. The source snapshot identifies inputs, not a Git commit.

Each ZIP has a SHA256 sidecar, and every packaged file except the manifest itself
is hashed in `manifest.json`. `verification.json` records commands and whether
integration tests ran. Checksums detect corruption; they are not a publisher signature.
MCP `get_diagnostics` reads versions, runtime health, provenance and the hashes of
seven core binaries. It works before an input is opened and does not reset devices.

## Updating the baseline

1. Review an explicit upstream commit and its diff against the current base. Keep
   the offline export validation, shared texture, input notification and add-on
   overlay changes accounted for. Update the upstream commit/tree/tag and submodule
   pins together; never use a moving branch or `submodule update --remote` for release.
2. For an ABI/layout/signature change, increment the affected internal version.
   Increment native/broker protocols for incompatible request/response changes.
   Preserve version negotiation so old components fail before writes. Review
   `maintenance/interfaces.lock.json` even for additive public surface changes.
3. Edit `maintenance/baseline.json`, run `python tools/sync_offline_contract.py --write`,
   and after reviewing the API diff run
   `python tools/verify_offline_baseline.py --write-interface-lock`.
   For deliberate NuGet updates restore with `-p:RestoreLockedMode=false`, inspect
   the lock diff, then restore again in locked mode. Update CI action/tool pins
   from their publisher's repository and review the changed code.
4. Run the full verified build from a clean checkout, inspect manifest/diagnostics,
   and commit the source, generated constants, dependency locks and tests together.
   Tag only the tested commit. Do not commit local artifacts, user shaders or settings.

## Deferred verification

Per the project owner's 2026-11-03 instruction, driver-change testing is deferred
until a future NVIDIA driver update on this machine. No driver installation/reset,
cross-vendor hardware matrix or related test expansion is part of this baseline work.

References: [SDK selection](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json),
[NuGet dependency locks](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files#locking-dependencies),
[GitHub Actions immutable action pins](https://docs.github.com/en/actions/reference/security/secure-use).
