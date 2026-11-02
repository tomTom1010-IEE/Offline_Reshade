using System.Text.Json;
using System.Text.Json.Nodes;

namespace OfflineReShade.Core;

public sealed record ImageExportRequest(string Path, int WarmupFrames = 5, bool ReShadeShot = true,
    string ConflictPolicy = "replace", RenderInput? ApplyInput = null, RenderInput? ExpectedInput = null);

public static class ImageExport
{
    public static void Validate(ImageExportRequest request)
    {
        if (request.ConflictPolicy is not ("replace" or "rename" or "fail")) throw new ArgumentException("Conflict policy must be replace, rename or fail.");
        if (request.WarmupFrames is < 0 or > 360000) throw new ArgumentException("Invalid warmup frame count.");
    }

    public static async Task<JsonElement> WriteAsync(string destination, string policy,
        Func<string, CancellationToken, Task<JsonElement>> render, CancellationToken token)
    {
        var directory = System.IO.Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        if (policy == "fail" && File.Exists(destination)) throw new IOException("The output file already exists. Choose another path or conflict policy.");
        var temporary = System.IO.Path.Combine(directory, ".OfflineReShade-export-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            var result = await render(temporary, token);
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0) throw new IOException("The renderer did not write an image. The previous output was preserved.");
            token.ThrowIfCancellationRequested();
            var selected = destination;
            for (var suffix = 0; ; suffix++)
            {
                if (suffix > 10000) throw new IOException("No available output filename was found.");
                if (suffix > 0) selected = System.IO.Path.Combine(directory,
                    System.IO.Path.GetFileNameWithoutExtension(destination) + $" ({suffix})" + System.IO.Path.GetExtension(destination));
                token.ThrowIfCancellationRequested();
                try { File.Move(temporary, selected, policy == "replace"); break; }
                catch (IOException) when (policy == "rename" && File.Exists(selected)) { }
            }
            var node = JsonNode.Parse(result.GetRawText())!.AsObject();
            node["path"] = selected;
            node["conflictPolicy"] = policy;
            return AutomationEngine.Json(node);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
