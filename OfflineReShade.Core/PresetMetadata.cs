using System.Text;

namespace OfflineReShade.Core;

// ReShade's normal preset format does not persist its global effects toggle.
public static class PresetMetadata
{
    private const string Section = "[OfflineReShade]";
    public static bool ReadEffectsEnabled(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        var inside = false;
        foreach (var line in File.ReadLines(path))
        {
            var text = line.Trim();
            if (text.StartsWith('[')) { inside = text.Equals(Section, StringComparison.OrdinalIgnoreCase); continue; }
            if (!inside) continue;
            var pair = text.Split('=', 2);
            if (pair.Length == 2 && pair[0].Trim().Equals("EffectsEnabled", StringComparison.OrdinalIgnoreCase))
                return pair[1].Trim() switch { "0" => false, "1" => true, _ => throw new InvalidDataException("Invalid OfflineReShade EffectsEnabled preset metadata.") };
        }
        return true;
    }
    public static async Task WriteEffectsEnabledAsync(string path, bool enabled, CancellationToken token)
    {
        var lines = (await File.ReadAllLinesAsync(path, token)).ToList();
        var inside = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var text = lines[i].Trim();
            if (text.StartsWith('[')) { inside = text.Equals(Section, StringComparison.OrdinalIgnoreCase); continue; }
            if (inside && text.Split('=', 2)[0].Trim().Equals("EffectsEnabled", StringComparison.OrdinalIgnoreCase)) lines.RemoveAt(i--);
        }
        var index = lines.FindIndex(x => x.Trim().Equals(Section, StringComparison.OrdinalIgnoreCase));
        if (index < 0) { lines.Add(Section); index = lines.Count - 1; }
        lines.Insert(index + 1, "EffectsEnabled=" + (enabled ? "1" : "0"));
        await File.WriteAllLinesAsync(path, lines, new UTF8Encoding(false), token);
    }
}
