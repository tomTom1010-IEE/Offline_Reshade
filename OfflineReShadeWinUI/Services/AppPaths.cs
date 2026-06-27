namespace OfflineReShade.WinUI.Services;

public sealed class AppPaths
{
    public AppPaths()
    {
        RepositoryRoot = FindRepositoryRoot();
        PrototypePath = Path.Combine(RepositoryRoot, "bin", "x64", "Release", "OfflineReShadePrototype.exe");

        const string exportDir = @"D:\Program Files\KoikatuSunshine\UserData\cap\OfflineReShade";
        DefaultColorPath = Path.Combine(exportDir, "coloroutput.png");
        DefaultDepthPath = Path.Combine(exportDir, "depthoutput.png");
        DefaultOutputPath = Path.Combine(exportDir, "reshadeoutput.png");
        DefaultEffectDir = Path.Combine(RepositoryRoot, "bin", "x64", "Release", "OfflinePrototype", "Effects");
    }

    public string RepositoryRoot { get; }
    public string PrototypePath { get; }
    public string DefaultColorPath { get; }
    public string DefaultDepthPath { get; }
    public string DefaultOutputPath { get; }
    public string DefaultEffectDir { get; }

    private static string FindRepositoryRoot()
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

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }
}
