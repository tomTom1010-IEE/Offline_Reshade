using Microsoft.UI.Xaml.Media.Imaging;
using OfflineReShade.WinUI.Mvvm;

namespace OfflineReShade.WinUI.ViewModels;

public sealed class GalleryItemViewModel : ObservableObject
{
    public GalleryItemViewModel(string baseName, string colorPath, string depthPath, string depthFormat, string outputPath, bool isValid)
    {
        BaseName = baseName;
        ColorPath = colorPath;
        DepthPath = depthPath;
        DepthFormat = depthFormat;
        OutputPath = outputPath;
        IsValid = isValid;
        if (File.Exists(colorPath))
            Thumbnail = new BitmapImage(new Uri(colorPath));
    }

    public string BaseName { get; }
    public string ColorPath { get; }
    public string DepthPath { get; }
    public string DepthFormat { get; }
    public string OutputPath { get; }
    public bool IsValid { get; }
    public BitmapImage? Thumbnail { get; }
    public string StatusText => IsValid ? DepthFormat.ToUpperInvariant() : "Missing depth";
}
