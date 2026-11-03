using OfflineReShade.Core;

namespace OfflineReShade.WinUI.Services;

public sealed class UserSettingsStore
{
    private readonly WorkspaceSettingsFile _file;
    public UserSettingsStore(AppPaths paths) => _file = new(paths.UserSettingsPath);
    public string SettingsPath => _file.Path;
    public string? LoadError => _file.LoadError;
    public WorkspaceConfiguration? Load(WorkspaceConfiguration defaults) => _file.Load(defaults,
        Path.Combine(AppContext.BaseDirectory, "OfflineReShadeWinUI.settings.json"));
    public void Save(WorkspaceConfiguration settings) => _file.Save(settings);
    public Task SaveAsync(WorkspaceConfiguration settings, CancellationToken token) => _file.SaveAsync(settings, token);
}
