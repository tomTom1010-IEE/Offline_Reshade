namespace OfflineReShade.Core;

public sealed record DepthCandidate(string Path, string Format);
public sealed record InputPairing(string ColorPath, string Name, DepthCandidate[] Candidates, string OutputName)
{
    public bool IsAmbiguous => Candidates.Length > 1;
    public DepthCandidate? AutomaticDepth => Candidates.Length == 1 ? Candidates[0] : null;

    public static bool IsColorFile(string path)
    {
        if (!Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)) return false;
        var name = Path.GetFileNameWithoutExtension(path);
        return !new[] { "-Depth", ".depth", "-Reshade", ".reshade", "depthoutput", "reshadeoutput" }.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    public static InputPairing Resolve(string color, string profile)
    {
        color = Path.GetFullPath(color);
        var folder = Path.GetDirectoryName(color)!;
        var stem = Path.GetFileNameWithoutExtension(color);
        var namedPair = stem.EndsWith("-Color", StringComparison.OrdinalIgnoreCase);
        var gameOutput = stem.Equals("coloroutput", StringComparison.OrdinalIgnoreCase);
        var name = namedPair ? stem[..^6] : stem;
        var depthStem = namedPair ? name + "-Depth" : gameOutput ? "depthoutput" : stem + ".depth";
        var candidates = Directory.Exists(folder) ? Directory.EnumerateFiles(folder)
            .Where(x => Path.GetFileName(x).Equals(depthStem + ".rfloat", StringComparison.OrdinalIgnoreCase) ||
                (profile != "kk" && Path.GetFileName(x).Equals(depthStem + ".png", StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.OrdinalIgnoreCase).Select(x => new DepthCandidate(x, Path.GetExtension(x).Equals(".rfloat", StringComparison.OrdinalIgnoreCase) ? "raw" : "rgba")).ToArray() : [];
        return new(color, name, candidates, namedPair ? name + "-Reshade.png" : gameOutput ? "reshadeoutput.png" : stem + ".reshade.png");
    }
}
