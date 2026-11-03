using System.Diagnostics;
using System.Globalization;
using OfflineReShade.Core;
using OfflineReShade.WinUI.Mvvm;

namespace OfflineReShade.WinUI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool _exporting;
    private int _exportConflictIndex;
    private bool _directPng;
    private string _exportWarmup = "5";
    private string _exportedPath = "";
    public event Action? ExportRequested;
    public RelayCommand OpenExportCommand { get; }
    public AsyncRelayCommand PickExportFolderCommand { get; }
    public AsyncRelayCommand OpenExportedImageCommand { get; }
    public AsyncRelayCommand LocateExportedImageCommand { get; }
    public bool CanExport => !_exporting && CanEditSession && (_session.IsConnected || !string.IsNullOrWhiteSpace(Settings.ColorPath));
    public bool Exporting => _exporting;
    public string ExportPath { get => Settings.OutputPath; set { Settings.OutputPath = value; OnPropertyChanged(); } }
    public int ExportConflictIndex { get => _exportConflictIndex; set => SetProperty(ref _exportConflictIndex, value); }
    public bool ExportDirectPng { get => _directPng; set => SetProperty(ref _directPng, value); }
    public string ExportWarmup { get => _exportWarmup; set => SetProperty(ref _exportWarmup, value); }
    public string ExportActionText => HasPendingConfiguration ? "Apply changes and export" : "Export image";
    public string ExportSourceText => HasPendingConfiguration ? Settings.ColorPath : CurrentInputText;
    public string ExportConfigurationText => HasPendingConfiguration
        ? "Pending configuration will be applied before export. If applying fails, the previous image and output are preserved."
        : "Export uses the applied image and current effect adjustments.";
    public string ExportDimensionsText
    {
        get
        {
            try
            {
                var input = HasPendingConfiguration ? CurrentRenderInput() : _runtimeInput;
                if (input == null) return "Open an image to determine the output size.";
                if (input.Width > 0) return $"{input.Width} × {input.Height} pixels";
                if (_runtimeInput?.ColorPath == input.ColorPath && RenderWidth > 0 && !HasPendingConfiguration)
                    return $"{RenderWidth} × {RenderHeight} pixels (source size)";
                // Read only the PNG header for display; native decoding/validation still gates export.
                using var file = File.OpenRead(input.ColorPath);
                Span<byte> header = stackalloc byte[24]; file.ReadExactly(header);
                if (!header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) || !header[12..16].SequenceEqual("IHDR"u8))
                    return "Invalid PNG input — apply a valid image before exporting.";
                var width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
                var height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
                return $"{width} × {height} pixels (source size; validated on apply)";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return "Output size unavailable: " + error.Message; }
        }
    }
    public string ExportedPath => _exportedPath;
    public bool HasExportedImage => _exportedPath.Length > 0;

    public async Task ExportImageAsync()
    {
        if (!CanExport) throw new InvalidOperationException("Finish the current operation or open an image before exporting.");
        if (!int.TryParse(ExportWarmup, NumberStyles.None, CultureInfo.InvariantCulture, out var frames) || frames is < 0 or > 360000)
            throw new ArgumentException("Warmup must be a whole number of frames between 0 and 360000.");
        var conflict = ExportConflictIndex switch { 0 => "rename", 1 => "replace", 2 => "fail", _ => throw new ArgumentException("Choose a file conflict policy.") };
        var expected = _session.IsConnected ? _session.Current : null;
        var pending = HasPendingConfiguration ? CurrentRenderInput() : null;
        var request = new ImageExportRequest(Path.GetFullPath(ExportPath, _paths.AppDirectory), frames, !ExportDirectPng, conflict, pending, expected);
        _exporting = true; NotifyExportState();
        try
        {
            var result = await ExecuteOperationAsync("export_image", request);
            _exportedPath = result.GetProperty("path").GetString()!;
            OnPropertyChanged(nameof(ExportedPath)); OnPropertyChanged(nameof(HasExportedImage));
            OpenExportedImageCommand.RaiseCanExecuteChanged(); LocateExportedImageCommand.RaiseCanExecuteChanged();
        }
        finally { _exporting = false; NotifyExportState(); }
    }
    private async Task PickExportFolderAsync()
    {
        if (_picker == null || await _picker.PickFolderAsync() is not { } folder) return;
        var name = Path.GetFileName(ExportPath);
        ExportPath = Path.Combine(folder, string.IsNullOrWhiteSpace(name) ? "reshadeoutput.png" : name);
    }
    private Task OpenExportedAsync(bool locate)
    {
        if (!File.Exists(_exportedPath) || !Path.GetExtension(_exportedPath).Equals(".png", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("The exported image is no longer available.", _exportedPath);
        var start = new ProcessStartInfo(locate ? "explorer.exe" : _exportedPath) { UseShellExecute = true };
        if (locate) start.ArgumentList.Add("/select," + _exportedPath);
        Process.Start(start);
        return Task.CompletedTask;
    }
    private void NotifyExportState()
    {
        // Configuration restoration runs before commands are created in the constructor.
        foreach (var name in new[] { nameof(CanExport), nameof(Exporting), nameof(ExportPath), nameof(ExportActionText), nameof(ExportSourceText), nameof(ExportConfigurationText), nameof(ExportDimensionsText) })
            OnPropertyChanged(name);
        OpenExportCommand?.RaiseCanExecuteChanged(); PickExportFolderCommand?.RaiseCanExecuteChanged();
    }
}
