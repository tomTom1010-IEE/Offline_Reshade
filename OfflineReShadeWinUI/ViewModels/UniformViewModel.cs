using OfflineReShade.WinUI.Mvvm;

namespace OfflineReShade.WinUI.ViewModels;

public sealed class UniformViewModel : ObservableObject
{
    public UniformViewModel(string id, string effectName, string name, string label, string type, string uiType, string category, IReadOnlyList<object?> values, IReadOnlyList<string> items, double? minimum, double? maximum, double? step)
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

    public void SetValues(object?[] values)
    {
        Values = values;
        OnPropertyChanged(nameof(Values));
    }
}
