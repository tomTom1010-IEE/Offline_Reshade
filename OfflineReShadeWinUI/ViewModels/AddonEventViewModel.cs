namespace OfflineReShade.WinUI.ViewModels;

public sealed class AddonEventViewModel
{
    public AddonEventViewModel(string name, string support)
    {
        Name = name;
        Support = support;
    }

    public string Name { get; }
    public string Support { get; }
    public bool IsSupported => string.Equals(Support, "supported", StringComparison.OrdinalIgnoreCase);
    public string SupportText => Support switch
    {
        "supported" => "Supported",
        "native_overlay_only" => "Native overlay only",
        "game_render_stream_unavailable" => "Unavailable without the game render stream",
        _ => Support
    };
}
