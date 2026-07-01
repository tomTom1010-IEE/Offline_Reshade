using Microsoft.UI.Xaml;
using WinRT.Interop;
using Windows.Storage.Pickers;

namespace OfflineReShade.WinUI.Services;

public sealed class SettingsPickerService
{
    private readonly Func<IntPtr> _windowHandleProvider;

    public SettingsPickerService(Func<IntPtr> windowHandleProvider)
    {
        _windowHandleProvider = windowHandleProvider;
    }

    public async Task<string?> PickPngAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, _windowHandleProvider());
        picker.FileTypeFilter.Add(".png");
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task<string?> PickDepthAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, _windowHandleProvider());
        picker.FileTypeFilter.Add(".rfloat");
        picker.FileTypeFilter.Add(".png");
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task<string?> PickIniAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, _windowHandleProvider());
        picker.FileTypeFilter.Add(".ini");
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task<string?> PickOutputPngAsync()
    {
        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, _windowHandleProvider());
        picker.FileTypeChoices.Add("PNG", new List<string> { ".png" });
        picker.SuggestedFileName = "reshadeoutput.png";
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    public async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, _windowHandleProvider());
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
