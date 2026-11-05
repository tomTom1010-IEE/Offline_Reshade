using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OfflineReShade.Core;

public sealed record EffectFingerprint(string EffectName, string Hash, string[] Files);

public sealed record EffectSourceHunk(string File, int StartLine, int EndLine, int[] MatchLines, string Excerpt);

public sealed record EffectSourceSearch(string EffectDirectory, string Pattern, string Scope, int FilesSearched, int Matches, bool Truncated,
    string[] Notes, EffectSourceHunk[] Hunks)
{
    public string ToText()
    {
        var text = new StringBuilder();
        var files = Hunks.Select(x => x.File).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        text.Append(Matches == 0 ? "No matches" : $"{Matches} matching line{(Matches == 1 ? "" : "s")} in {files} file{(files == 1 ? "" : "s")}")
            .Append($" for \"{Pattern}\" ({Scope}; {FilesSearched} files searched under {EffectDirectory})\n");
        foreach (var hunk in Hunks) text.Append($"--- {hunk.File}:{hunk.StartLine}-{hunk.EndLine}\n").Append(hunk.Excerpt);
        if (Truncated) text.Append($"[More matches exist. Narrow the pattern, pass effectName, or raise maxResults (at most {EffectSourceLibrary.MaxResults}).]\n");
        foreach (var note in Notes) text.Append("Note: ").Append(note).Append('\n');
        return text.ToString();
    }
}

public sealed record EffectSourceExcerpt(string EffectDirectory, string File, int StartLine, int EndLine, int TotalLines, bool Truncated, string Text)
{
    public string ToText() => $"--- {File} lines {StartLine}-{EndLine} of {TotalLines}\n" + Text +
        (Truncated ? $"[Output limit reached. Continue with startLine={EndLine + 1}.]\n" : EndLine < TotalLines ? $"[Continue with startLine={EndLine + 1}.]\n" : "");
}

// Bounded, read-only access to the shader sources of one effect directory. Agents use it when
// list_effects metadata cannot explain a parameter: non-linear sliders, blend formulas, depth
// conventions or setup rules that only exist in code. Resolved paths never leave the directory,
// and links, hidden and system entries inside it are not followed.
public sealed class EffectSourceLibrary
{
    public const int MaxPatternLength = 512, MaxContextLines = 20, MaxResults = 100, MaxReadLines = 400, MaxOutputChars = 32_000;
    private const int MaxFiles = 5_000, MaxIncludedFiles = 256, MaxExcerptLineChars = 300, MaxReadLineChars = 1_000;
    private const long MaxFileBytes = 4L << 20;
    private const FileAttributes Skipped = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint;
    public static readonly IReadOnlyList<string> Extensions = [".fx", ".fxh", ".addonfx", ".h", ".hlsl", ".hlsli", ".inc"];
    private static readonly HashSet<string> ExtensionSet = new(Extensions, StringComparer.OrdinalIgnoreCase);
    private static readonly Regex IncludeDirective = new(@"^\s*#\s*include\s*[""<]([^"">\r\n]+)["">]", RegexOptions.CultureInvariant);
    private static readonly Regex CitationPattern = new(@"^(?<file>.+?):(?<start>\d+)(?:-(?<end>\d+))?$", RegexOptions.CultureInvariant);
    private static readonly EnumerationOptions Walk = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchType = MatchType.Win32, AttributesToSkip = Skipped };

    public EffectSourceLibrary(string effectDirectory)
    {
        if (string.IsNullOrWhiteSpace(effectDirectory)) throw new ArgumentException("The session has no effect directory. Open an input first.");
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(effectDirectory));
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException("The session's effect directory does not exist: " + Root);
    }

    public string Root { get; }

    // The effect directory of a session from its get_operation_status result: the applied input's,
    // else the one a listener is waiting to open. WinUI and independent MCP report the same shape.
    public static string? EffectDirectoryFrom(JsonElement operationStatus)
    {
        static string? FromInput(JsonElement input) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty("effectDir", out var dir) && dir.ValueKind == JsonValueKind.String ? dir.GetString() : null;
        var applied = operationStatus.TryGetProperty("appliedInput", out var input) ? FromInput(input) : null;
        if (!string.IsNullOrEmpty(applied)) return applied;
        if (!operationStatus.TryGetProperty("waitingInput", out var waiting) || waiting.ValueKind != JsonValueKind.Object || !waiting.TryGetProperty("input", out var pending)) return null;
        var awaited = FromInput(pending);
        return string.IsNullOrEmpty(awaited) ? null : awaited;
    }

    public EffectSourceSearch Search(string pattern, string effectName = "", bool regex = false, bool caseSensitive = false,
        int contextLines = 3, int maxResults = 20, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(pattern) || pattern.Length > MaxPatternLength) throw new ArgumentException($"pattern must be 1-{MaxPatternLength} characters.");
        if (contextLines is < 0 or > MaxContextLines) throw new ArgumentException($"contextLines must be 0-{MaxContextLines}.");
        if (maxResults is < 1 or > MaxResults) throw new ArgumentException($"maxResults must be 1-{MaxResults}.");
        var isMatch = Matcher(pattern, regex, caseSensitive);
        var notes = new List<string>();
        var all = ShaderFiles(notes);
        var (files, scope) = string.IsNullOrWhiteSpace(effectName) ? (all, "all shader files") : EffectClosure(effectName.Trim(), all, notes);

        var hunks = new List<EffectSourceHunk>();
        int found = 0, chars = 0, searched = 0;
        var truncated = false;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var lines = ReadLines(file, notes);
            if (lines == null) continue;
            searched++;
            var hits = new List<int>();
            for (var i = 0; i < lines.Length; i++)
                if (isMatch(lines[i])) hits.Add(i);
            for (var index = 0; index < hits.Count;)
            {
                if (found == maxResults || chars >= MaxOutputChars) { truncated = true; break; }
                // Matches whose context windows touch share one excerpt.
                var start = Math.Max(0, hits[index] - contextLines);
                var end = Math.Min(lines.Length - 1, hits[index] + contextLines);
                var marked = new List<int> { hits[index++] };
                found++;
                while (index < hits.Count && found < maxResults && hits[index] - contextLines <= end + 1)
                {
                    end = Math.Min(lines.Length - 1, hits[index] + contextLines);
                    marked.Add(hits[index++]);
                    found++;
                }
                var excerpt = Excerpt(lines, start, end, marked, MaxExcerptLineChars);
                hunks.Add(new(Relative(file), start + 1, end + 1, marked.Select(x => x + 1).ToArray(), excerpt));
                chars += excerpt.Length;
            }
            if (truncated) break;
        }
        return new(Root, pattern, scope, searched, found, truncated, [.. notes.Distinct()], [.. hunks]);
    }

    public EffectSourceExcerpt Read(string file, int startLine = 1, int lineCount = 120)
    {
        if (string.IsNullOrWhiteSpace(file)) throw new ArgumentException("file is required: a path relative to the effect directory, or a unique file name.");
        if (startLine < 1) throw new ArgumentException("startLine must be 1 or greater.");
        if (lineCount is < 1 or > MaxReadLines) throw new ArgumentException($"lineCount must be 1-{MaxReadLines}.");
        var path = Resolve(file.Trim());
        var lines = ReadLines(path, null) ?? throw new IOException("The file is larger than 4 MiB and is not read.");
        if (startLine > lines.Length) throw new ArgumentException($"{Relative(path)} has {lines.Length} lines.");
        var last = Math.Min(lines.Length, startLine + lineCount - 1);
        var marked = Array.Empty<int>();
        var text = new StringBuilder();
        var end = startLine - 1;
        var truncated = false;
        for (var i = startLine - 1; i < last; i++)
        {
            var line = Excerpt(lines, i, i, marked, MaxReadLineChars, last);
            if (text.Length + line.Length > MaxOutputChars && i > startLine - 1) { truncated = true; break; }
            text.Append(line);
            end = i + 1;
        }
        return new(Root, Relative(path), startLine, end, lines.Length, truncated, text.ToString());
    }

    private static Func<string, bool> Matcher(string pattern, bool regex, bool caseSensitive)
    {
        if (!regex)
        {
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return line => line.Contains(pattern, comparison);
        }
        try
        {
            // The non-backtracking engine runs in linear time, so a pattern cannot stall the server.
            var options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
            return new Regex(pattern, options, TimeSpan.FromSeconds(2)).IsMatch;
        }
        catch (NotSupportedException error)
        {
            throw new ArgumentException("This regex needs backtracking (backreferences, lookarounds or atomic groups are not supported); simplify it or search literally. " + error.Message);
        }
        catch (ArgumentException error)
        {
            throw new ArgumentException("Invalid regex: " + error.Message);
        }
    }

    private List<string> ShaderFiles(List<string> notes)
    {
        var files = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Root, "*", Walk))
        {
            if (!ExtensionSet.Contains(Path.GetExtension(path))) continue;
            if (files.Count == MaxFiles) { notes.Add($"Only the first {MaxFiles} shader files were searched."); break; }
            files.Add(path);
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    // Identifies one version of an effect: its FX file and the headers it includes, by file name and
    // content. Moving the effect folder keeps the identity; editing any of those files changes it.
    public EffectFingerprint Fingerprint(string effectName)
    {
        var all = ShaderFiles([]);
        var named = all.Where(x => Path.GetFileName(x).Equals(effectName.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (named.Count == 0) throw new FileNotFoundException($"No effect file named '{effectName}' under {Root}. Use an effectName reported by list_effects.");
        if (named.Count > 1) throw new ArgumentException($"'{effectName}' exists more than once: {string.Join(", ", named.Take(5).Select(Relative))}.");
        var files = IncludeClosure(named, all, []);
        var entries = files.Select(file => Path.GetFileName(file).ToLowerInvariant() + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))))
            .Order(StringComparer.Ordinal);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", entries)))).ToLowerInvariant();
        return new(Path.GetFileName(named[0]), hash, files.Select(Relative).ToArray());
    }

    // Evidence of the form file:line or file:start-end names shader lines. Returns the normalized
    // citation, or null when the text is not a citation; a citation that does not resolve throws.
    public string? NormalizeCitation(string evidence)
    {
        var match = CitationPattern.Match(evidence.Trim());
        if (!match.Success || !ExtensionSet.Contains(Path.GetExtension(match.Groups["file"].Value))) return null;
        var path = Resolve(match.Groups["file"].Value.Trim());
        if (!int.TryParse(match.Groups["start"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var start)) start = -1;
        var end = start;
        if (match.Groups["end"].Success && !int.TryParse(match.Groups["end"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out end)) end = -1;
        var count = (ReadLines(path, null) ?? throw new IOException("The cited file is larger than 4 MiB.")).Length;
        if (start < 1 || end < start || end > count) throw new ArgumentException($"{evidence.Trim()} does not exist: {Relative(path)} has {count} lines.");
        return $"{Relative(path)}:{start}" + (end != start ? $"-{end}" : "");
    }

    // Notes must describe and cite code rather than reproduce it, since effect sources are often licensed.
    // Returns where a long source line appears verbatim in the text, or null.
    public string? FindCopiedSource(string text, IEnumerable<string> relativeFiles, int minimumLength = 40)
    {
        foreach (var relative in relativeFiles)
        {
            var lines = ReadLines(Path.GetFullPath(Path.Combine(Root, relative)), null) ?? [];
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length >= minimumLength && text.Contains(line, StringComparison.Ordinal)) return $"{relative}:{i + 1}";
            }
        }
        return null;
    }

    // The effect file plus the headers it includes, resolved like ReShade: the including file's
    // directory first, then the effect directory and its subdirectories in path order.
    private (List<string> Files, string Scope) EffectClosure(string effectName, List<string> all, List<string> notes)
    {
        static bool IsEffect(string path) => Path.GetExtension(path).Equals(".fx", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".addonfx", StringComparison.OrdinalIgnoreCase);
        var roots = all.Where(x => Path.GetFileName(x).Equals(effectName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (roots.Count == 0) roots = all.Where(x => IsEffect(x) && Path.GetFileName(x).Contains(effectName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (roots.Count == 0) throw new FileNotFoundException($"No effect file matching '{effectName}' under {Root}. Use an effectName reported by list_effects.");
        if (roots.Count > 8) throw new ArgumentException($"'{effectName}' matches {roots.Count} effect files. Use the exact file name, for example {Path.GetFileName(roots[0])}.");
        var files = IncludeClosure(roots, all, notes);
        var scope = string.Join(", ", roots.Select(Relative)) + (files.Count > roots.Count ? $" and {files.Count - roots.Count} included file{(files.Count - roots.Count == 1 ? "" : "s")}" : "");
        return (files, scope);
    }

    private List<string> IncludeClosure(List<string> roots, List<string> all, List<string> notes)
    {
        var known = new HashSet<string>(all, StringComparer.OrdinalIgnoreCase);
        var directories = Directory.EnumerateDirectories(Root, "*", Walk).Prepend(Root).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(roots);
        var unresolved = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            var file = pending.Dequeue();
            if (!seen.Add(file)) continue;
            if (files.Count == MaxIncludedFiles) { notes.Add($"Only {MaxIncludedFiles} files of the include tree were searched."); break; }
            files.Add(file);
            foreach (var line in ReadLines(file, notes) ?? [])
            {
                var include = IncludeDirective.Match(line);
                if (!include.Success) continue;
                if (ResolveInclude(file, include.Groups[1].Value, known, directories) is { } target) pending.Enqueue(target);
                else unresolved.Add(include.Groups[1].Value.Trim());
            }
        }
        if (unresolved.Count > 0) notes.Add("Includes not found under the effect directory: " + string.Join(", ", unresolved.Take(10)) + ".");
        return files;
    }

    private static string? ResolveInclude(string includingFile, string include, HashSet<string> known, List<string> directories)
    {
        var relative = include.Trim().Replace('/', Path.DirectorySeparatorChar);
        if (relative.Length == 0 || Path.IsPathRooted(relative)) return null;
        foreach (var directory in directories.Prepend(Path.GetDirectoryName(includingFile)!))
        {
            string candidate;
            try { candidate = Path.GetFullPath(Path.Combine(directory, relative)); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
            if (known.TryGetValue(candidate, out var actual)) return actual;
        }
        return null;
    }

    private string Resolve(string file)
    {
        if (!file.Contains('/') && !file.Contains('\\') && !Path.IsPathRooted(file))
        {
            var named = ShaderFiles([]).Where(x => Path.GetFileName(x).Equals(file, StringComparison.OrdinalIgnoreCase)).ToList();
            if (named.Count == 1) return named[0];
            if (named.Count > 1) throw new ArgumentException($"'{file}' is ambiguous: {string.Join(", ", named.Take(5).Select(Relative))}. Pass the relative path.");
            throw new FileNotFoundException($"No shader file named '{file}' under the effect directory.");
        }
        string full;
        try { full = Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(Root, file)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { throw new ArgumentException("Invalid file path: " + file); }
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Only files inside the session's effect directory can be read.");
        if (full.IndexOf(':', 2) >= 0) throw new UnauthorizedAccessException("Alternate data streams are not read.");
        if (!ExtensionSet.Contains(Path.GetExtension(full)))
            throw new UnauthorizedAccessException("Only shader sources can be read: " + string.Join(", ", Extensions) + ".");
        if (!File.Exists(full)) throw new FileNotFoundException("Shader file not found: " + Relative(full));
        for (FileSystemInfo? item = new FileInfo(full); item != null && item.FullName.Length > Root.Length; item = item is FileInfo info ? info.Directory : ((DirectoryInfo)item).Parent)
            if ((item.Attributes & Skipped) != 0) throw new UnauthorizedAccessException("Links, hidden and system entries inside the effect directory are not read.");
        return full;
    }

    // notes == null: the caller reports failures itself.
    private string[]? ReadLines(string path, List<string>? notes)
    {
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes) { notes?.Add($"Skipped {Relative(path)}: larger than 4 MiB."); return null; }
            return File.ReadAllLines(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (notes == null) throw;
            notes.Add($"Skipped {Relative(path)}: {error.Message}");
            return null;
        }
    }

    private static string Excerpt(string[] lines, int start, int end, IReadOnlyCollection<int> marked, int maxLineChars, int? widestLine = null)
    {
        var width = (widestLine ?? end + 1).ToString(CultureInfo.InvariantCulture).Length;
        var text = new StringBuilder();
        for (var i = start; i <= end; i++)
        {
            var line = lines[i].TrimEnd();
            if (line.Length > maxLineChars) line = line[..maxLineChars] + "...";
            text.Append(marked.Contains(i) ? '>' : ' ').Append((i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(width)).Append(" | ").Append(line).Append('\n');
        }
        return text.ToString();
    }

    private string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');
}
