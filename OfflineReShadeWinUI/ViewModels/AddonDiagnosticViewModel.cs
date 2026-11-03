namespace OfflineReShade.WinUI.ViewModels;

public sealed class AddonDiagnosticViewModel
{
    public AddonDiagnosticViewModel(
        string file,
        string path,
        string status,
        string stage,
        string message,
        uint errorCode,
        bool dependencyFailure)
    {
        File = file;
        Path = path;
        Status = status;
        Stage = stage;
        Message = message;
        ErrorCode = errorCode;
        DependencyFailure = dependencyFailure;
    }

    public string File { get; }
    public string Path { get; }
    public string Status { get; }
    public string Stage { get; }
    public string Message { get; }
    public uint ErrorCode { get; }
    public bool DependencyFailure { get; }

    public bool IsLoaded => string.Equals(Status, "loaded", StringComparison.OrdinalIgnoreCase);
    public bool IsDisabled => string.Equals(Status, "disabled", StringComparison.OrdinalIgnoreCase);
    public bool IsFailure => string.Equals(Status, "failed", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(Status, "skipped", StringComparison.OrdinalIgnoreCase);
    public string DisplayName => string.IsNullOrWhiteSpace(File) ? "Add-on folder" : File;
    public string StatusText => string.IsNullOrWhiteSpace(Stage) ? Status : $"{Status} - {Stage}";
    public string DetailText
    {
        get
        {
            var detail = Message;
            if (ErrorCode != 0)
                detail = $"Win32 {ErrorCode}: {detail}";
            if (DependencyFailure)
                detail += " The add-on DLL or one of its dependent DLLs is missing, incompatible, or the wrong architecture.";
            return detail;
        }
    }
}
