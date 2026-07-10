using System.Text;
using System.Text.Json;
using System.Globalization;

namespace OfflineReShade.WinUI.ViewModels;

public static class JsonStateParser
{
    public static IReadOnlyList<TechniqueViewModel> ParseTechniques(JsonElement state)
    {
        var result = new List<TechniqueViewModel>();
        if (!state.TryGetProperty("techniques", out var techniques) || techniques.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in techniques.EnumerateArray())
        {
            result.Add(new TechniqueViewModel(
                GetString(item, "id"),
                GetString(item, "effectName"),
                GetString(item, "name"),
                GetBool(item, "enabled")));
        }

        return result;
    }

    public static IReadOnlyList<UniformViewModel> ParseUniforms(JsonElement state)
    {
        var result = new List<UniformViewModel>();
        if (!state.TryGetProperty("uniforms", out var uniforms) || uniforms.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in uniforms.EnumerateArray())
        {
            var name = GetString(item, "name");
            var label = GetString(item, "label");
            result.Add(new UniformViewModel(
                GetString(item, "id"),
                GetString(item, "effectName"),
                name,
                string.IsNullOrWhiteSpace(label) ? name : label,
                GetString(item, "type"),
                GetString(item, "uiType"),
                GetString(item, "category"),
                GetArrayValues(item, "value"),
                GetStringArray(item, "items"),
                GetNullableDouble(item, "min"),
                GetNullableDouble(item, "max"),
                GetNullableDouble(item, "step")));
        }

        return result;
    }

    public static IReadOnlyList<PreprocessorDefinitionViewModel> ParsePreprocessorDefinitions(JsonElement state)
    {
        var result = new List<PreprocessorDefinitionViewModel>();
        if (!state.TryGetProperty("preprocessorDefinitions", out var definitions) || definitions.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in definitions.EnumerateArray())
        {
            result.Add(new PreprocessorDefinitionViewModel(
                GetString(item, "id"),
                GetString(item, "effectName"),
                GetString(item, "name"),
                GetString(item, "defaultValue"),
                GetString(item, "value")));
        }

        return result;
    }

    public static IReadOnlyList<AddonViewModel> ParseAddons(JsonElement state)
    {
        var result = new List<AddonViewModel>();
        if (!state.TryGetProperty("addons", out var addonState) || addonState.ValueKind != JsonValueKind.Object)
            return result;
        if (!addonState.TryGetProperty("addons", out var addons) || addons.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in addons.EnumerateArray())
        {
            var name = GetString(item, "name");
            if (string.IsNullOrWhiteSpace(name))
                name = GetString(item, "file");

            result.Add(new AddonViewModel(
                name,
                GetString(item, "description"),
                GetString(item, "file"),
                GetString(item, "author"),
                GetBool(item, "loaded"),
                GetBool(item, "external"),
                GetBool(item, "hasSettingsOverlay"),
                GetStringArray(item, "overlays")));
        }

        return result;
    }

    public static bool ParseEffectsEnabled(JsonElement state)
    {
        return !state.TryGetProperty("runtime", out var runtime) || !runtime.TryGetProperty("effectsEnabled", out var enabled) || enabled.GetBoolean();
    }

    public static string ParseAddonUiDebugText(JsonElement state)
    {
        var capture = state;
        if (state.ValueKind == JsonValueKind.Object &&
            state.TryGetProperty("addonUi", out var addonUi) &&
            addonUi.ValueKind == JsonValueKind.Object)
        {
            capture = addonUi;
        }

        if (capture.ValueKind != JsonValueKind.Object)
            return "No ImGui controls captured yet.";

        var builder = new StringBuilder();
        var enabled = capture.TryGetProperty("enabled", out var enabledValue) && enabledValue.ValueKind == JsonValueKind.True;
        var frame = capture.TryGetProperty("frame", out var frameValue) && frameValue.ValueKind == JsonValueKind.Number ? frameValue.GetInt32() : -1;
        builder.Append("Capture: ").Append(enabled ? "enabled" : "disabled").Append(", frame ").Append(frame).AppendLine();

        if (capture.TryGetProperty("requestedVersions", out var versions) && versions.ValueKind == JsonValueKind.Array)
        {
            var versionText = string.Join(", ", versions.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.Number)
                .Select(static item => item.GetUInt32().ToString(CultureInfo.InvariantCulture)));
            builder.Append("ImGui table versions: ").Append(string.IsNullOrWhiteSpace(versionText) ? "(none)" : versionText).AppendLine();
        }

        if (!capture.TryGetProperty("controls", out var controls) || controls.ValueKind != JsonValueKind.Array || controls.GetArrayLength() == 0)
        {
            builder.AppendLine("No standard ImGui widgets captured yet.");
            return builder.ToString();
        }

        string currentGroup = string.Empty;
        foreach (var control in controls.EnumerateArray())
        {
            var addon = GetString(control, "addon");
            var overlay = GetString(control, "overlay");
            var group = addon + " / " + overlay;
            if (!string.Equals(group, currentGroup, StringComparison.Ordinal))
            {
                if (builder.Length != 0)
                    builder.AppendLine();
                builder.AppendLine(group);
                currentGroup = group;
            }

            var kind = GetString(control, "kind");
            var label = GetString(control, "label");
            var value = GetString(control, "value");
            var minimum = GetString(control, "min");
            var maximum = GetString(control, "max");
            var changed = GetBool(control, "changed");

            builder.Append("  - ").Append(kind);
            if (!string.IsNullOrWhiteSpace(label))
                builder.Append(": ").Append(label);
            if (!string.IsNullOrWhiteSpace(value))
                builder.Append(" = ").Append(value);
            if (!string.IsNullOrWhiteSpace(minimum) || !string.IsNullOrWhiteSpace(maximum))
                builder.Append(" [").Append(minimum).Append("..").Append(maximum).Append(']');
            if (changed)
                builder.Append(" *changed*");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    public static IReadOnlyList<AddonImGuiControlViewModel> ParseAddonImGuiControls(JsonElement state)
    {
        var capture = state;
        if (state.ValueKind == JsonValueKind.Object &&
            state.TryGetProperty("addonUi", out var addonUi) &&
            addonUi.ValueKind == JsonValueKind.Object)
        {
            capture = addonUi;
        }

        if (capture.ValueKind != JsonValueKind.Object ||
            !capture.TryGetProperty("controls", out var controls) ||
            controls.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AddonImGuiControlViewModel>();
        }

        var result = new List<AddonImGuiControlViewModel>();
        foreach (var control in controls.EnumerateArray())
        {
            var kind = GetString(control, "kind");
            if (!IsRedrawableAddonControl(kind))
                continue;

            var id = GetString(control, "id");
            if (string.IsNullOrWhiteSpace(id))
                continue;

            result.Add(new AddonImGuiControlViewModel(
                id,
                GetString(control, "addon"),
                GetString(control, "overlay"),
                GetString(control, "window"),
                kind,
                GetString(control, "label"),
                GetString(control, "value"),
                GetString(control, "min"),
                GetString(control, "max"),
                GetStringArray(control, "items"),
                GetInt(control, "components")));
        }

        return result;
    }

    private static bool IsRedrawableAddonControl(string kind)
    {
        return kind == "button" ||
            kind == "checkbox" ||
            kind == "combo" ||
            kind == "slider_float" ||
            kind == "slider_int" ||
            kind == "drag_float" ||
            kind == "drag_int" ||
            kind == "input_float" ||
            kind == "input_int" ||
            kind == "color";
    }

    public static IReadOnlyList<EffectControlViewModel> BuildEffects(IReadOnlyList<TechniqueViewModel> techniques, IReadOnlyList<UniformViewModel> uniforms, IReadOnlyList<PreprocessorDefinitionViewModel> definitions)
    {
        var enabledEffectNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var technique in techniques.Where(static technique => technique.IsEnabled))
        {
            if (enabledEffectNames.Add(technique.EffectName))
            {
                order.Add(technique.EffectName);
                aliases[NormalizeEffectName(technique.EffectName)] = technique.EffectName;
            }
        }

        var effects = order.ToDictionary(static name => name, static name => new EffectControlViewModel(name), StringComparer.OrdinalIgnoreCase);

        foreach (var uniform in uniforms)
        {
            var effectName = ResolveEnabledEffectName(uniform.EffectName, enabledEffectNames, aliases);
            if (effectName != null && effects.TryGetValue(effectName, out var effect))
                effect.Uniforms.Add(uniform);
        }

        foreach (var definition in definitions)
        {
            var effectName = ResolveEnabledEffectName(definition.EffectName, enabledEffectNames, aliases);
            if (effectName != null && effects.TryGetValue(effectName, out var effect))
                effect.PreprocessorDefinitions.Add(definition);
        }

        return order.Select(name => effects[name])
            .Where(static effect => effect.Uniforms.Count != 0 || effect.PreprocessorDefinitions.Count != 0)
            .ToArray();
    }

    private static string? ResolveEnabledEffectName(string effectName, HashSet<string> enabledEffectNames, Dictionary<string, string> aliases)
    {
        if (enabledEffectNames.Contains(effectName))
            return effectName;
        return aliases.TryGetValue(NormalizeEffectName(effectName), out var enabledEffectName) ? enabledEffectName : null;
    }

    private static string NormalizeEffectName(string effectName)
    {
        var builder = new StringBuilder(effectName.Length);
        foreach (var ch in effectName)
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    private static string GetString(JsonElement item, string name)
    {
        return item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    }

    private static bool GetBool(JsonElement item, string name)
    {
        return item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    }

    private static int GetInt(JsonElement item, string name)
    {
        return item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
    }

    private static double? GetNullableDouble(JsonElement item, string name)
    {
        return item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return array.EnumerateArray()
            .Where(static value => value.ValueKind == JsonValueKind.String)
            .Select(static value => value.GetString() ?? string.Empty)
            .ToArray();
    }

    private static IReadOnlyList<object?> GetArrayValues(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            return Array.Empty<object?>();

        var result = new List<object?>();
        foreach (var value in array.EnumerateArray())
        {
            result.Add(value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => value.GetDouble(),
                JsonValueKind.String => value.GetString(),
                _ => null
            });
        }

        return result;
    }
}
