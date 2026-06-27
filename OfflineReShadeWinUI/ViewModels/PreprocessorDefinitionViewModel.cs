namespace OfflineReShade.WinUI.ViewModels;

public sealed class PreprocessorDefinitionViewModel
{
    public PreprocessorDefinitionViewModel(string id, string effectName, string name, string defaultValue, string value)
    {
        Id = id;
        EffectName = effectName;
        Name = name;
        DefaultValue = defaultValue;
        Value = value;
    }

    public string Id { get; }
    public string EffectName { get; }
    public string Name { get; }
    public string DefaultValue { get; }
    public string Value { get; set; }
}
