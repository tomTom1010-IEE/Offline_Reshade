namespace OfflineReShade.Core;

public sealed class WorkspaceSettingsFile
{
    private readonly object _writes = new();
    private long _requestedWrite;
    private string? _loadedSource;
    public string Path { get; }
    public string? LoadError { get; private set; }
    public WorkspaceSettingsFile(string path) => Path = path;

    public WorkspaceConfiguration? Load(WorkspaceConfiguration defaults, string? legacyPath = null)
    {
        var source = File.Exists(Path) ? Path : legacyPath;
        if (source == null || !File.Exists(source)) return null;
        try
        {
            var json = File.ReadAllText(source);
            var result = WorkspaceConfigurationCodec.Read(json, defaults);
            _loadedSource = source;
            LoadError = null;
            return result;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            LoadError = "Settings could not be read; the original file is protected from automatic overwrite. " + ex.Message;
            return null;
        }
    }

    public Task SaveAsync(WorkspaceConfiguration configuration, CancellationToken token = default)
    {
        var version = Interlocked.Increment(ref _requestedWrite);
        return Task.Run(() => Write(configuration, version, token), token);
    }
    public void Save(WorkspaceConfiguration configuration) => Write(configuration, Interlocked.Increment(ref _requestedWrite), CancellationToken.None);

    private void Write(WorkspaceConfiguration configuration, long version, CancellationToken token)
    {
        lock (_writes)
        {
            string? temporary = null;
            try
            {
                token.ThrowIfCancellationRequested();
                if (version != Interlocked.Read(ref _requestedWrite)) return;
                if (LoadError != null) throw new IOException(LoadError);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
                if (_loadedSource != null && !File.Exists(Path + ".before-v2.bak"))
                {
                    File.Copy(_loadedSource, Path + ".before-v2.bak", false);
                    _loadedSource = null;
                }
                temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, WorkspaceConfigurationCodec.Write(configuration));
                token.ThrowIfCancellationRequested();
                File.Move(temporary, Path, true);
            }
            finally { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
