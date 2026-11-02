using System.Text.Json;

namespace OfflineReShade.Core;

public static class Compatibility
{
    public static object Descriptor(string endpoint) => new {
        endpoint, productVersion = BuildContract.ProductVersion,
        protocolVersion = endpoint == "native" ? BuildContract.NativeRpc : BuildContract.AutomationBroker,
        nativeRpcVersion = BuildContract.NativeRpc,
        offlineApiVersion = BuildContract.OfflineApi, previewBridgeVersion = BuildContract.PreviewBridge,
        reShadeApiVersion = BuildContract.ReShadeApi, upstreamVersion = BuildContract.UpstreamVersion,
        upstreamCommit = BuildContract.UpstreamCommit
    };

    public static void Validate(JsonElement descriptor, string endpoint)
    {
        var expected = AutomationEngine.Json(Descriptor(endpoint));
        foreach (var name in new[] { "endpoint", "protocolVersion", "nativeRpcVersion", "offlineApiVersion", "previewBridgeVersion", "reShadeApiVersion", "upstreamCommit" })
            if (!descriptor.TryGetProperty(name, out var actual) || actual.GetRawText() != expected.GetProperty(name).GetRawText())
                throw new InvalidOperationException($"Incompatible {endpoint} component: {name} must be {expected.GetProperty(name)}; received {(actual.ValueKind == JsonValueKind.Undefined ? "missing" : actual.ToString())}. Install all components from the same Offline ReShade package.");
    }

    public static async Task VerifyAsync(RuntimeConnection connection, string endpoint, CancellationToken token = default)
    {
        try { Validate(await connection.CallAsync("get_compatibility", cancellationToken: token), endpoint); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            connection.Close();
            throw new InvalidOperationException($"Cannot use incompatible {endpoint} component. Install all components from the same Offline ReShade package. {ex.Message}", ex);
        }
    }
}
