namespace OfflineReShade.WinUI.ViewModels;

public sealed class AddonViewModel
{
    public AddonViewModel(
        string name,
        string description,
        string file,
        string author,
        bool isLoaded,
        bool isExternal,
        bool hasSettingsOverlay,
        IReadOnlyList<string> overlays)
    {
        Name = name;
        Description = description;
        File = file;
        Author = author;
        IsLoaded = isLoaded;
        IsExternal = isExternal;
        HasSettingsOverlay = hasSettingsOverlay;
        Overlays = overlays;
    }

    public string Name { get; }
    public string Description { get; }
    public string File { get; }
    public string Author { get; }
    public bool IsLoaded { get; }
    public bool IsExternal { get; }
    public bool HasSettingsOverlay { get; }
    public IReadOnlyList<string> Overlays { get; }

    public string SourceText => string.IsNullOrWhiteSpace(File) ? "Built-in" : File;
    public string StatusText => IsLoaded ? "Loaded" : "Disabled or failed";
    public string OverlayText
    {
        get
        {
            var count = Overlays.Count + (HasSettingsOverlay ? 1 : 0);
            return count == 0 ? "No registered UI overlays" : $"{count} registered UI overlay{(count == 1 ? string.Empty : "s")}";
        }
    }
}
