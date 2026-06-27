using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace OfflineReShade.WinUI.Services;

public sealed class ReShadeRpcClient : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _id;

    public bool IsConnected => _pipe?.IsConnected == true;

    public async Task ConnectAsync(string pipeName, CancellationToken cancellationToken)
    {
        Close();
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await _pipe.ConnectAsync(5000, cancellationToken);
        _reader = new StreamReader(_pipe, new UTF8Encoding(false));
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    }

    public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        if (_reader == null || _writer == null)
            throw new InvalidOperationException("Control pipe is not connected.");

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var request = JsonSerializer.Serialize(new
            {
                id = ++_id,
                method,
                @params = parameters ?? new { }
            });
            await _writer.WriteLineAsync(request.AsMemory(), cancellationToken);

            var line = await _reader.ReadLineAsync(cancellationToken);
            if (line == null)
                throw new IOException("Control pipe closed.");

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            {
                var message = root.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var errorMessage)
                    ? errorMessage.GetString()
                    : "ReShade control command failed.";
                throw new InvalidOperationException(message);
            }

            return root.TryGetProperty("result", out var result) ? result.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Close()
    {
        _writer?.Dispose();
        _reader?.Dispose();
        _pipe?.Dispose();
        _writer = null;
        _reader = null;
        _pipe = null;
    }

    public void Dispose()
    {
        Close();
        _lock.Dispose();
    }
}
