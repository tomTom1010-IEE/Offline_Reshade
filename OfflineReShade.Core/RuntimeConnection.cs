using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace OfflineReShade.Core;

public class RuntimeConnection : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _id;
    public bool IsConnected => _pipe?.IsConnected == true;

    public async Task ConnectAsync(string name, CancellationToken token = default)
    {
        Close();
        _pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await _pipe.ConnectAsync(15000, token);
        _reader = new StreamReader(_pipe, new UTF8Encoding(false), leaveOpen: true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
    }

    public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        var sent = false;
        try
        {
            if (_reader == null || _writer == null) throw new IOException("Runtime is disconnected.");
            // Drain an in-flight response before honoring caller cancellation.
            using var deadline = new CancellationTokenSource();
            deadline.CancelAfter(TimeSpan.FromSeconds(method.StartsWith("save_") ? 120 : 30));
            var id = ++_id;
            sent = true;
            await _writer.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = parameters ?? new { } }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }).AsMemory(), deadline.Token);
            var line = await _reader.ReadLineAsync(deadline.Token) ?? throw new IOException("Runtime closed the pipe.");
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id) throw new IOException("Mismatched runtime response ID.");
            sent = false;
            cancellationToken.ThrowIfCancellationRequested();
            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                throw new InvalidOperationException(root.GetProperty("error").GetProperty("message").GetString());
            return root.GetProperty("result").Clone();
        }
        catch (Exception ex) when (sent && ex is OperationCanceledException or IOException or JsonException)
        {
            // A cancelled read leaves an unread response. Discard the connection rather than
            // allowing the next caller to consume a response belonging to a different request.
            Close();
            throw;
        }
        finally { _gate.Release(); }
    }

    public void Close()
    {
        var pipe = _pipe; var reader = _reader; var writer = _writer;
        _pipe = null; _reader = null; _writer = null;
        pipe?.Dispose();
        try { writer?.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        reader?.Dispose();
    }
    public void Dispose() => Close();
}

public static class RuntimeOperations
{
    public static async Task WaitForFramesAsync(Func<string, object?, CancellationToken, Task<JsonElement>> call, int frames, CancellationToken token, int timeoutSeconds = 600)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frames);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        ulong? baseline = null, generation = null;
        try
        {
            while (true)
            {
                var state = await call("get_runtime_info", null, deadline.Token);
                if (state.TryGetProperty("recoveryRequired", out var recovery) && recovery.GetBoolean())
                    throw new DeviceLostException(state.TryGetProperty("deviceError", out var deviceError) ? deviceError.GetString() : null);
                if (state.TryGetProperty("restartRequired", out var restart) && restart.GetBoolean()) throw new InputResizeException();
                if (state.TryGetProperty("inputError", out var inputError) && !string.IsNullOrEmpty(inputError.GetString())) throw new InvalidOperationException(inputError.GetString());
                var currentGeneration = state.GetProperty("inputGeneration").GetUInt64();
                if (generation.HasValue && currentGeneration != generation) throw new InvalidOperationException("Input changed while waiting for the render.");
                generation = currentGeneration;
                var frame = state.GetProperty("frameCount").GetUInt64();
                if (state.GetProperty("loading").GetBoolean()) baseline = null;
                else if (frame > 0)
                {
                    baseline ??= frame;
                    if (frame - baseline.Value >= (ulong)frames) return;
                }
                await Task.Delay(50, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("Timed out waiting for FX compilation and rendered frames."); }
    }

    public static string OutputPath(string path, string color, string depth)
    {
        var full = Path.GetFullPath(path);
        if (!full.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output must be a PNG path.");
        if (string.Equals(full, Path.GetFullPath(color), StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(depth) && string.Equals(full, Path.GetFullPath(depth), StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Output must not overwrite an input file.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return full;
    }
}

public sealed class InputResizeException : Exception
{
    public InputResizeException() : base("Input dimensions changed; the runtime must restart.") { }
}

public sealed class DeviceLostException(string? message) : Exception(message ?? "D3D device was lost; runtime recovery required.");
