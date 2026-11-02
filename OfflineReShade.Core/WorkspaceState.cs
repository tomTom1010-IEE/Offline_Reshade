using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OfflineReShade.Core;

// Text fields deliberately retain unfinished edits. Validation happens at the apply boundary.
public sealed record EditorDocument
{
    public string ColorPath { get; init; } = "";
    public string DepthPath { get; init; } = "";
    public string DepthFormat { get; init; } = "raw";
    public string DepthProfile { get; init; } = "kks";
    public string DepthDownsample { get; init; } = "max2x2";
    public string EffectDir { get; init; } = "";
    public string PresetPath { get; init; } = "";
    public string OutputPath { get; init; } = "";
    public string RenderWidth { get; init; } = "";
    public string RenderHeight { get; init; } = "";
    public string InputMode { get; init; } = "RealTime";

    public RenderInput ToRenderInput(string? baseDirectory = null)
    {
        var width = ParseDimension(RenderWidth, "Width");
        var height = ParseDimension(RenderHeight, "Height");
        if ((width == 0) != (height == 0)) throw new ArgumentException("Set both output dimensions, or leave both blank for source size.");
        if ((long)width * height > PreviewGeometry.MaxRenderPixels) throw new ArgumentException("Output exceeds the 64 megapixel resource limit.");
        string Resolve(string path) => baseDirectory == null || string.IsNullOrWhiteSpace(path) ? path : Path.GetFullPath(path, baseDirectory);
        return new(Resolve(ColorPath), Resolve(DepthPath), DepthFormat, DepthProfile, DepthDownsample, Resolve(OutputPath), Resolve(EffectDir), Resolve(PresetPath),
            width, height, WatchInput: InputMode == "RealTime");
    }

    public static int ParseDimension(string text, string name)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value is < 0 or > 16384)
            throw new ArgumentException($"{name} must be an integer between 1 and 16384, or blank/zero for source size.");
        return value;
    }
}

public sealed record ProfilePaths
{
    public string? ColorPath { get; init; }
    public string? DepthPath { get; init; }
    public string? DepthFormat { get; init; }
    public string? OutputPath { get; init; }
    public string? GalleryInputFolder { get; init; }
    public string? GalleryOutputFolder { get; init; }
}

public sealed record ApplicationPreferences
{
    public string PreviewTransport { get; init; } = "GPU";
    public string GalleryInputFolder { get; init; } = "";
    public string GalleryOutputFolder { get; init; } = "";
    public string GalleryBatchFrameDelay { get; init; } = "5";
    public bool ShowFps { get; init; } = true;
    public int SliderDragSensitivity { get; init; } = 1;
    public bool SliderSymLog { get; init; }
    public ProfilePaths KksPaths { get; init; } = new();
    public ProfilePaths KkPaths { get; init; } = new();
}

public sealed record WorkspaceConfiguration
{
    public int SchemaVersion { get; init; } = 2;
    public EditorDocument Editor { get; init; } = new();
    public ApplicationPreferences Preferences { get; init; } = new();
    public string? SessionPresetPath { get; init; }
}

public static class WorkspaceConfigurationCodec
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true, AllowTrailingCommas = true };
    public static string Write(WorkspaceConfiguration value) => JsonSerializer.Serialize(value, Options);

    public static WorkspaceConfiguration Read(string json, WorkspaceConfiguration defaults)
    {
        var root = JsonNode.Parse(json, documentOptions: new() { AllowTrailingCommas = true }) as JsonObject
            ?? throw new JsonException("Settings must be an object.");
        JsonNode? Find(JsonObject obj, string key) => obj.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
        var version = Find(root, "schemaVersion")?.GetValue<int>() ?? 1;
        if (version is < 1 or > 2) throw new InvalidDataException($"Unsupported settings schema {version}; the file has been preserved.");
        T Merge<T>(T baseline, JsonObject source)
        {
            var result = JsonSerializer.SerializeToNode(baseline, Options)!.AsObject();
            foreach (var key in result.Select(x => x.Key).ToArray())
                if (Find(source, key) is { } value) result[key] = value.DeepClone();
            return result.Deserialize<T>(Options)!;
        }
        var editor = version == 1 ? root : Find(root, "editor") as JsonObject ?? throw new JsonException("Missing editor settings.");
        var prefs = version == 1 ? root : Find(root, "preferences") as JsonObject ?? throw new JsonException("Missing application preferences.");
        return new() { Editor = Merge(defaults.Editor, editor), Preferences = Merge(defaults.Preferences, prefs),
            SessionPresetPath = Find(root, "sessionPresetPath")?.GetValue<string>() };
    }
}

public sealed record AppliedInput(RenderInput Input, long Revision);
public sealed record ApplyTicket(long Operation, long Revision, RenderInput Input);

// Implemented by the shared operation layer; views never own a separate history.
public interface IEditHistory
{
    bool CanUndo { get; }
    bool CanRedo { get; }
    bool IsModified { get; }
    Task UndoAsync(CancellationToken token = default);
    Task RedoAsync(CancellationToken token = default);
    void MarkSaved();
}

// Runtime state is intentionally never serialized as preferences or inferred from a saved file.
public sealed class EditingSession
{
    public EditorDocument Draft { get; private set; }
    public AppliedInput? Applied { get; private set; }
    public long Revision { get; private set; }
    private long _operation;
    public event Action? Changed;
    public EditingSession(EditorDocument draft) => Draft = draft;
    public void Edit(EditorDocument document)
    {
        if (document == Draft) return;
        Draft = document; Revision++; Changed?.Invoke();
    }
    public string? ValidationError { get { try { Draft.ToRenderInput(); return null; } catch (ArgumentException ex) { return ex.Message; } } }
    public bool HasPendingChanges => Applied == null || ValidationError != null || Draft.ToRenderInput() != Applied.Input;
    public ApplyTicket BeginApply(RenderInput input) => new(++_operation, Revision, input);
    public bool AppliedSuccessfully(ApplyTicket ticket)
    {
        if (ticket.Operation != _operation) return false;
        Applied = new(ticket.Input, ticket.Revision); Changed?.Invoke(); return true;
    }
    public void Disconnected() { _operation++; Applied = null; Changed?.Invoke(); }
}
