using OfflineReShade.Core;

namespace OfflineReShade.WinUI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool _previewOriginal;
    public bool PreviewOriginal { get => _previewOriginal; private set => SetProperty(ref _previewOriginal, value); }
    public async Task SetPreviewComparisonAsync(bool original)
    {
        var result = await _automation.ExecuteAsync("set_preview_comparison", AutomationEngine.Json(new { original }));
        PreviewOriginal = result.GetProperty("previewOriginal").GetBoolean();
    }
}
