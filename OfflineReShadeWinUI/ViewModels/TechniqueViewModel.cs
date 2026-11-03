using OfflineReShade.WinUI.Mvvm;

namespace OfflineReShade.WinUI.ViewModels;

public sealed class TechniqueViewModel : ObservableObject
{
    private bool _isEnabled;

    public TechniqueViewModel(string id, string effectName, string name, bool isEnabled)
    {
        Id = id;
        EffectName = effectName;
        Name = name;
        _isEnabled = isEnabled;
    }

    public string Id { get; }
    public string EffectName { get; }
    public string Name { get; }
    public string Label => $"{Name} [{EffectName}]";
    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }
}
