using OfflineReShade.Core;
using OfflineReShade.WinUI.Mvvm;
using OfflineReShade.WinUI.Services;
using System.Runtime.CompilerServices;

namespace OfflineReShade.WinUI.ViewModels;

// Binding adapter; values are owned by the document or preferences, never duplicate fields.
public sealed class SettingsViewModel : ObservableObject
{
    public EditingSession Session { get; }
    public EditorDocument Editor => Session.Draft;
    public ApplicationPreferences Preferences { get; private set; } = new();
    public SettingsViewModel(AppPaths paths)
    {
        Session = new(new EditorDocument { ColorPath = paths.DefaultColorPath, DepthPath = paths.DefaultDepthPath,
            EffectDir = paths.DefaultEffectDir, OutputPath = paths.DefaultOutputPath });
    }
    private void Edit(EditorDocument next, [CallerMemberName] string? name = null)
    {
        if (next == Editor) return;
        Session.Edit(next); OnPropertyChanged(name);
    }
    private void Prefer(ApplicationPreferences next, [CallerMemberName] string? name = null)
    {
        if (next == Preferences) return;
        Preferences = next; OnPropertyChanged(name);
    }
    public string ColorPath { get => Editor.ColorPath; set => Edit(Editor with { ColorPath = value }); }
    public string DepthPath { get => Editor.DepthPath; set => Edit(Editor with { DepthPath = value }); }
    public string DepthFormat { get => Editor.DepthFormat; set => Edit(Editor with { DepthFormat = value }); }
    public string DepthProfile { get => Editor.DepthProfile; set => Edit(Editor with { DepthProfile = value }); }
    public string DepthDownsample { get => Editor.DepthDownsample; set => Edit(Editor with { DepthDownsample = value }); }
    public string EffectDir { get => Editor.EffectDir; set => Edit(Editor with { EffectDir = value }); }
    public string PresetPath { get => Editor.PresetPath; set => Edit(Editor with { PresetPath = value }); }
    public string OutputPath { get => Editor.OutputPath; set => Edit(Editor with { OutputPath = value }); }
    public string RenderWidth { get => Editor.RenderWidth; set => Edit(Editor with { RenderWidth = value }); }
    public string RenderHeight { get => Editor.RenderHeight; set => Edit(Editor with { RenderHeight = value }); }
    public string InputMode { get => Editor.InputMode; set => Edit(Editor with { InputMode = value }); }
    public string PreviewTransport { get => Preferences.PreviewTransport; set => Prefer(Preferences with { PreviewTransport = value }); }
    public string GalleryInputFolder { get => Preferences.GalleryInputFolder; set => Prefer(Preferences with { GalleryInputFolder = value }); }
    public string GalleryOutputFolder { get => Preferences.GalleryOutputFolder; set => Prefer(Preferences with { GalleryOutputFolder = value }); }
    public string GalleryBatchFrameDelay { get => Preferences.GalleryBatchFrameDelay; set => Prefer(Preferences with { GalleryBatchFrameDelay = value }); }
    public bool ShowFps { get => Preferences.ShowFps; set => Prefer(Preferences with { ShowFps = value }); }
    public int SliderDragSensitivity { get => Preferences.SliderDragSensitivity; set => Prefer(Preferences with { SliderDragSensitivity = value is 2 or 4 or 8 ? value : 1 }); }
    public bool SliderSymLog { get => Preferences.SliderSymLog; set => Prefer(Preferences with { SliderSymLog = value }); }
    public ProfilePaths KksPaths { get => Preferences.KksPaths; set => Prefer(Preferences with { KksPaths = value }); }
    public ProfilePaths KkPaths { get => Preferences.KkPaths; set => Prefer(Preferences with { KkPaths = value }); }
}
