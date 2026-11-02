namespace OfflineReShade.WinUI.Services;

public sealed class AppPaths
{
    public AppPaths()
    {
        AppDirectory = AppContext.BaseDirectory;
        RepositoryRoot = FindRepositoryRoot() ?? AppDirectory;
        UserSettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OfflineReShade", "settings.json");

        var packagedPrototypePath = Path.Combine(AppDirectory, "OfflineReShadePrototype.exe");
        var developmentOutputDir = Path.Combine(RepositoryRoot, "bin", "x64", "Release");
        if (File.Exists(packagedPrototypePath))
        {
            PrototypePath = packagedPrototypePath;
            DefaultEffectDir = "Effects";
            TryCreateDirectory(Path.Combine(AppDirectory, DefaultEffectDir));
        }
        else
        {
            PrototypePath = Path.Combine(developmentOutputDir, "OfflineReShadePrototype.exe");
            DefaultEffectDir = Path.Combine(developmentOutputDir, "OfflinePrototype", "Effects");
        }

        DefaultColorPath = string.Empty;
        DefaultDepthPath = string.Empty;
        DefaultOutputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "OfflineReShade", "reshadeoutput.png");
    }

    public string AppDirectory { get; }
    public string RepositoryRoot { get; }
    public string PrototypePath { get; }
    public string UserSettingsPath { get; }
    public string DefaultColorPath { get; }
    public string DefaultDepthPath { get; }
    public string DefaultOutputPath { get; }
    public string DefaultEffectDir { get; }

    private static void TryCreateDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

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
