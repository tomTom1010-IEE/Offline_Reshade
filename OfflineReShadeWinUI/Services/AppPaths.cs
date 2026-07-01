namespace OfflineReShade.WinUI.Services;

public sealed class AppPaths
{
    public AppPaths()
    {
        AppDirectory = AppContext.BaseDirectory;
        RepositoryRoot = FindRepositoryRoot() ?? AppDirectory;
        UserSettingsPath = Path.Combine(AppDirectory, "OfflineReShadeWinUI.settings.json");

        var packagedPrototypePath = Path.Combine(AppDirectory, "OfflineReShadePrototype.exe");
        var packagedEffectDir = Path.Combine(AppDirectory, "OfflinePrototype", "Effects");
        var developmentOutputDir = Path.Combine(RepositoryRoot, "bin", "x64", "Release");
        if (File.Exists(packagedPrototypePath))
        {
            PrototypePath = packagedPrototypePath;
            DefaultEffectDir = packagedEffectDir;
        }
        else
        {
            PrototypePath = Path.Combine(developmentOutputDir, "OfflineReShadePrototype.exe");
            DefaultEffectDir = Path.Combine(developmentOutputDir, "OfflinePrototype", "Effects");
        }

        const string exportDir = @"D:\Program Files\KoikatuSunshine\UserData\cap\OfflineReShade";
        DefaultColorPath = Path.Combine(exportDir, "coloroutput.png");
        DefaultDepthPath = Path.Combine(exportDir, "depthoutput.rfloat");
        DefaultOutputPath = Path.Combine(exportDir, "reshadeoutput.png");
    }

    public string AppDirectory { get; }
    public string RepositoryRoot { get; }
    public string PrototypePath { get; }
    public string UserSettingsPath { get; }
    public string DefaultColorPath { get; }
    public string DefaultDepthPath { get; }
    public string DefaultOutputPath { get; }
    public string DefaultEffectDir { get; }

    private static string? FindRepositoryRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "ReShade.sln")))
                return directory;

            var parent = Directory.GetParent(directory);
            if (parent == null)
                break;
            directory = parent.FullName;
        }

        return null;
    }
}
