namespace OfflineReShade.WinUI.ViewModels;

public sealed class AddonOverlayViewModel
{
    public AddonOverlayViewModel(string addonName, string title, bool isSettings, string? displayName = null)
    {
        AddonName = addonName;
        Title = title;
        IsSettings = isSettings;
        DisplayName = displayName ?? title;
    }

    public string AddonName { get; }
    public string Title { get; }
    public bool IsSettings { get; }
    public string DisplayName { get; }
    public string Id => AddonName + "|" + (IsSettings ? "Settings" : Title);
    public string DisplayTitle => AddonName + " - " + (IsSettings ? "Settings" : DisplayName);
}
