namespace OfflineReShade.WinUI.ViewModels;

public enum EffectListItemKind { Section, Hint, Effect }

// One row of the effects list: a section header, a hint, or an effect file with its techniques.
public sealed class EffectListItem
{
    private EffectListItem(EffectListItemKind kind, string name, int count, IReadOnlyList<TechniqueViewModel> techniques, EffectControlViewModel? parameters)
    {
        Kind = kind;
        Name = name;
        Count = count;
        Techniques = techniques;
        Parameters = parameters;
    }

    public EffectListItem(string effectName, IReadOnlyList<TechniqueViewModel> techniques, EffectControlViewModel? parameters)
        : this(EffectListItemKind.Effect, effectName, 0, techniques, parameters)
    {
    }

    public static EffectListItem Section(string title, int count) => new(EffectListItemKind.Section, title, count, Array.Empty<TechniqueViewModel>(), null);
    public static EffectListItem Hint(string text) => new(EffectListItemKind.Hint, text, 0, Array.Empty<TechniqueViewModel>(), null);

    public EffectListItemKind Kind { get; }
    public string Name { get; }
    public int Count { get; }
    public IReadOnlyList<TechniqueViewModel> Techniques { get; }
    public EffectControlViewModel? Parameters { get; }
    public bool IsEnabled => Techniques.Any(static technique => technique.IsEnabled);
    public int EnabledCount => Techniques.Count(static technique => technique.IsEnabled);
    public string DisplayName => Kind == EffectListItemKind.Effect && Name.EndsWith(".fx", StringComparison.OrdinalIgnoreCase) ? Name[..^3] : Name;

    public bool Matches(string query) => query.Length == 0 ||
        Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Techniques.Any(technique => technique.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
        Parameters?.Uniforms.Any(uniform => uniform.Label.Contains(query, StringComparison.OrdinalIgnoreCase) || uniform.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) == true;
}
