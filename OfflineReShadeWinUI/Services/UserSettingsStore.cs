using System.Text.Json;

namespace OfflineReShade.WinUI.Services;

public sealed class UserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    public UserSettingsStore(AppPaths paths)
    {
        SettingsPath = paths.UserSettingsPath;
    }

    public string SettingsPath { get; }

    public PersistedUiSettings? Load()
    {
        if (!File.Exists(SettingsPath))
            return null;

        using var stream = File.OpenRead(SettingsPath);
        return JsonSerializer.Deserialize<PersistedUiSettings>(stream, JsonOptions);
    }

    public async Task SaveAsync(PersistedUiSettings settings, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = SettingsPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        File.Move(tempPath, SettingsPath, true);
    }

    public void Save(PersistedUiSettings settings)
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = SettingsPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tempPath, SettingsPath, true);
    }
}

public sealed class PersistedUiSettings
{
    public string? ColorPath { get; set; }
    public string? DepthPath { get; set; }
    public string? DepthFormat { get; set; }
    public string? DepthProfile { get; set; }
    public string? DepthDownsample { get; set; }
    public string? EffectDir { get; set; }
    public string? PresetPath { get; set; }
    public string? OutputPath { get; set; }
    public string? RenderWidth { get; set; }
    public string? RenderHeight { get; set; }
    public string? PreviewTransport { get; set; }
    public string? GalleryInputFolder { get; set; }
    public string? GalleryOutputFolder { get; set; }
    public string? GalleryBatchFrameDelay { get; set; }
    public bool? ShowFps { get; set; }
    public string? InputMode { get; set; }
    public PersistedProfilePaths? KksPaths { get; set; }
    public PersistedProfilePaths? KkPaths { get; set; }
}

public sealed class PersistedProfilePaths
{
    public string? ColorPath { get; set; }
    public string? DepthPath { get; set; }
    public string? DepthFormat { get; set; }
    public string? OutputPath { get; set; }
    public string? GalleryInputFolder { get; set; }
    public string? GalleryOutputFolder { get; set; }
}
