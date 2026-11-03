using System.Security.Cryptography;
using System.Text.Json;

namespace OfflineReShade.Core;

public sealed partial class AutomationEngine
{
    private string? _martyRecoveryError;
    internal sealed record MartyProfile(string Effect, string DllHash, string FxHash);
    private static MartyProfile? MartyProfileFor(string addon) => addon switch {
        "MARTYSMODS ReLight" => new("MartysMods_RELIGHT.fx", "4a3148361df281350fafd5a2a14f3a016a329c4e2a01fa674b6c8009286d03d8", "3dd727f3945006f98b0ba69221f83ca21b05bf83bca0565823ca2bb8033291bf"),
        "MARTYSMODS ReGrade+" => new("MartysMods_REGRADE+.fx", "3ef3b70d4d51f0f6723f184014473270fbd7ef0171e73d1acb9cce8d52c10093", "2b9f4b23de0131a359d2515182911c7f4d11e820d7ad9881bfa107f6a73a0f01"),
        "MARTYSMODS LUT-Manager" => new("MartysMods_LUTMANAGER.fx", "bc368c1d4f90ef456d27f85cd927f1cec60f6a1c4587420b4a0546fa2c5eec0b", "460194fab4a9c94a603992e6bc4767772ffce5a00b41ffee15f1f6512652d89d"),
        _ => null
    };
    private async Task<string?> MartyCompatibilityAsync(MartyProfile profile, JsonElement loaded, JsonElement info, CancellationToken token)
    {
        try {
            var name = info.GetProperty("file").GetString()!;
            if (Path.GetFileName(name) != name) return "Unexpected Add-on filename.";
            var dll = Path.Combine(loaded.GetProperty("searchPath").GetString()!, name);
            var fx = Directory.EnumerateFiles(_current().EffectDir, profile.Effect, SearchOption.AllDirectories).ToArray();
            if (fx.Length != 1) return "The adapter requires exactly one matching companion FX file.";
            async Task<bool> Matches(string path, string expected) {
                await using var stream = File.OpenRead(path);
                return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).Equals(expected, StringComparison.OrdinalIgnoreCase);
            }
            if (!await Matches(dll, profile.DllHash) || !await Matches(fx[0], profile.FxHash)) return "Unverified Marty DLL/FX version; private writes are disabled until its format and rendering are tested.";
            return null;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return e.Message; }
    }
    private async Task<MartyPresetCodec> ReadMartyPresetAsync(CancellationToken token)
    {
        await WaitForEditStateAsync(token);
        var info = await _call("get_runtime_info", null, token);
        var root = Path.GetDirectoryName(info.GetProperty("presetPath").GetString()!)!;
        var snapshot = Path.Combine(root, ".marty-read-" + Guid.NewGuid().ToString("N") + ".ini");
        try {
            await _call("export_preset", new { path = snapshot }, token);
            return new(await ReadLimitedPresetAsync(snapshot, token));
        } finally { File.Delete(snapshot); }
    }
    private static async Task<string> ReadLimitedPresetAsync(string path, CancellationToken token)
    {
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("Preset exceeds 2 MiB.");
        return await File.ReadAllTextAsync(path, token);
    }
    private async Task<object> MartyPrivateStateAsync(MartyProfile profile, CancellationToken token)
    {
        var preset = await ReadMartyPresetAsync(token);
        if (profile.Effect == "MartysMods_RELIGHT.fx") return new { lights = preset.ReadLights(profile.Effect), capacity = 64, coordinateSystem = "screen XY and vendor depth coordinate (cubed before projection)" };
        if (profile.Effect == "MartysMods_REGRADE+.fx") return new { curves = preset.ReadCurves(profile.Effect), quantization = 255 };
        return new { atlasId = preset.UInts(profile.Effect, "LUT_MANAGER_SOURCE_ATLAS", 2), tileIndex = preset.UInts(profile.Effect, "LUT_MANAGER_ATLAS_IDX", 1)[0],
            tileSize = preset.UInts(profile.Effect, "LUT_MANAGER_ATLAS_TILE_SIZE", 1)[0], tileAmount = preset.UInts(profile.Effect, "LUT_MANAGER_ATLAS_TILE_AMT", 1)[0] };
    }
    private static object[] MartyActions(MartyProfile profile) => profile.Effect switch {
        "MartysMods_RELIGHT.fx" => [new { id = "set_light", label = "Set sphere light", requiresArguments = true,
            arguments = "{slot:0..63, position:[x,y,depth] (0..1), radius:0..10, intensity:0..20, temperatureTint:[-1..1,-1..1]}; intensity 0 disables a slot without renumbering" }],
        "MartysMods_REGRADE+.fx" => [new { id = "set_curve", label = "Set tone curve", requiresArguments = true,
            arguments = "{channel:RGB|R|G|B, points:[[input,output],...]}; 2..10 normalized points, strictly increasing input, endpoint inputs 0 and 1" }],
        _ => [new { id = "select_lut_from_preset", label = "Select saved LUT reference", requiresArguments = true,
            arguments = "{presetPath:absolute INI, lutDirectory:absolute folder (optional if already staged)}; selects a saved atlas reference at tile 0 only, does not load other preset settings" }]
    };
    internal async Task ApplyMartyActionAsync(MartyProfile profile, JsonElement request, CancellationToken token)
    {
        if (_martyRecoveryError != null) throw new InvalidOperationException(_martyRecoveryError);
        var id = request.GetProperty("id").GetString();
        var args = request.GetProperty("arguments");
        var original = (await _call("get_runtime_info", null, token)).GetProperty("presetPath").GetString()!;
        if (!Path.IsPathFullyQualified(original) || string.Equals(Path.GetFullPath(original), _current().PresetPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Private edits require a host-owned working preset, not the user's source preset.");
        var work = Path.GetDirectoryName(original)!;
        var temp = Path.Combine(work, ".marty-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var beforePath = Path.Combine(temp, "before.ini"); var afterPath = Path.Combine(temp, "after.ini");
        var readback = Path.Combine(temp, "readback.ini");
        var library = Path.Combine(work, "LUTs"); var staged = Path.Combine(temp, "LUTs"); var backup = Path.Combine(temp, "previous-LUTs");
        var librarySwapped = false; var mutationStarted = false; var preserveRecovery = false;
        Dictionary<string, string>? restoreValues = null;
        FileStream? lutReceipt = null;
        try {
            await _call("validate_export", null, token);
            await _call("export_preset", new { path = beforePath }, token);
            var before = new MartyPresetCodec(await ReadLimitedPresetAsync(beforePath, token));
            Dictionary<string, string> patch;
            if (profile.Effect == "MartysMods_REGRADE+.fx" && id == "set_curve") patch = MartyPresetCodec.CurvePatch(args);
            else if (profile.Effect == "MartysMods_RELIGHT.fx" && id == "set_light") {
                if (string.IsNullOrEmpty(_current().DepthPath)) throw new InvalidOperationException("ReLight requires matching depth input.");
                var techniques = (await _call("list_state", null, token)).GetProperty("techniques").EnumerateArray().Where(t => t.GetProperty("enabled").GetBoolean()).Select(t => t.GetProperty("effectName").GetString()).ToList();
                if (techniques.IndexOf(profile.Effect) >= 0 && (techniques.IndexOf("MartysMods_LAUNCHPAD.fx") < 0 || techniques.IndexOf("MartysMods_LAUNCHPAD.fx") > techniques.IndexOf(profile.Effect)))
                    throw new InvalidOperationException("Enable Launchpad before ReLight in the effect order.");
                patch = before.LightPatch(profile.Effect, args);
            } else if (profile.Effect == "MartysMods_LUTMANAGER.fx" && id == "select_lut_from_preset") {
                var source = args.GetProperty("presetPath").GetString()!;
                if (!Path.IsPathFullyQualified(source)) throw new ArgumentException("Use an absolute LUT reference preset path.");
                var reference = new MartyPresetCodec(await ReadLimitedPresetAsync(source, token));
                var atlas = reference.UInts(profile.Effect, "LUT_MANAGER_SOURCE_ATLAS", 2);
                var index = reference.UInts(profile.Effect, "LUT_MANAGER_ATLAS_IDX", 1)[0];
                var size = reference.UInts(profile.Effect, "LUT_MANAGER_ATLAS_TILE_SIZE", 1)[0];
                var amount = reference.UInts(profile.Effect, "LUT_MANAGER_ATLAS_TILE_AMT", 1)[0];
                if (atlas.All(v => v == 0) || size is < 2 or > 256 || amount is < 2 or > 256) throw new ArgumentException("Invalid saved LUT descriptor.");
                if (index != 0) throw new NotSupportedException("Only tile 0 is verified. The vendor does not expose the selected atlas dimensions needed to validate other tile indices.");
                patch = new() { ["LUT_MANAGER_SOURCE_ATLAS"] = string.Join(',', atlas), ["LUT_MANAGER_ATLAS_IDX"] = index.ToString(), ["LUT_MANAGER_ATLAS_TILE_SIZE"] = size.ToString(), ["LUT_MANAGER_ATLAS_TILE_AMT"] = amount.ToString() };
                if (args.TryGetProperty("lutDirectory", out var directory) && !string.IsNullOrEmpty(directory.GetString())) {
                    await StageLutsAsync(directory.GetString()!, staged, token);
                    if (Directory.Exists(library)) {
                        if ((File.GetAttributes(library) & FileAttributes.ReparsePoint) != 0) throw new IOException("The session LUT folder must not be a link.");
                        // Backup remains available until a confirmed readback.
                        Directory.Move(library, backup);
                    }
                    librarySwapped = true;
                    Directory.Move(staged, library);
                }
            } else throw new ArgumentException("Unknown private Marty action.");
            restoreValues = patch.Keys.ToDictionary(k => k, k => before.Get(profile.Effect, k));
            await File.WriteAllTextAsync(afterPath, before.Patch(profile.Effect, patch), token);
            if (id == "select_lut_from_preset") {
                // This vendor version retains an unknown ID in its uniforms while
                // binding a neutral fallback. Uniform readback alone is not proof.
                lutReceipt = new FileStream(Path.Combine(work, "ReShade.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                lutReceipt.Seek(0, SeekOrigin.End);
            }
            mutationStarted = true; // Includes cancellation while awaiting the first dispatch.
            await LoadMartyPresetAsync(afterPath, token);
            if (lutReceipt != null) {
                if (lutReceipt.Length < lutReceipt.Position || lutReceipt.Length - lutReceipt.Position > 2 * 1024 * 1024) throw new IOException("LUT load receipt was truncated or exceeded its limit.");
                using var reader = new StreamReader(lutReceipt, leaveOpen: true);
                VerifyMartyLutReceipt(await reader.ReadToEndAsync(token));
            }
            await _call("validate_export", null, token);
            await _call("export_preset", new { path = readback }, token);
            new MartyPresetCodec(await ReadLimitedPresetAsync(readback, token)).Verify(profile.Effect, patch);
            // Keep the session's stable working path, so resize/restart does not lose this edit.
            await _call("export_preset", new { path = original }, token);
            await _call("set_preset", new { path = original }, token);
            token.ThrowIfCancellationRequested();
        } catch (Exception failure) {
            try {
                if (librarySwapped) {
                    if (Directory.Exists(library)) Directory.Move(library, Path.Combine(temp, "failed-LUTs"));
                    if (Directory.Exists(backup)) Directory.Move(backup, library);
                }
                if (mutationStarted) {
                    using var restore = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    await LoadMartyPresetAsync(beforePath, restore.Token);
                    await _call("export_preset", new { path = readback }, restore.Token);
                    new MartyPresetCodec(await ReadLimitedPresetAsync(readback, restore.Token)).Verify(profile.Effect, restoreValues!);
                    await _call("export_preset", new { path = original }, restore.Token);
                    await _call("set_preset", new { path = original }, restore.Token);
                }
            } catch (Exception recovery) {
                preserveRecovery = true;
                _martyRecoveryError = "Private Add-on restoration failed; load a known preset before editing or exporting. Recovery snapshot: " + beforePath;
                _history.Invalidate(_martyRecoveryError);
                throw new AggregateException("Private edit and restoration failed; recovery snapshot: " + beforePath, failure, recovery);
            }
            throw;
        } finally { lutReceipt?.Dispose(); if (!preserveRecovery) Directory.Delete(temp, true); }
    }
    private async Task LoadMartyPresetAsync(string path, CancellationToken token)
    {
        await _call("set_preset", new { path }, token);
        await _call("reload_effects", null, token);
        // Drain the scheduled reload and then wait for compiled, rendered frames.
        await RuntimeOperations.WaitForFramesAsync(_call, 5, token, 120);
    }
    private static async Task StageLutsAsync(string folder, string staged, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)) throw new ArgumentException("Use an existing absolute LUT folder.");
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new IOException("LUT source must not be a link.");
        var files = Directory.EnumerateFiles(folder).Where(p => Path.GetExtension(p).ToLowerInvariant() is ".png" or ".bmp" || Path.GetFileName(p).Equals("LUTs.csv", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (files.Length is < 1 or > 256 || files.Sum(p => new FileInfo(p).Length) > 256L * 1024 * 1024) throw new ArgumentException("Use 1–256 LUT files, at most 256 MiB total.");
        Directory.CreateDirectory(staged);
        foreach (var file in files) {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("LUT files must not be links.");
            await using var source = File.OpenRead(file);
            await using var target = File.Create(Path.Combine(staged, Path.GetFileName(file)));
            await source.CopyToAsync(target, token);
        }
    }
    internal static void VerifyMartyLutReceipt(string logTail)
    {
        // Exact wording belongs to the pinned DLL profile, not a universal API.
        // Require the last completed selection attempt to explicitly resolve it.
        const string prefix = "[MARTYSMODS LUT-Manager] ";
        var reading = false; var confirmed = false;
        foreach (var line in logTail.Split('\n').SkipLast(1)) {
            var at = line.IndexOf(prefix, StringComparison.Ordinal);
            if (at < 0) continue;
            var message = line[(at + prefix.Length)..].Trim();
            if (message == "Reading preset...") { reading = true; confirmed = false; }
            else if (reading && message == "LUT specified in preset found.") confirmed = true;
            else if (message == "Preset specifies unknown LUT, defaulting to neutral.") confirmed = false;
        }
        if (!reading || !confirmed) throw new InvalidOperationException("The LUT atlas was not confirmed by the loaded vendor catalog; its identifier may be unknown or its files missing. The previous selection will be restored.");
    }
}
