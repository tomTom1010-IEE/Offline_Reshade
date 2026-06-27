using OfflineReShade.WinUI.Mvvm;
using OfflineReShade.WinUI.Services;

namespace OfflineReShade.WinUI.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private string _colorPath;
    private string _depthPath;
    private string _effectDir;
    private string _presetPath = string.Empty;
    private string _outputPath;
    private string _renderWidth = string.Empty;
    private string _renderHeight = string.Empty;

    public SettingsViewModel(AppPaths paths)
    {
        _colorPath = paths.DefaultColorPath;
        _depthPath = paths.DefaultDepthPath;
        _effectDir = paths.DefaultEffectDir;
        _outputPath = paths.DefaultOutputPath;
    }

    public string ColorPath { get => _colorPath; set => SetProperty(ref _colorPath, value); }
    public string DepthPath { get => _depthPath; set => SetProperty(ref _depthPath, value); }
    public string EffectDir { get => _effectDir; set => SetProperty(ref _effectDir, value); }
    public string PresetPath { get => _presetPath; set => SetProperty(ref _presetPath, value); }
    public string OutputPath { get => _outputPath; set => SetProperty(ref _outputPath, value); }
    public string RenderWidth { get => _renderWidth; set => SetProperty(ref _renderWidth, value); }
    public string RenderHeight { get => _renderHeight; set => SetProperty(ref _renderHeight, value); }
}
