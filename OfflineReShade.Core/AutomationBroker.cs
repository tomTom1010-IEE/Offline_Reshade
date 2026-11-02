using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace OfflineReShade.Core;

public sealed record SessionDescriptor(string Id, string PipeName, int Pid, long StartTicks, string Name);
public sealed class AutomationBroker : IDisposable
{
    public static string SessionDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OfflineReShade", "Sessions");
    private readonly CancellationTokenSource _stop = new();
    private readonly string _descriptorPath;
    public SessionDescriptor Descriptor { get; }
    public AutomationBroker(Func<string, JsonElement, CancellationToken, Task<JsonElement>> dispatch, string name)
    {
        var id = Guid.NewGuid().ToString("N");
        Descriptor = new(id, "OfflineReShade.Automation." + id, Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, name);
        Directory.CreateDirectory(SessionDirectory);
        _descriptorPath = Path.Combine(SessionDirectory, id + ".json");
        File.WriteAllText(_descriptorPath, JsonSerializer.Serialize(Descriptor, AutomationEngine.JsonOptions));
        Trace.WriteLine($"OfflineReShade session {id} pid={Environment.ProcessId} descriptor={_descriptorPath}");
        _ = ListenAsync(dispatch);
    }
    public static SessionDescriptor[] Discover()
    {
        if (!Directory.Exists(SessionDirectory)) return [];
        var list = new List<SessionDescriptor>();
        foreach (var file in Directory.EnumerateFiles(SessionDirectory, "*.json"))
        {
            try
            {
                var d = JsonSerializer.Deserialize<SessionDescriptor>(File.ReadAllText(file), AutomationEngine.JsonOptions)!;
                using var p = Process.GetProcessById(d.Pid);
                if (!p.HasExited && p.StartTime.ToUniversalTime().Ticks == d.StartTicks) list.Add(d);
            }
            catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return list.ToArray();
    }
    private async Task ListenAsync(Func<string, JsonElement, CancellationToken, Task<JsonElement>> dispatch)
    {
        while (!_stop.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(Descriptor.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.WaitForConnectionAsync(_stop.Token); _ = ServeAsync(pipe, dispatch); }
            catch (OperationCanceledException) { pipe.Dispose(); break; }
        }
    }
    private async Task ServeAsync(NamedPipeServerStream pipe, Func<string, JsonElement, CancellationToken, Task<JsonElement>> dispatch)
    {
        using (pipe)
        using (var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true))
        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" })
        {
            try
            {
                while (!_stop.IsCancellationRequested && await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    var id = 0;
                    object response;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        id = doc.RootElement.GetProperty("id").GetInt32();
                        var method = doc.RootElement.GetProperty("method").GetString()!;
                        var result = method == "get_compatibility" ? AutomationEngine.Json(Compatibility.Descriptor("automation"))
                            : await dispatch(method, doc.RootElement.GetProperty("params").Clone(), _stop.Token);
                        response = new { id, ok = true, result };
                    }
                    catch (Exception ex) { response = new { id, ok = false, error = new { message = ex.Message,
                        operation = ex.Data[nameof(OperationOutcome)] is OperationOutcome outcome ? AutomationEngine.Json(outcome) : (JsonElement?)null } }; }
                    await writer.WriteLineAsync(JsonSerializer.Serialize(response).AsMemory(), _stop.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }
    public void Dispose() { _stop.Cancel(); try { File.Delete(_descriptorPath); } catch (IOException) { } }
}
