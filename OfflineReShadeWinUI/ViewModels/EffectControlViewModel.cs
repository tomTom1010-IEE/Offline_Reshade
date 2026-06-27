namespace OfflineReShade.WinUI.ViewModels;

public sealed class EffectControlViewModel
{
    public EffectControlViewModel(string name)
    {
        Name = name;
    }

    public string Name { get; }
    public List<UniformViewModel> Uniforms { get; } = new();
    public List<PreprocessorDefinitionViewModel> PreprocessorDefinitions { get; } = new();
}
