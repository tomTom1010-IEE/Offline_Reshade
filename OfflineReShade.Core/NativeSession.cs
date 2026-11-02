using System.Diagnostics;
using System.Text.Json;

namespace OfflineReShade.Core;

public sealed class NativeSession : IDisposable
{
    private readonly string _nativeExe;
    private readonly string _workDir;
    private Process? _process;
    private readonly DeviceRecoveryBudget _recovery = new();
    public RuntimeConnection Connection { get; } = new();
    public RenderInput Current { get; private set; } = new("");
    public NativeSession(string nativeExe)
    {
        _nativeExe = Path.GetFullPath(nativeExe);
        _workDir = Path.Combine(AutomationBroker.SessionDirectory, "work", Guid.NewGuid().ToString("N"));
    }
    public async Task OpenAsync(RenderInput input, CancellationToken token)
    {
        input = AutomationEngine.ValidateInput(input);
        var previous = Connection.IsConnected ? await Connection.CallAsync("get_runtime_info", cancellationToken: token) : default;
        var recovery = previous.ValueKind == JsonValueKind.Object && previous.TryGetProperty("recoveryRequired", out var required) && required.GetBoolean();
        if (recovery && !_recovery.TryAcquire(DateTimeOffset.UtcNow)) throw new DeviceLostException("D3D recovery failed repeatedly. Wait 30 seconds and restart the session.");
        if (Connection.IsConnected && !recovery && input.EffectDir == Current.EffectDir && input.PresetPath == Current.PresetPath && input.Width == Current.Width && input.Height == Current.Height)
        {
            var result = await Connection.CallAsync("set_input_paths", input, token);
            if (!result.TryGetProperty("restartRequired", out var restart) || !restart.GetBoolean()) { await Connection.CallAsync("set_input_watch_enabled", new { enabled = input.WatchInput }, token); Current = input; return; }
        }
        bool? effectsEnabled = previous.ValueKind == JsonValueKind.Object ? previous.GetProperty("effectsEnabled").GetBoolean() : null;
        Stop();
        if (!File.Exists(_nativeExe)) throw new FileNotFoundException("Native host not found. Use --native with OfflineReShadePrototype.exe.", _nativeExe);
        if (string.IsNullOrWhiteSpace(input.EffectDir) || !Directory.Exists(input.EffectDir)) throw new ArgumentException("EffectDir must be an existing shader directory (it may be empty).");
        Directory.CreateDirectory(_workDir);
        var pipeName = "OfflineReShade.Mcp." + Guid.NewGuid().ToString("N");
        var info = new ProcessStartInfo(_nativeExe) { WorkingDirectory = Path.GetDirectoryName(_nativeExe), UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        void Add(string key, string? value) { if (!string.IsNullOrWhiteSpace(value)) { info.ArgumentList.Add(key); info.ArgumentList.Add(value); } }
        Add("--color", input.ColorPath); Add("--depth", input.DepthPath); Add("--depth-format", input.DepthFormat); Add("--depth-profile", input.DepthProfile);
        Add("--depth-downsample", input.DepthDownsample); Add("--effect-dir", Path.GetFullPath(input.EffectDir)); Add("--preset", input.PresetPath); Add("--output", input.OutputPath);
        Add("--work-dir", _workDir); Add("--control-pipe", pipeName);
        if (input.Width > 0) { Add("--width", input.Width.ToString()); Add("--height", input.Height.ToString()); }
        info.ArgumentList.Add("--interactive"); info.ArgumentList.Add("--preview-shared"); if (!input.WatchInput) info.ArgumentList.Add("--disable-input-watch");
        _process = Process.Start(info) ?? throw new IOException("Failed to start native host.");
        _process.OutputDataReceived += (_, e) => { if (e.Data != null) Console.Error.WriteLine(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.Error.WriteLine(e.Data); };
        _process.BeginOutputReadLine(); _process.BeginErrorReadLine();
        try {
            await Connection.ConnectAsync(pipeName, token); Current = input;
            await Compatibility.VerifyAsync(Connection, "native", token);
            if (effectsEnabled.HasValue) await Connection.CallAsync("set_effects_state", new { enabled = effectsEnabled.Value }, token);
        }
        catch { Stop(); throw; }
    }
    public Task<JsonElement> CallAsync(string method, object? args, CancellationToken token) => Connection.CallAsync(method, args, token);
    private void Stop() { Connection.Close(); if (_process == null) return; try { if (!_process.HasExited) { _process.Kill(true); _process.WaitForExit(5000); } } finally { _process.Dispose(); _process = null; } }
    public void Dispose() { Stop(); Connection.Dispose(); }
}
