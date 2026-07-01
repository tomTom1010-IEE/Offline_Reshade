using OfflineReShade.WinUI.Mvvm;
using OfflineReShade.WinUI.Services;

namespace OfflineReShade.WinUI.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private string _colorPath;
    private string _depthPath;
    private string _depthFormat = "raw";
    private string _depthProfile = "kks";
    private string _depthDownsample = "max2x2";
    private string _effectDir;
    private string _presetPath = string.Empty;
    private string _outputPath;
    private string _renderWidth = string.Empty;
    private string _renderHeight = string.Empty;
    private string _previewTransport = "GPU";
    private string _galleryInputFolder = string.Empty;
    private string _galleryOutputFolder = string.Empty;
    private string _galleryBatchFrameDelay = "5";
    private bool _showFps = true;

    public SettingsViewModel(AppPaths paths)
    {
        _colorPath = paths.DefaultColorPath;
        _depthPath = paths.DefaultDepthPath;
        _effectDir = paths.DefaultEffectDir;
        _outputPath = paths.DefaultOutputPath;
    }

    public string ColorPath { get => _colorPath; set => SetProperty(ref _colorPath, value); }
    public string DepthPath { get => _depthPath; set => SetProperty(ref _depthPath, value); }
    public string DepthFormat { get => _depthFormat; set => SetProperty(ref _depthFormat, value); }
    public string DepthProfile { get => _depthProfile; set => SetProperty(ref _depthProfile, value); }
    public string DepthDownsample { get => _depthDownsample; set => SetProperty(ref _depthDownsample, value); }
    public string EffectDir { get => _effectDir; set => SetProperty(ref _effectDir, value); }
    public string PresetPath { get => _presetPath; set => SetProperty(ref _presetPath, value); }
    public string OutputPath { get => _outputPath; set => SetProperty(ref _outputPath, value); }
    public string RenderWidth { get => _renderWidth; set => SetProperty(ref _renderWidth, value); }
    public string RenderHeight { get => _renderHeight; set => SetProperty(ref _renderHeight, value); }
    public string PreviewTransport { get => _previewTransport; set => SetProperty(ref _previewTransport, value); }
    public string GalleryInputFolder { get => _galleryInputFolder; set => SetProperty(ref _galleryInputFolder, value); }
    public string GalleryOutputFolder { get => _galleryOutputFolder; set => SetProperty(ref _galleryOutputFolder, value); }
    public string GalleryBatchFrameDelay { get => _galleryBatchFrameDelay; set => SetProperty(ref _galleryBatchFrameDelay, value); }
    public bool ShowFps { get => _showFps; set => SetProperty(ref _showFps, value); }
}
