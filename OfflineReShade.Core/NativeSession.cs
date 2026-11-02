using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace OfflineReShade.Core;

public sealed record NativeSessionOptions(string? WorkDirectory = null, bool PreviewPipe = false, bool AddonOverlay = false);

public sealed class NativeSession : RenderSessionController
{
    private readonly NativeRuntimeHost _native;
    public event Action<string>? OutputReceived;
    public event Action? UnexpectedExit;
    public event Action<string>? PreviewPipeCreated;
    public NativeSession(string nativeExe, NativeSessionOptions? options = null, EditingSession? state = null)
        : this(new NativeRuntimeHost(nativeExe, options ?? new()), state) { }
    private NativeSession(NativeRuntimeHost native, EditingSession? state)
        : base(native, Path.Combine(native.WorkDirectory, "snapshots"), state)
    {
        _native = native;
        native.OutputReceived += text => { if (OutputReceived != null) OutputReceived(text); else Console.Error.WriteLine(text); };
        native.UnexpectedExit += () => UnexpectedExit?.Invoke();
        native.PreviewPipeCreated += pipe => PreviewPipeCreated?.Invoke(pipe);
    }
    public int? ProcessId => _native.ProcessId;
}

internal sealed class NativeRuntimeHost : IRenderSessionHost
{
    private readonly string _nativeExe;
    private readonly NativeSessionOptions _options;
    private readonly RuntimeConnection _connection = new();
    private Process? _process;
    public string WorkDirectory { get; }
    public string? PersistedPresetPath => Path.Combine(WorkDirectory, "OfflinePreset.ini");
    public bool IsConnected => _connection.IsConnected && _process is { HasExited: false };
    public int? ProcessId => _process is { HasExited: false } process ? process.Id : null;
    public event Action<string>? OutputReceived;
    public event Action? UnexpectedExit;
    public event Action<string>? PreviewPipeCreated;
    public NativeRuntimeHost(string nativeExe, NativeSessionOptions options)
    {
        _nativeExe = Path.GetFullPath(nativeExe); _options = options;
        WorkDirectory = options.WorkDirectory ?? Path.Combine(AutomationBroker.SessionDirectory, "work", Guid.NewGuid().ToString("N"));
    }
    public async Task StartAsync(RenderInput input, string? snapshot, CancellationToken token)
    {
        if (!File.Exists(_nativeExe)) throw new FileNotFoundException("Native host not found. Use OfflineReShadePrototype.exe.", _nativeExe);
        Directory.CreateDirectory(WorkDirectory);
        // ReShade auto-saves several parameter operations. Its active preset must be a
        // private working copy; saving the user's source is an explicit editor operation.
        var workingPreset = Path.Combine(WorkDirectory, "OfflinePreset.ini");
        var sourcePreset = snapshot ?? input.PresetPath;
        if (!string.IsNullOrEmpty(sourcePreset) && !string.Equals(Path.GetFullPath(sourcePreset), Path.GetFullPath(workingPreset), StringComparison.OrdinalIgnoreCase))
            File.Copy(sourcePreset, workingPreset, true);
        var pipe = "OfflineReShade.Runtime." + Guid.NewGuid().ToString("N");
        var info = new ProcessStartInfo(_nativeExe) { WorkingDirectory = Path.GetDirectoryName(_nativeExe), UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        void Add(string key, string? value) { if (!string.IsNullOrWhiteSpace(value)) { info.ArgumentList.Add(key); info.ArgumentList.Add(value); } }
        Add("--color", input.ColorPath); Add("--depth", input.DepthPath); Add("--depth-format", input.DepthFormat); Add("--depth-profile", input.DepthProfile);
        Add("--depth-downsample", input.DepthDownsample); Add("--effect-dir", Path.GetFullPath(input.EffectDir)); Add("--preset", File.Exists(workingPreset) ? workingPreset : null); Add("--output", input.OutputPath);
        Add("--work-dir", WorkDirectory); Add("--control-pipe", pipe);
        if (input.Width > 0) { Add("--width", input.Width.ToString(CultureInfo.InvariantCulture)); Add("--height", input.Height.ToString(CultureInfo.InvariantCulture)); }
        info.ArgumentList.Add("--interactive"); info.ArgumentList.Add("--preview-shared");
        if (!input.WatchInput) info.ArgumentList.Add("--disable-input-watch");
        if (_options.AddonOverlay) info.ArgumentList.Add("--addon-overlay-shared");
        if (_options.PreviewPipe) { Add("--preview-pipe", pipe + ".preview"); PreviewPipeCreated?.Invoke(pipe + ".preview"); }
        token.ThrowIfCancellationRequested();
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
        var ready = false;
        _process = process;
        process.OutputDataReceived += (_, e) => { if (e.Data != null) OutputReceived?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) OutputReceived?.Invoke(e.Data); };
        process.Exited += (_, _) =>
        {
            if (!ReferenceEquals(Volatile.Read(ref _process), process)) return;
            if (Volatile.Read(ref ready)) UnexpectedExit?.Invoke();
            else { try { startup.Cancel(); } catch (ObjectDisposedException) { } }
        };
        try
        {
            process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
            await _connection.ConnectAsync(pipe, startup.Token);
            await Compatibility.VerifyAsync(_connection, "native", startup.Token);
            startup.Token.ThrowIfCancellationRequested();
            Volatile.Write(ref ready, true);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && process.HasExited)
        { await StopAsync(); throw new IOException("Native host exited before accepting the input. Check the image, depth and runtime log."); }
        catch { await StopAsync(); throw; }
    }
    public Task<JsonElement> CallAsync(string method, object? args, CancellationToken cancellationToken) => _connection.CallAsync(method, args, cancellationToken);
    public async Task StopAsync()
    {
        _connection.Close();
        var process = _process; _process = null;
        if (process == null) return;
        try
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }
    public async ValueTask DisposeAsync() { await StopAsync(); _connection.Dispose(); }
}
