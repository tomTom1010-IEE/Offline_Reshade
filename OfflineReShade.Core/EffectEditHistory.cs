using System.Text.Json;

namespace OfflineReShade.Core;

public sealed record EditableUniform(string Id, string Type, JsonElement Value);
public sealed record EditableTechnique(string Id, bool Enabled);
public sealed record EditableDefinition(string Id, string EffectName, string Name, string Value);

// Only values that the public FX API can read and restore belong in this snapshot.
// Source-driven uniforms and opaque Add-on state must never masquerade as editable FX.
public sealed class EffectSnapshot
{
    public IReadOnlyList<EditableUniform> Uniforms { get; }
    public IReadOnlyList<EditableTechnique> Techniques { get; }
    public IReadOnlyList<EditableDefinition> Definitions { get; }
    public bool EffectsEnabled { get; }
    private readonly string _content;
    public int Size => _content.Length;
    private EffectSnapshot(EditableUniform[] uniforms, EditableTechnique[] techniques, EditableDefinition[] definitions, bool enabled)
    {
        Uniforms = Array.AsReadOnly(uniforms); Techniques = Array.AsReadOnly(techniques); Definitions = Array.AsReadOnly(definitions); EffectsEnabled = enabled;
        _content = JsonSerializer.Serialize(new { uniforms, techniques, definitions, enabled });
    }
    public bool SameAs(EffectSnapshot? other) => other != null && _content == other._content;
    public static EffectSnapshot Read(JsonElement value)
    {
        var uniforms = value.GetProperty("uniforms").EnumerateArray().Select(x => new EditableUniform(
            x.GetProperty("id").GetString()!, x.GetProperty("type").GetString()!, x.GetProperty("value").Clone())).OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        foreach (var u in uniforms)
        {
            if (string.IsNullOrEmpty(u.Id) || u.Value.ValueKind != JsonValueKind.Array || u.Value.GetArrayLength() is < 1 or > 16)
                throw new InvalidDataException("The renderer returned an invalid editable uniform.");
            foreach (var component in u.Value.EnumerateArray())
                if (component.ValueKind is not (JsonValueKind.True or JsonValueKind.False) &&
                    (component.ValueKind != JsonValueKind.Number || !component.TryGetDouble(out var number) || !double.IsFinite(number)))
                    throw new InvalidDataException("The renderer returned a non-finite or unsupported uniform value: " + u.Id);
        }
        var techniques = value.GetProperty("techniques").EnumerateArray().Select(x => new EditableTechnique(x.GetProperty("id").GetString()!, x.GetProperty("enabled").GetBoolean())).ToArray();
        var definitions = value.GetProperty("preprocessorDefinitions").EnumerateArray().Select(x => new EditableDefinition(
            x.GetProperty("id").GetString()!, x.GetProperty("effectName").GetString()!, x.GetProperty("name").GetString()!, x.GetProperty("value").GetString()!))
            .OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        if (uniforms.Select(x => x.Id).Distinct().Count() != uniforms.Length || techniques.Select(x => x.Id).Distinct().Count() != techniques.Length ||
            definitions.Select(x => x.Id).Distinct().Count() != definitions.Length) throw new InvalidDataException("Duplicate editable state IDs.");
        return new(uniforms, techniques, definitions, value.GetProperty("effectsEnabled").GetBoolean());
    }
}

public sealed record EditHistoryStatus(bool Available, bool CanUndo, bool CanRedo, bool IsModified, long Revision,
    string? UndoLabel, string? RedoLabel, string? TransactionId, string? Warning,
    string AddonCapability = "Third-party Add-ons manage their own private state; FX history and preset saving do not cover that state.");

// The shared OperationCoordinator serializes all calls, including reads that reconcile
// external native changes. Status is an immutable cached value safe for status polling.
public sealed class EffectEditHistory : IEditHistory
{
    private sealed record Entry(EffectSnapshot Before, EffectSnapshot After, string Label, string? Group);
    private readonly Func<string, object?, CancellationToken, Task<JsonElement>> _call;
    private readonly Func<CancellationToken, Task> _settle;
    private readonly Func<CancellationToken, Task> _settleDefinitions;
    private readonly List<Entry> _entries = new();
    private EffectSnapshot? _current, _saved;
    private int _cursor;
    private long _revision;
    private string? _group, _closedGroup, _warning;
    private bool _uncertain;
    private volatile EditHistoryStatus _status = new(false, false, false, false, 0, null, null, null, null);
    public EditHistoryStatus Status => _status;
    public bool CanUndo => Status.CanUndo;
    public bool CanRedo => Status.CanRedo;
    public bool IsModified => Status.IsModified;
    public EffectSnapshot? Current => _current;
    public event Action? Changed;
    public EffectEditHistory(Func<string, object?, CancellationToken, Task<JsonElement>> call, Func<CancellationToken, Task> settle, Func<CancellationToken, Task>? settleDefinitions = null)
    { _call = call; _settle = settle; _settleDefinitions = settleDefinitions ?? settle; }
    private void Publish()
    {
        _status = new(_current != null && !_uncertain, _group == null && !_uncertain && _cursor > 0,
            _group == null && !_uncertain && _cursor < _entries.Count, _uncertain || (_current != null && !_current.SameAs(_saved)),
            _revision, _cursor > 0 ? _entries[_cursor - 1].Label : null, _cursor < _entries.Count ? _entries[_cursor].Label : null, _group, _warning);
        Changed?.Invoke();
    }
    public void ResetContext()
    {
        _entries.Clear(); _cursor = 0; _current = _saved = null; _uncertain = false; _warning = null;
        _closedGroup = _group; _group = null; _revision++; Publish();
    }
    public void Invalidate(string reason)
    {
        _entries.Clear(); _cursor = 0; _uncertain = true; _warning = reason;
        _closedGroup = _group; _group = null; _revision++; Publish();
    }
    public void CheckRevision(long? expected)
    {
        if (expected.HasValue && expected.Value != _revision) throw new InvalidOperationException("Effects changed since the decision was made. Review the unsaved changes again.");
    }
    public string BeginTransaction()
    {
        RequireAccess(null);
        _group = Guid.NewGuid().ToString("N"); Publish(); return _group;
    }
    public void EndTransaction(string id)
    {
        if (_group == null && _closedGroup == id) return;
        RequireAccess(id);
        if (_group == null) throw new InvalidOperationException("Edit transaction is no longer active.");
        _closedGroup = _group; _group = null; Publish();
    }
    public void CloseTransaction()
    { if (_group != null) { _closedGroup = _group; _group = null; Publish(); } }
    public void RequireAccess(string? transactionId)
    {
        if (_group != transactionId) throw new InvalidOperationException(_group != null
            ? "An edit transaction owns the session. Finish or cancel it before another operation."
            : "This edit transaction is no longer active.");
    }
    private async Task<EffectSnapshot> ReadAsync(CancellationToken token) => EffectSnapshot.Read(await _call("get_edit_state", null, token));
    public async Task<EffectSnapshot> SynchronizeAsync(CancellationToken token = default)
    {
        await _settle(token);
        var actual = await ReadAsync(token);
        if (_current == null) { _current = _saved = actual; _revision++; }
        else if (!actual.SameAs(_current))
        {
            _entries.Clear(); _cursor = 0; _current = actual; _revision++;
            _warning = "FX state changed outside the shared editor or its schema changed. Previous undo history was cleared; current edits are preserved.";
        }
        Publish(); return actual;
    }
    public void MarkSaved()
    {
        RequireAccess(null);
        if (_current == null || _uncertain) throw new InvalidOperationException("The current FX state could not be verified; it cannot be marked saved.");
        _saved = _current; _warning = null; _revision++; Publish();
    }
    public async Task<JsonElement> ChangeAsync(string label, string? transactionId, Func<CancellationToken, Task<JsonElement>> change, CancellationToken token = default)
    {
        RequireAccess(transactionId);
        var before = await SynchronizeAsync(token);
        if (_uncertain) throw new InvalidOperationException("Reload a preset to recover the unverified FX state before editing.");
        JsonElement result;
        EffectSnapshot after;
        try { result = await change(token); await _settle(token); after = await ReadAsync(token); token.ThrowIfCancellationRequested(); }
        catch (Exception error) { await RollbackAsync(before, error); throw; }
        if (!after.SameAs(before))
        {
            if (_cursor < _entries.Count) _entries.RemoveRange(_cursor, _entries.Count - _cursor);
            if (transactionId != null && _cursor > 0 && _entries[_cursor - 1].Group == transactionId)
            {
                var first = _entries[_cursor - 1].Before;
                if (first.SameAs(after)) { _entries.RemoveAt(--_cursor); }
                else _entries[_cursor - 1] = new(first, after, label, transactionId);
            }
            else { _entries.Add(new(before, after, label, transactionId)); _cursor++; }
            // Bound live history independently of the saved-content baseline.
            while (_entries.Count > 1 && (_entries.Count > 100 || _entries.Sum(x => (long)x.Before.Size + x.After.Size) > 16 * 1024 * 1024))
            { _entries.RemoveAt(0); _cursor--; }
            _current = after; _revision++; Publish();
        }
        return result;
    }
    public Task UndoAsync(CancellationToken token = default) => MoveAsync(false, token);
    public Task RedoAsync(CancellationToken token = default) => MoveAsync(true, token);
    private async Task MoveAsync(bool redo, CancellationToken token)
    {
        RequireAccess(null);
        var before = await SynchronizeAsync(token);
        if (redo ? !CanRedo : !CanUndo) throw new InvalidOperationException(redo ? "Nothing can be redone." : "Nothing can be undone.");
        var target = redo ? _entries[_cursor].After : _entries[_cursor - 1].Before;
        try { await RestoreAsync(target, token); token.ThrowIfCancellationRequested(); }
        catch (Exception error) { await RollbackAsync(before, error); throw; }
        _cursor += redo ? 1 : -1; _current = target; _revision++; Publish();
    }
    private async Task RollbackAsync(EffectSnapshot before, Exception error)
    {
        try { using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await RestoreAsync(before, rollback.Token); }
        catch (Exception restoreError)
        {
            _entries.Clear(); _cursor = 0; _uncertain = true; _revision++;
            _warning = "FX rollback failed. Reload a known preset before continuing: " + restoreError.Message; Publish();
            throw new IOException(error.Message + " " + _warning, error);
        }
    }
    private async Task RestoreAsync(EffectSnapshot target, CancellationToken token)
    {
        // Read before waiting: a bad preprocessor edit may have failed compilation.
        var actual = await ReadAsync(token);
        var definitionsChanged = false;
        foreach (var definition in actual.Definitions.Concat(target.Definitions).DistinctBy(x => x.Id))
        {
            var desired = target.Definitions.FirstOrDefault(x => x.Id == definition.Id);
            var present = actual.Definitions.FirstOrDefault(x => x.Id == definition.Id);
            if ((desired?.Value ?? "") == (present?.Value ?? "")) continue;
            await _call("set_preprocessor_definition", new { definition.EffectName, definition.Name, value = desired?.Value ?? "" }, token);
            definitionsChanged = true;
        }
        if (definitionsChanged) { await _settleDefinitions(token); actual = await ReadAsync(token); }
        // A changed source shader must not be partially restored using mismatched IDs.
        if (!target.Uniforms.Select(x => (x.Id, x.Type, Count: x.Value.GetArrayLength())).SequenceEqual(actual.Uniforms.Select(x => (x.Id, x.Type, Count: x.Value.GetArrayLength()))) ||
            !target.Techniques.Select(x => x.Id).Order().SequenceEqual(actual.Techniques.Select(x => x.Id).Order()))
            throw new InvalidOperationException("The effect schema no longer matches this history entry.");
        foreach (var uniform in target.Uniforms)
            if (uniform.Value.GetRawText() != actual.Uniforms.First(x => x.Id == uniform.Id).Value.GetRawText())
                await _call("set_uniform", new { uniform.Id, value = uniform.Value }, token);
        foreach (var technique in target.Techniques)
            if (actual.Techniques.First(x => x.Id == technique.Id).Enabled != technique.Enabled)
                await _call("set_technique_state", new { technique.Id, technique.Enabled }, token);
        if (!target.Techniques.Select(x => x.Id).SequenceEqual(actual.Techniques.Select(x => x.Id)))
            await _call("reorder_techniques", new { ids = target.Techniques.Select(x => x.Id).ToArray() }, token);
        if (actual.EffectsEnabled != target.EffectsEnabled) await _call("set_effects_state", new { enabled = target.EffectsEnabled }, token);
        await _settle(token);
        if (!target.SameAs(await ReadAsync(token))) throw new IOException("The renderer did not restore the requested FX state.");
    }
}
