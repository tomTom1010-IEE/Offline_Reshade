using OfflineReShade.WinUI.Mvvm;

namespace OfflineReShade.WinUI.ViewModels;

public sealed class UniformViewModel : ObservableObject
{
    public UniformViewModel(string id, string effectName, string name, string label, string type, string uiType, string category, IReadOnlyList<object?> values, IReadOnlyList<string> items, double? minimum, double? maximum, double? step, bool readOnly = false)
    {
        Id = id;
        EffectName = effectName;
        Name = name;
        Label = label;
        Type = type;
        UiType = uiType;
        Category = string.IsNullOrWhiteSpace(category) ? "General" : category;
        Values = values.ToArray();
        Items = items.ToArray();
        Minimum = minimum;
        Maximum = maximum;
        Step = step;
        IsReadOnly = readOnly;
    }

    public string Id { get; }
    public string EffectName { get; }
    public string Name { get; }
    public string Label { get; }
    public string Type { get; }
    public string UiType { get; }
    public string Category { get; }
    public object?[] Values { get; private set; }
    public string[] Items { get; }
    public double? Minimum { get; }
    public double? Maximum { get; }
    public double? Step { get; }
    public bool IsReadOnly { get; }
    public string Tooltip { get; init; } = "";
    public string Description { get; init; } = "";
    public string Units { get; init; } = "";
    public string AddonService { get; init; } = "";

    public void SetValues(object?[] values)
    {
        Values = values;
        OnPropertyChanged(nameof(Values));
    }
}
