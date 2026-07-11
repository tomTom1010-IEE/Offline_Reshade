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
        bool hasEventOverlay,
        IReadOnlyList<string> overlays,
        string offlineCompatibility,
        IReadOnlyList<AddonEventViewModel> events)
    {
        Name = name;
        Description = description;
        File = file;
        Author = author;
        IsLoaded = isLoaded;
        IsExternal = isExternal;
        HasSettingsOverlay = hasSettingsOverlay;
        HasEventOverlay = hasEventOverlay;
        Overlays = overlays;
        OfflineCompatibility = offlineCompatibility;
        Events = events;
    }

    public string Name { get; }
    public string Description { get; }
    public string File { get; }
    public string Author { get; }
    public bool IsLoaded { get; }
    public bool IsExternal { get; }
    public bool HasSettingsOverlay { get; }
    public bool HasEventOverlay { get; }
    public IReadOnlyList<string> Overlays { get; }
    public string OfflineCompatibility { get; }
    public IReadOnlyList<AddonEventViewModel> Events { get; }

    public string SourceText => string.IsNullOrWhiteSpace(File) ? "Built-in" : File;
    public string StatusText => IsLoaded ? "Loaded" : "Disabled or failed";
    public string CompatibilityText => OfflineCompatibility == "runtime_compatible"
        ? "Offline runtime compatible"
        : "Partial compatibility: this add-on requests events that need a live game render stream or the native ReShade overlay";
    public string OverlayText
    {
        get
        {
            var count = Overlays.Count + (HasSettingsOverlay ? 1 : 0) + (HasEventOverlay ? 1 : 0);
            return count == 0 ? "No registered UI overlays" : $"{count} registered UI overlay{(count == 1 ? string.Empty : "s")}";
        }
    }
}
