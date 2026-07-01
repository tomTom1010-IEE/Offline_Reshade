using System.Diagnostics;

namespace OfflineReShade.WinUI.Services;

public sealed class PrototypeProcessService : IDisposable
{
    private readonly AppPaths _paths;
    private Process? _previewProcess;

    public PrototypeProcessService(AppPaths paths)
    {
        _paths = paths;
    }

    public event Action<string>? OutputReceived;
    public event Action? PreviewExited;

    public bool IsPreviewRunning => _previewProcess is { HasExited: false };

    public void StartPreview(string arguments)
    {
        StopPreview();

        if (!File.Exists(_paths.PrototypePath))
            throw new FileNotFoundException("OfflineReShadePrototype.exe was not found.", _paths.PrototypePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = _paths.PrototypePath,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(_paths.PrototypePath),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        _previewProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _previewProcess.OutputDataReceived += OnOutputDataReceived;
        _previewProcess.ErrorDataReceived += OnOutputDataReceived;
        _previewProcess.Exited += OnPreviewProcessExited;
        _previewProcess.Start();
        _previewProcess.BeginOutputReadLine();
        _previewProcess.BeginErrorReadLine();
    }

    public async Task<string> RunExportAsync(string arguments)
    {
        if (!File.Exists(_paths.PrototypePath))
            throw new FileNotFoundException("OfflineReShadePrototype.exe was not found.", _paths.PrototypePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = _paths.PrototypePath,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(_paths.PrototypePath),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start OfflineReShadePrototype.exe.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var log = new List<string>();
        var output = await outputTask;
        var error = await errorTask;
        if (!string.IsNullOrWhiteSpace(output)) log.Add(output.TrimEnd());
        if (!string.IsNullOrWhiteSpace(error)) log.Add(error.TrimEnd());
        log.Add("ExitCode=" + process.ExitCode);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, log));
        return string.Join(Environment.NewLine, log);
    }

    public void StopPreview()
    {
        var process = _previewProcess;
        _previewProcess = null;
        if (process == null)
            return;

        try
        {
            process.OutputDataReceived -= OnOutputDataReceived;
            process.ErrorDataReceived -= OnOutputDataReceived;
            process.Exited -= OnPreviewProcessExited;
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(250);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs args)
    {
        if (!string.IsNullOrWhiteSpace(args.Data))
            OutputReceived?.Invoke(args.Data);
    }

    private void OnPreviewProcessExited(object? sender, EventArgs args)
    {
        PreviewExited?.Invoke();
    }

    public void Dispose()
    {
        StopPreview();
    }
}
