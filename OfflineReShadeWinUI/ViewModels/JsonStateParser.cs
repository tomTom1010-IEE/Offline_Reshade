using System.Text;
using System.Text.Json;

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
