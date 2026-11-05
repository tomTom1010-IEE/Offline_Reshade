using System.Globalization;
using System.Text.Json;

namespace OfflineReShade.Core;

// Version-specific interoperability data only; no vendor shader implementation.
internal sealed class MartyPresetCodec
{
    private readonly string[] _lines;
    private readonly Dictionary<(string Section, string Key), int> _keys = new();
    public MartyPresetCodec(string text)
    {
        if (text.Length > 2 * 1024 * 1024) throw new InvalidDataException("Preset exceeds the 2 MiB adapter limit.");
        _lines = text.Replace("\r\n", "\n").Split('\n');
        var section = "";
        for (var i = 0; i < _lines.Length; i++) {
            var line = _lines[i].Trim().TrimStart('\ufeff');
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            if (line.StartsWith(';') || line.StartsWith('#')) continue;
            var split = line.IndexOf('=');
            if (split >= 0 && !_keys.TryAdd((section, line[..split].Trim()), i)) throw new InvalidDataException("Duplicate preset key.");
        }
    }
    public string Get(string section, string key) => _keys.TryGetValue((section, key), out var i)
        ? _lines[i][(_lines[i].IndexOf('=') + 1)..].Trim() : throw new InvalidDataException("Missing private preset field: " + key);
    public uint[] UInts(string section, string key, int count)
    {
        var parts = Get(section, key).Split(',');
        if (parts.Length != count || parts.Any(p => !uint.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _))) throw new InvalidDataException("Invalid private field: " + key);
        return parts.Select(p => uint.Parse(p, CultureInfo.InvariantCulture)).ToArray();
    }
    public int Int(string section, string key) => int.TryParse(Get(section, key), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
        ? value : throw new InvalidDataException("Invalid private field: " + key);
    public string Patch(string section, IReadOnlyDictionary<string, string> patch)
    {
        var lines = (string[])_lines.Clone();
        foreach (var (key, value) in patch) {
            _ = Get(section, key); // Never silently invent fields for an incompatible shader.
            lines[_keys[(section, key)]] = key + "=" + value;
        }
        return string.Join('\n', lines);
    }
    public void Verify(string section, IReadOnlyDictionary<string, string> patch)
    {
        foreach (var (key, value) in patch)
            if (Get(section, key) != value) throw new InvalidOperationException("Marty private-state readback mismatch: " + key);
    }
    private static double Number(JsonElement value, double min, double max)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var n) || !double.IsFinite(n) || n < min || n > max)
            throw new ArgumentException($"Expected a finite number in [{min}, {max}].");
        return n;
    }
    public static double[] Vector(JsonElement args, string key, int count, double min, double max)
    {
        var value = args.GetProperty(key);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != count) throw new ArgumentException("Invalid vector: " + key);
        return value.EnumerateArray().Select(v => Number(v, min, max)).ToArray();
    }
    public static Dictionary<string, string> CurvePatch(JsonElement args)
    {
        var channel = args.GetProperty("channel").GetString();
        if (channel is not ("RGB" or "R" or "G" or "B")) throw new ArgumentException("channel must be RGB, R, G or B; monochrome curves are not supported.");
        var points = args.GetProperty("points");
        if (points.ValueKind != JsonValueKind.Array || points.GetArrayLength() is < 2 or > 10) throw new ArgumentException("Use 2–10 curve points.");
        var packed = new List<uint>(); var last = -1;
        foreach (var point in points.EnumerateArray()) {
            if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() != 2) throw new ArgumentException("Each point is [input, output].");
            var x = (int)Math.Round(Number(point[0], 0, 1) * 255);
            var y = (int)Math.Round(Number(point[1], 0, 1) * 255);
            if (x <= last) throw new ArgumentException("Curve inputs must remain strictly increasing after 8-bit quantization.");
            packed.Add((uint)((x << 8) | y)); last = x;
        }
        if (packed[0] >> 8 != 0 || packed[^1] >> 8 != 255) throw new ArgumentException("Curve endpoints must have input 0 and 1.");
        var result = new Dictionary<string, string> { ["EXT_CURVES_NODE_COUNT_" + channel] = packed.Count.ToString(CultureInfo.InvariantCulture) };
        for (var i = 0; i < 10; i++) result["EXT_CURVES_NODE_COORDS_" + channel + i] = (i < packed.Count ? packed[i] : 0).ToString(CultureInfo.InvariantCulture);
        return result;
    }
    public Dictionary<string, string> LightPatch(string section, JsonElement args)
    {
        var slot = args.GetProperty("slot").GetInt32();
        if (slot is < 0 or >= 64) throw new ArgumentException("Light slot must be 0–63.");
        var p = Vector(args, "position", 3, 0, 1);
        var tint = Vector(args, "temperatureTint", 2, -1, 1);
        var radius = Number(args.GetProperty("radius"), 0, 10);
        var intensity = Number(args.GetProperty("intensity"), 0, 20);
        uint Half(double v) => BitConverter.HalfToUInt16Bits((Half)v);
        var previous = UInts(section, "RELIGHT_LIGHT_DATA_" + slot, 4);
        var words = new[] {
            (uint)Math.Round(p[0] * 4095) << 20 | (uint)Math.Round(p[1] * 4095) << 8,
            Half(p[2]) << 17 | Half(radius) << 2, // Sphere; no guessed spot/directional model.
            (uint)Math.Round((tint[0] + 1) * 127) << 24 | (uint)Math.Round((tint[1] + 1) * 127) << 16 | Half(intensity),
            previous[3] // The vendor's auxiliary word is opaque; preserve it.
        };
        var count = UInts(section, "NUM_ACTIVE_LIGHTS", 1)[0];
        if (count > 64) throw new InvalidDataException("Invalid light count.");
        var patch = new Dictionary<string, string> {
            ["NUM_ACTIVE_LIGHTS"] = Math.Max(count, (uint)slot + 1).ToString(CultureInfo.InvariantCulture),
            ["RELIGHT_LIGHT_DATA_" + slot] = string.Join(',', words)
        };
        // An unused slot may contain a previously deleted light. Do not revive gaps.
        for (var i = count; i < slot; i++) patch["RELIGHT_LIGHT_DATA_" + i] = "0,0,0,0";
        return patch;
    }
    public object ReadCurves(string section) => new[] { "RGB", "R", "G", "B" }.Select(channel => {
        var count = UInts(section, "EXT_CURVES_NODE_COUNT_" + channel, 1)[0];
        if (count > 10) throw new InvalidDataException("Invalid curve node count.");
        return new { channel, points = Enumerable.Range(0, (int)count).Select(i => {
            var v = UInts(section, "EXT_CURVES_NODE_COORDS_" + channel + i, 1)[0];
            if (v > 65535) throw new InvalidDataException("Invalid packed curve point.");
            return new[] { (v >> 8) / 255d, (v & 255) / 255d };
        }).ToArray() };
    }).ToArray();
    public object ReadLights(string section)
    {
        var count = UInts(section, "NUM_ACTIVE_LIGHTS", 1)[0];
        if (count > 64) throw new InvalidDataException("Invalid light count.");
        return Enumerable.Range(0, (int)count).Select(slot => {
            var w = UInts(section, "RELIGHT_LIGHT_DATA_" + slot, 4);
            double Half(uint v) => (double)BitConverter.UInt16BitsToHalf((ushort)(v & 0x7fff));
            return new { slot, type = w[1] & 3, position = new[] { (w[0] >> 20) / 4095d, ((w[0] >> 8) & 4095) / 4095d, Half(w[1] >> 17) },
                radius = Half(w[1] >> 2), intensity = Half(w[2]), colorMode = (w[2] & 0x8000) != 0 ? "rgb" : "temperature_tint",
                temperatureTint = (w[2] & 0x8000) != 0 ? null : new[] { (w[2] >> 24) / 127d - 1, ((w[2] >> 16) & 255) / 127d - 1 }, packed = w };
        }).ToArray();
    }
}
