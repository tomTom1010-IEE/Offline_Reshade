using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OfflineReShade.Core;

public sealed partial class AutomationEngine
{
    private sealed record FrozenBatchItem(RenderInput Input, string? Preset, string? PresetHash, string? Error);
    private sealed record BatchTaskFile(int Schema, string Id, BatchRequest Settings, FrozenBatchItem[] Inputs,
        Dictionary<string, string> Dependencies, BatchStatus Status);
    private sealed record BatchFile(string Source, string Hash, string Path);
    private sealed record BatchCheckpoint(string Signature, Dictionary<string, BatchFile> Done);
    private const string BatchSignature = "frozen-output-v4";

    public static BatchPreflight PreflightBatch(BatchRequest request)
    {
        if (request.Items == null || request.Items.Length is 0 or > 10000) throw new ArgumentException("Batch needs 1 to 10000 items.");
        if (request.WarmupFrames is < 0 or > 360000 || request.Retries is < 0 or > 5) throw new ArgumentException("Invalid warmup frames or retry count.");
        if (request.ConflictPolicy is not ("replace" or "rename" or "fail" or "skip")) throw new ArgumentException("Conflict policy must be replace, rename, fail or skip.");
        if (request.InputErrors != null && request.InputErrors.Length != request.Items.Length) throw new ArgumentException("Input error count does not match batch items.");
        var items = request.Items.Select((input, index) =>
        {
            try
            {
                var normalized = NormalizeInput(input, requireFiles: false) with { WatchInput = false };
                if (!string.IsNullOrEmpty(request.InputErrors?[index])) return new BatchItemResult(index, normalized, "invalid", Error: request.InputErrors![index]);
                try
                {
                    ValidateInput(normalized);
                    if (!Directory.Exists(normalized.EffectDir)) throw new DirectoryNotFoundException("Effects directory not found.");
                    if (normalized.PresetPath.Length > 0 && !File.Exists(normalized.PresetPath)) throw new FileNotFoundException("Preset not found.", normalized.PresetPath);
                    return new BatchItemResult(index, normalized);
                }
                catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return new(index, normalized, "invalid", Error: e.Message); }
            }
            catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException)
            { return new(index, input ?? new(""), "invalid", Error: e.Message); }
        }).ToArray();
        var sources = BatchSources(items.Select(x => x.Input));
        var duplicateOutputs = items.Where(x => !string.IsNullOrWhiteSpace(x.Input.OutputPath))
            .GroupBy(x => x.Input.OutputPath, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        items = items.Select(x => sources.Contains(x.Input.OutputPath) ? x with { State = "invalid", Error = "Output overlaps a batch source." }
            : duplicateOutputs.Contains(x.Input.OutputPath) ? x with { State = "invalid", Error = "Batch output paths must be unique." } : x).ToArray();
        return new(items, items.Count(x => x.State == "pending"), items.Count(x => x.State == "invalid"),
            items.Where(x => x.State == "pending").Select(x => Path.GetDirectoryName(x.Input.OutputPath)!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), request.ConflictPolicy);
    }

    private static HashSet<string> BatchSources(IEnumerable<RenderInput> inputs)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in inputs.SelectMany(x => new[] { x.ColorPath, x.DepthPath, x.PresetPath }))
            if (!string.IsNullOrWhiteSpace(path)) { try { result.Add(Path.GetFullPath(path)); } catch (Exception e) when (e is ArgumentException or NotSupportedException) { } }
        return result;
    }

    private async Task StartBatchAsync(OperationRequest operation, TaskCompletionSource<JsonElement> accepted, Task edits, CancellationToken token)
    {
        try
        {
            await _operations.RunAsync(operation, async ct =>
            {
                await edits.WaitAsync(ct);
                _history.RequireAccess(null);
                if (_renderSession == null) throw new InvalidOperationException("Batch export requires a session with complete editing-context capture and restore.");
                EffectSnapshot? before = _renderSession.IsConnected ? await _history.SynchronizeAsync(ct) : null;
                using var context = await _renderSession.CaptureContextAsync(ct);
                BatchTaskFile task;
                string manifest;
                if (operation.Method == "batch_start")
                {
                    var request = operation.Arguments.Deserialize<BatchRequest>(JsonOptions) ?? throw new ArgumentException("Missing batch request.");
                    _history.CheckRevision(request.ExpectedRevision);
                    if (request.ExpectedInput != null && request.ExpectedInput != _current()) throw new InvalidOperationException("The applied image changed. Review the batch again.");
                    (task, manifest) = await CreateBatchAsync(request, context, ct);
                }
                else
                {
                    manifest = Path.GetFullPath(operation.Arguments.GetProperty("path").GetString()!);
                    task = await ReadBatchTaskAsync(manifest, ct);
                    task = task with { Settings = task.Settings with { Resume = true } };
                    if (operation.Method == "batch_retry" && !task.Status.Items!.Any(x => x.State is "failed" or "invalid"))
                        throw new InvalidOperationException("This task has no failed items to retry.");
                }
                _batchActive = true;
                Job = task.Status with { State = "running", Current = null, Error = null, ManifestPath = manifest };
                Changed?.Invoke(); accepted.TrySetResult(Json(Job));
                var finished = Job;
                try { finished = await RunBatchAsync(task, manifest, operation.Method == "batch_retry", ct); }
                catch (Exception error) { finished = Job! with { State = "failed", Error = error.Message }; }
                finally
                {
                    try
                    {
                        using var restore = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                        await _renderSession.RestoreContextAsync(context, restore.Token);
                        if (before != null)
                        {
                            await WaitForEditStateAsync(restore.Token);
                            if (!before.SameAs(EffectSnapshot.Read(await _call("get_edit_state", null, restore.Token))))
                                throw new IOException("The restored FX parameters do not match the original editing context.");
                        }
                        if (_batchRestored != null) await _batchRestored();
                        if (_renderSession.IsConnected) await _call("get_runtime_info", null, restore.Token);
                        else Activity.Disconnected();
                    }
                    catch (Exception error)
                    {
                        _history.Invalidate("Batch context restore failed: " + error.Message);
                        finished = finished! with { State = "failed", Error = JoinError(finished!.Error, "Restore editing context: " + error.Message) };
                    }
                    finally { _batchActive = false; }
                    Job = finished;
                    try { await SaveBatchTaskAsync(manifest, task with { Status = Job! }, CancellationToken.None); }
                    catch (Exception error) { Job = Job! with { State = "failed", Error = JoinError(Job!.Error, "Save task: " + error.Message) }; }
                    Changed?.Invoke();
                }
                return Json(Job);
            }, token);
        }
        catch (Exception error) { accepted.TrySetException(error); }
    }

    private async Task<(BatchTaskFile, string)> CreateBatchAsync(BatchRequest request, RenderSessionContext context, CancellationToken token)
    {
        var preflight = PreflightBatch(request);
        if (preflight.Valid == 0) throw new ArgumentException("The batch has no valid items. " + preflight.Items[0].Error);
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(preflight.OutputDirectories[0], ".OfflineReShade.jobs", id);
        Directory.CreateDirectory(directory);
        var manifest = Path.Combine(directory, "job.json");
        var inputs = new List<FrozenBatchItem>();
        var copies = new Dictionary<string, (string Path, string Hash)>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in preflight.Items)
        {
            var input = row.Input;
            try
            {
                input = NormalizeInput(input, requireFiles: false) with { WatchInput = false };
                var useLive = context.PresetSnapshot != null && (input.PresetPath.Length == 0 || string.Equals(input.PresetPath, context.Input.PresetPath, StringComparison.OrdinalIgnoreCase));
                var key = useLive ? "<live>" : input.PresetPath;
                if (!copies.TryGetValue(key, out var copy))
                {
                    var name = "preset-" + copies.Count + ".ini";
                    var path = Path.Combine(directory, name);
                    var source = useLive ? context.PresetSnapshot! : input.PresetPath;
                    if (source.Length > 0) File.Copy(source, path);
                    else await File.WriteAllTextAsync(path, "[GENERAL]\nTechniques=\n", token);
                    if (useLive) await PresetMetadata.WriteEffectsEnabledAsync(path, context.EffectsEnabled, token);
                    copy = (name, await FileHashAsync(path, token)); copies.Add(key, copy);
                }
                inputs.Add(new(input, copy.Path, copy.Hash, null));
            }
            catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
            { inputs.Add(new(input, null, null, "Could not freeze task settings: " + error.Message)); }
        }
        var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!inputs.Any(x => x.Preset != null)) throw new IOException("No task settings could be captured. " + inputs[0].Error);
        foreach (var dir in inputs.Where(x => x.Preset != null).Select(x => x.Input.EffectDir).Distinct(StringComparer.OrdinalIgnoreCase))
            if (Directory.Exists(dir)) foreach (var path in EffectFiles(dir)) dependencies[path] = await FileHashAsync(path, token);
        var rows = preflight.Items.Select((x, i) => inputs[i].Error is { } error ? x with { State = "invalid", Error = error } : x).ToArray();
        var status = new BatchStatus(id, "pending", 0, rows.Length, null, null, ManifestPath: manifest, Items: rows);
        var task = new BatchTaskFile(1, id, request with { Items = inputs.Select(x => x.Input).ToArray(), ExpectedInput = null, ExpectedRevision = null }, inputs.ToArray(), dependencies, status);
        await SaveBatchTaskAsync(manifest, task, token);
        return (task, manifest);
    }

    private static async Task<BatchTaskFile> ReadBatchTaskAsync(string path, CancellationToken token)
    {
        var task = JsonSerializer.Deserialize<BatchTaskFile>(await File.ReadAllTextAsync(path, token), JsonOptions) ?? throw new InvalidDataException("Invalid batch task.");
        if (task.Schema != 1 || !Guid.TryParseExact(task.Id, "N", out _) || task.Inputs == null || task.Settings == null || task.Dependencies == null ||
            task.Status?.Items == null || task.Status.Id != task.Id || task.Inputs.Length != task.Status.Items.Length || task.Settings.Items.Length != task.Inputs.Length)
            throw new InvalidDataException("Unsupported or damaged batch task.");
        PreflightBatch(task.Settings); // Bounds and settings validation; missing sources may be repaired before retry.
        for (var i = 0; i < task.Inputs.Length; i++)
        {
            var item = task.Inputs[i];
            if (item.Input != task.Settings.Items[i] || task.Status.Items[i].Input != item.Input || task.Status.Items[i].Index != i ||
                (item.Preset != null && (Path.GetFileName(item.Preset) != item.Preset || !item.Preset.StartsWith("preset-", StringComparison.Ordinal) || !item.Preset.EndsWith(".ini", StringComparison.Ordinal))))
                throw new InvalidDataException("Damaged task inputs or frozen preset path.");
        }
        return task;
    }

    private async Task<BatchStatus> RunBatchAsync(BatchTaskFile task, string manifest, bool retryFailedOnly, CancellationToken token)
    {
        var rows = Job!.Items!.ToArray();
        var directory = Path.GetDirectoryName(manifest)!;
        var protectedPaths = BatchSources(task.Inputs.Select(x => x.Input));
        var attemptId = Guid.NewGuid().ToString("N"); // Opaque Add-on state can never reuse a previous run.
        var checkpointPath = Path.Combine(Path.GetDirectoryName(task.Inputs.First(x => x.Preset != null).Input.OutputPath)!, ".OfflineReShade.batch.json");
        var checkpoint = new BatchCheckpoint(BatchSignature, new(StringComparer.OrdinalIgnoreCase));
        try
        {
            if (File.Exists(checkpointPath))
            {
                try
                {
                    var saved = JsonSerializer.Deserialize<BatchCheckpoint>(await File.ReadAllTextAsync(checkpointPath, token), JsonOptions);
                    if (saved?.Signature == BatchSignature && saved.Done != null) checkpoint = saved with { Done = new(saved.Done, StringComparer.OrdinalIgnoreCase) };
                }
                catch (JsonException) { /* Unreadable cache is not evidence of a successful export. */ }
            }
            for (var i = 0; i < rows.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                if (retryFailedOnly && rows[i].State is not ("failed" or "invalid")) continue;
                var frozen = task.Inputs[i];
                var previous = rows[i];
                rows[i] = rows[i] with { State = "running", Error = null, Warning = null, OutputPath = null, Attempts = 0, SourceFingerprint = null, OutputHash = null };
                PublishBatch(rows, frozen.Input.ColorPath);
                try
                {
                    var preflight = PreflightBatch(task.Settings);
                    if (preflight.Items[i].State == "invalid" && (frozen.Preset == null || !IsOnlyMissingSourcePreset(preflight.Items[i], frozen)))
                        throw new IOException(preflight.Items[i].Error);
                    if (frozen.Preset == null) throw new IOException(frozen.Error ?? "This item has no frozen preset. Start a new task.");
                    var preset = Path.Combine(directory, frozen.Preset);
                    if (frozen.PresetHash != await FileHashAsync(preset, token)) throw new IOException("Frozen task preset is missing or changed. Start a new task.");
                    await VerifyBatchDependenciesAsync(task, token);
                    var input = ValidateInput(frozen.Input with { PresetPath = preset, WatchInput = false });
                    if (task.Settings.ConflictPolicy == "skip" && File.Exists(input.OutputPath))
                    { rows[i] = rows[i] with { State = "skipped", OutputPath = input.OutputPath, Warning = "Existing file skipped by policy; its contents were not validated." }; }
                    else
                    {
                        await _renderSession!.OpenPresetInputAsync(input, token);
                        await PrepareExportAsync(task.Settings.WarmupFrames, token);
                        await _call("save_preset", null, token);
                        await CaptureRuntimeDependenciesAsync(task, manifest, token);
                        var fingerprint = await RenderFingerprintAsync(frozen.Input, preset, task.Settings, attemptId, token);
                        // A saved task owns its completion evidence. Another task may have
                        // replaced or removed the optional shared cache in this directory.
                        var done = previous.State is "exported" or "reused" && previous.SourceFingerprint != null && previous.OutputHash != null && previous.OutputPath != null
                            ? new BatchFile(previous.SourceFingerprint, previous.OutputHash, previous.OutputPath) : checkpoint.Done.GetValueOrDefault(input.OutputPath);
                        var reusable = task.Settings.Resume && done != null && done.Source == fingerprint && File.Exists(done.Path) &&
                            string.Equals(Path.GetDirectoryName(done.Path), Path.GetDirectoryName(input.OutputPath), StringComparison.OrdinalIgnoreCase) && !protectedPaths.Contains(done.Path) &&
                            done.Hash == await FileHashAsync(done.Path, token);
                        if (reusable) rows[i] = rows[i] with { State = "reused", OutputPath = done!.Path, SourceFingerprint = done.Source, OutputHash = done.Hash };
                        else
                        {
                            for (var attempt = 0; ; attempt++)
                            {
                                rows[i] = rows[i] with { Attempts = attempt + 1 }; PublishBatch(rows, input.ColorPath);
                                try
                                {
                                    await PrepareExportAsync(task.Settings.WarmupFrames, token);
                                    var saved = await ImageExport.WriteAsync(input.OutputPath, task.Settings.ConflictPolicy == "skip" ? "fail" : task.Settings.ConflictPolicy,
                                        async (temporary, ct) =>
                                        {
                                            await VerifyBatchDependenciesAsync(task, ct);
                                            var rendered = await _call(task.Settings.ReShadeShot ? "save_screenshot" : "save_output", new { path = temporary }, ct);
                                            if (fingerprint != await RenderFingerprintAsync(frozen.Input, preset, task.Settings, attemptId, ct))
                                                throw new IOException("Inputs or dependencies changed during export; the previous output was preserved.");
                                            await VerifyBatchDependenciesAsync(task, ct);
                                            return rendered;
                                        }, token, path => !protectedPaths.Contains(path) && !task.Dependencies.ContainsKey(path));
                                    var output = saved.GetProperty("path").GetString()!;
                                    var outputHash = await FileHashAsync(output, token);
                                    rows[i] = rows[i] with { State = "exported", OutputPath = output, Warning = Text(saved, "warning"), SourceFingerprint = fingerprint, OutputHash = outputHash };
                                    checkpoint.Done[input.OutputPath] = new(fingerprint, outputHash, output);
                                    await WriteJsonAtomicAsync(checkpointPath, checkpoint, token);
                                    break;
                                }
                                catch (Exception e) when (e is not OperationCanceledException && attempt < task.Settings.Retries) { await Task.Delay(250, token); }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { rows[i] = rows[i] with { State = "cancelled", Error = "Cancelled before this item completed." }; throw; }
                catch (Exception error) { rows[i] = rows[i] with { State = "failed", Error = error.Message }; }
                PublishBatch(rows, null);
                await SaveBatchTaskAsync(manifest, task with { Status = Job! }, token);
            }
            PublishBatch(rows, null);
            return Job! with { State = Job!.Failed > 0 ? "failed" : "completed", Error = Job.Failed > 0 ? $"{Job.Failed} item(s) failed. Review the individual results and retry after fixing the cause." : null };
        }
        catch (OperationCanceledException) { PublishBatch(rows, null); return Job! with { State = "cancelled" }; }
        catch (Exception error) { PublishBatch(rows, null); return Job! with { State = "failed", Error = error.Message }; }
    }

    // Original preset availability is irrelevant after its content has been frozen.
    private static bool IsOnlyMissingSourcePreset(BatchItemResult row, FrozenBatchItem item) =>
        item.Preset != null && row.Error?.StartsWith("Preset not found.", StringComparison.Ordinal) == true;

    private void PublishBatch(BatchItemResult[] rows, string? current)
    {
        Job = Job! with { Items = rows.ToArray(), Current = current,
            Completed = rows.Count(x => x.State is "exported" or "reused" or "skipped" or "failed" or "invalid" or "cancelled"),
            Warning = rows.Any(x => !string.IsNullOrEmpty(x.Warning)) ? "Some items have warnings or were skipped; review the individual results." : null };
        Changed?.Invoke();
    }
    private static string JoinError(string? first, string second) => first == null ? second : first + "; " + second;
    private static IEnumerable<string> EffectFiles(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .Where(x => new[] { ".fx", ".fxh", ".addonfx", ".addon", ".addon64", ".dll", ".png", ".dds", ".jpg", ".jpeg", ".bmp", ".tga" }.Contains(Path.GetExtension(x).ToLowerInvariant()))
        .Select(Path.GetFullPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
    private static async Task VerifyBatchDependenciesAsync(BatchTaskFile task, CancellationToken token)
    {
        foreach (var pair in task.Dependencies)
            if (!File.Exists(pair.Key) || pair.Value != await FileHashAsync(pair.Key, token)) throw new IOException("Task effect dependency changed: " + pair.Key + ". Start a new task to use the changed effects.");
        foreach (var dir in task.Inputs.Where(x => x.Preset != null).Select(x => x.Input.EffectDir).Distinct(StringComparer.OrdinalIgnoreCase))
            if (Directory.Exists(dir)) foreach (var path in EffectFiles(dir))
                if (!task.Dependencies.ContainsKey(path)) throw new IOException("Task effect discovery changed: " + path + ". Start a new task.");
    }
    private async Task CaptureRuntimeDependenciesAsync(BatchTaskFile task, string manifest, CancellationToken token)
    {
        var health = await _call("validate_export", null, token);
        if (health.TryGetProperty("dependencies", out var dependencies))
            foreach (var path in dependencies.GetString()!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var full = Path.GetFullPath(path);
                var hash = await FileHashAsync(full, token);
                if (task.Dependencies.TryGetValue(full, out var prior) && hash != prior) throw new IOException("Task effect dependency changed: " + full);
                task.Dependencies[full] = hash;
            }
        await SaveBatchTaskAsync(manifest, task with { Status = Job! }, token);
    }
    private async Task<string> RenderFingerprintAsync(RenderInput logicalInput, string frozenPreset, BatchRequest request, string attemptId, CancellationToken token)
    {
        var state = await _call("list_state", null, token);
        var runtime = await _call("get_runtime_info", null, token);
        var health = await _call("validate_export", null, token);
        var parts = new List<string> { BuildContract.ProductVersion, BatchSignature, JsonSerializer.Serialize(logicalInput, JsonOptions), request.WarmupFrames.ToString(), request.ReShadeShot.ToString(),
            "frozenPreset:" + await FileHashAsync(frozenPreset, token) };
        foreach (var key in new[] { "techniques", "uniforms", "preprocessorDefinitions" }) if (state.TryGetProperty(key, out var value)) parts.Add(key + value.GetRawText());
        foreach (var key in new[] { "effectsEnabled", "width", "height", "protocolVersion", "adapterLuid" }) if (runtime.TryGetProperty(key, out var value)) parts.Add(key + value.GetRawText());
        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { logicalInput.ColorPath };
        if (logicalInput.DepthPath.Length > 0) paths.Add(logicalInput.DepthPath);
        // Host and frozen-preset filenames are private, unstable identities; hash their contents.
        if (runtime.TryGetProperty("presetPath", out var preset) && !string.IsNullOrEmpty(preset.GetString())) parts.Add("workingPreset:" + await FileHashAsync(preset.GetString()!, token));
        if (health.TryGetProperty("dependencies", out var dependencies)) foreach (var path in dependencies.GetString()!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) paths.Add(path);
        if (Directory.Exists(logicalInput.EffectDir)) foreach (var path in EffectFiles(logicalInput.EffectDir)) paths.Add(path);
        foreach (var path in paths) parts.Add(Path.GetFullPath(path) + ":" + await FileHashAsync(path, token));
        if (state.TryGetProperty("addons", out var addons) && addons.TryGetProperty("addons", out var list) && list.EnumerateArray().Any(x =>
            x.TryGetProperty("loaded", out var loaded) && loaded.GetBoolean() && ((x.TryGetProperty("file", out var file) && !string.IsNullOrEmpty(file.GetString())) ||
            (x.TryGetProperty("external", out var external) && external.GetBoolean())))) parts.Add(attemptId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts))));
    }
    private static async Task<string> FileHashAsync(string path, CancellationToken token) { await using var s = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(s, token)); }
    private static Task SaveBatchTaskAsync(string path, BatchTaskFile task, CancellationToken token) => WriteJsonAtomicAsync(path, task, token);
    private static async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, JsonOptions), token); token.ThrowIfCancellationRequested(); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
