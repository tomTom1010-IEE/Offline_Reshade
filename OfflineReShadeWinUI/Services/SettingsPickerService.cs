using System.Security.Principal;
using Microsoft.UI.Xaml;
using WinRT.Interop;
using Windows.Storage.Pickers;

namespace OfflineReShade.WinUI.Services;

public sealed class SettingsPickerService
{
    // With UAC the elevated token is the only one in the Administrators role.
    private static readonly bool IsElevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    private readonly Func<IntPtr> _windowHandleProvider;

    public SettingsPickerService(Func<IntPtr> windowHandleProvider)
    {
        _windowHandleProvider = windowHandleProvider;
    }

    public Task<string?> PickPngAsync() => PickAsync(new("PNG image", [".png"]));

    public Task<string?> PickDepthAsync() => PickAsync(new("Depth file", [".rfloat", ".png"]));

    public Task<string?> PickIniAsync() => PickAsync(new("ReShade preset", [".ini"]));

    public Task<string?> PickBatchTaskAsync() => PickAsync(new("Batch task", [".json"]));

    public Task<string?> PickOutputPngAsync() => PickAsync(new("PNG image", [".png"], Save: true, SuggestedName: "reshadeoutput.png"));

    public Task<string?> PickPresetSaveAsync() => PickAsync(new("ReShade preset", [".ini"], Save: true, SuggestedName: "Preset.ini"));

    public Task<string?> PickFolderAsync() => PickAsync(new("Folder", [], Folder: true));

    private sealed record PickerRequest(string FilterName, string[] Extensions, bool Save = false, bool Folder = false, string? SuggestedName = null);

    // The WinRT pickers are brokered through PickerHost.exe, which refuses elevated callers (E_FAIL),
    // so elevated sessions use the in-process dialog; it also covers an unexpected broker failure.
    private async Task<string?> PickAsync(PickerRequest request)
    {
        if (IsElevated) return ShowInProcess(request);
        try
        {
            return await ShowBrokeredAsync(request);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            try
            {
                return ShowInProcess(request);
            }
            catch (Exception fallbackError)
            {
                throw new InvalidOperationException($"The Windows file picker could not open ({error.GetType().Name} 0x{error.HResult:X8}). Nothing was changed; try again.",
                    new AggregateException(error, fallbackError));
            }
        }
    }

    private string? ShowInProcess(PickerRequest request) =>
        Win32FileDialog.Show(_windowHandleProvider(), request.Save, request.Folder, request.FilterName, request.Extensions, request.SuggestedName);

    private async Task<string?> ShowBrokeredAsync(PickerRequest request)
    {
        if (request.Folder)
        {
            var folderPicker = new FolderPicker();
            InitializeWithWindow.Initialize(folderPicker, _windowHandleProvider());
            folderPicker.FileTypeFilter.Add("*");
            return (await folderPicker.PickSingleFolderAsync())?.Path;
        }

        if (request.Save)
        {
            var savePicker = new FileSavePicker();
            InitializeWithWindow.Initialize(savePicker, _windowHandleProvider());
            savePicker.FileTypeChoices.Add(request.FilterName, request.Extensions.ToList());
            if (request.SuggestedName != null) savePicker.SuggestedFileName = request.SuggestedName;
            return (await savePicker.PickSaveFileAsync())?.Path;
        }

        var openPicker = new FileOpenPicker();
        InitializeWithWindow.Initialize(openPicker, _windowHandleProvider());
        foreach (var extension in request.Extensions)
            openPicker.FileTypeFilter.Add(extension);
        return (await openPicker.PickSingleFileAsync())?.Path;
    }
}
