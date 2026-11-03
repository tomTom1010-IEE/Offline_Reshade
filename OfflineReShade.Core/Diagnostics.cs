using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace OfflineReShade.Core;

public static class Diagnostics
{
    public static async Task<JsonElement> CollectAsync(Func<string, object?, CancellationToken, Task<JsonElement>> call, CancellationToken token)
    {
        JsonElement? runtime = null;
        string? runtimeError = null;
        try { runtime = await call("get_runtime_info", null, token); }
        catch (Exception ex) when (ex is not OperationCanceledException) { runtimeError = ex.Message; }
        return AutomationEngine.Json(new {
            schemaVersion = 1, expected = Compatibility.Descriptor("native"),
            environment = new { os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(), dotnet = RuntimeInformation.FrameworkDescription },
            package = await PackageAsync(token), runtime, runtimeError
        });
    }

    private static async Task<object> PackageAsync(CancellationToken token)
    {
        var root = AppContext.BaseDirectory;
        if (!File.Exists(Path.Combine(root, "manifest.json")) && new DirectoryInfo(root).Name.Equals("Mcp", StringComparison.OrdinalIgnoreCase))
            root = Path.GetFullPath(Path.Combine(root, ".."));
        var path = Path.Combine(root, "manifest.json");
        if (!File.Exists(path)) return new { status = "unpackaged", reason = "No release manifest (development build or incomplete package)." };
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
            var manifest = document.RootElement;
            var results = new List<object>();
            var valid = manifest.GetProperty("version").GetString() == BuildContract.ProductVersion;
            // Only fixed paths are inspected. A modified manifest cannot redirect reads outside the package.
            foreach (var relative in new[] { "OfflineReShadeWinUI.dll", "OfflineReShade.Core.dll", "OfflineReShadePrototype.exe", "ReShade64.dll", "OfflineReShadePreviewBridge.dll", "Mcp/OfflineReShade.Mcp.dll", "Mcp/OfflineReShade.Core.dll" })
            {
                var entry = manifest.GetProperty("files").EnumerateArray().FirstOrDefault(f => f.GetProperty("path").GetString() == relative);
                var file = Path.Combine(root, relative);
                var state = "missing";
                if (File.Exists(file) && entry.ValueKind == JsonValueKind.Object)
                {
                    await using var stream = File.OpenRead(file);
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
                    state = stream.Length == entry.GetProperty("size").GetInt64() && hash.Equals(entry.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase) ? "ok" : "modified";
                }
                valid &= state == "ok";
                results.Add(new { path = relative, status = state });
            }
            return new { status = valid ? "ok" : "mismatch", scope = "seven core binaries; checksums are integrity metadata, not signatures",
                version = manifest.GetProperty("version").GetString(), provenance = manifest.TryGetProperty("provenance", out var p) ? p.Clone() : (JsonElement?)null, files = results };
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException)
        { return new { status = "invalid", reason = ex.Message }; }
    }
}
