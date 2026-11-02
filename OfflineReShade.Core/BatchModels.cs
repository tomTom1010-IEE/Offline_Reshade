namespace OfflineReShade.Core;

public sealed record BatchRequest(RenderInput[] Items, int WarmupFrames = 5, bool ReShadeShot = true,
    bool Resume = true, int Retries = 1, string ConflictPolicy = "replace", long? ExpectedRevision = null,
    RenderInput? ExpectedInput = null, string?[]? InputErrors = null);
public sealed record BatchItemResult(int Index, RenderInput Input, string State = "pending", string? OutputPath = null,
    string? Error = null, string? Warning = null, int Attempts = 0, string? SourceFingerprint = null, string? OutputHash = null);
public sealed record BatchPreflight(BatchItemResult[] Items, int Valid, int Invalid, string[] OutputDirectories,
    string ConflictPolicy, string Validation = "File paths and settings checked. Image decoding, compilation, textures and depth are validated by the renderer before each export.");
public sealed record BatchStatus(string Id, string State, int Completed, int Total, string? Current, string? Error,
    string? Warning = null, string? ManifestPath = null, BatchItemResult[]? Items = null)
{
    public int Exported => Items?.Count(x => x.State == "exported") ?? 0;
    public int Reused => Items?.Count(x => x.State == "reused") ?? 0;
    public int Skipped => Items?.Count(x => x.State == "skipped") ?? 0;
    public int Failed => Items?.Count(x => x.State is "failed" or "invalid") ?? 0;
    public int Cancelled => Items?.Count(x => x.State == "cancelled") ?? 0;
}
