using System.Diagnostics;
using System.Text.Json;
using OfflineReShade.Core;

internal static class EffectSourceTests
{
    public static async Task RunAsync(string folder)
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        static void Fails<T>(Action action, string message) where T : Exception
        { try { action(); } catch (T) { return; } throw new Exception(message); }

        var root = Path.Combine(folder, "effect-source", "Effects");
        var outside = Path.Combine(folder, "effect-source", "Outside");
        Directory.CreateDirectory(Path.Combine(root, "Shared")); Directory.CreateDirectory(Path.Combine(root, "Other")); Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(root, "Main.fx"), string.Join('\n',
            "#include \"Shared/common.fxh\"", "#include \"Local.fxh\"", "#include \"Missing.fxh\"",
            "uniform float Gain < ui_min = 0.0; ui_max = 1.0; > = 0.5;", "", "float4 PS() : SV_Target", "{",
            "    float g = Gain * Gain;", "    return Helper() * g;", "}", "technique MainPass { pass { PixelShader = PS; } }"));
        await File.WriteAllTextAsync(Path.Combine(root, "Local.fxh"), "// resolved from the including file's directory\n#define LOCAL_SCALE 2\n");
        await File.WriteAllTextAsync(Path.Combine(root, "Shared", "common.fxh"), "#include \"deep.fxh\"\nfloat Helper() { return DEEP_VALUE; }\n");
        await File.WriteAllTextAsync(Path.Combine(root, "Shared", "deep.fxh"), "#define DEEP_VALUE 1.0\n");
        await File.WriteAllTextAsync(Path.Combine(root, "Other", "Other.fx"), "uniform float Gain = 1.0;\n" + string.Concat(Enumerable.Repeat("float x = Gain;\n", 9)));
        await File.WriteAllTextAsync(Path.Combine(root, "Other", "deep.fxh"), "// same name as Shared/deep.fxh\n");
        await File.WriteAllTextAsync(Path.Combine(root, "notes.txt"), "Gain is documented here, but this is not shader source.");
        await File.WriteAllTextAsync(Path.Combine(root, "Long.fxh"), string.Concat(Enumerable.Repeat(new string('x', 900) + "\n", 120)));
        await File.WriteAllTextAsync(Path.Combine(outside, "Secret.fx"), "uniform float Gain = 9.0; // outside the effect directory\n");
        var junction = Path.Combine(root, "Linked");
        var linked = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        await linked.WaitForExitAsync();
        var haveJunction = linked.ExitCode == 0 && Directory.Exists(Path.Combine(junction));

        var library = new EffectSourceLibrary(root + Path.DirectorySeparatorChar);
        Check(library.Root == root, "The effect directory was not normalized.");
        var all = library.Search("Gain", maxResults: 100, contextLines: 0);
        var files = all.Hunks.Select(x => x.File).Distinct().ToArray();
        Check(files.SequenceEqual(["Main.fx", "Other/Other.fx"]), "Whole-directory search returned unexpected files: " + string.Join(", ", files));
        Check(all.Matches == 12 && !all.Truncated, $"Expected 12 matching lines, found {all.Matches}.");

        var scoped = library.Search("Gain", "Main.fx", contextLines: 3);
        Check(scoped.Hunks.All(x => x.File == "Main.fx") && scoped.Hunks.Length == 1 && scoped.Hunks[0].MatchLines.SequenceEqual([4, 8]),
            "Nearby matches were not merged into one excerpt, or the effect scope leaked into other effects.");
        Check(scoped.Hunks[0].Excerpt.Contains("> 8 | ") && scoped.Hunks[0].Excerpt.Contains(" 7 | {"), "Excerpt lines lost their numbers or match markers.");
        Check(scoped.Scope.Contains("3 included files") && scoped.FilesSearched == 4, "Include closure was wrong: " + scoped.Scope);
        Check(library.Search("DEEP_VALUE", "Main.fx").Hunks.Select(x => x.File).SequenceEqual(["Shared/common.fxh", "Shared/deep.fxh"]),
            "A nested include was not resolved from the including file's directory first.");
        Check(scoped.Notes.Any(x => x.Contains("Missing.fxh")), "An unresolved include was not reported.");
        Check(library.Search("LOCAL_SCALE", "main").Matches == 1, "A partial effect name did not select the effect.");
        Check(library.Search("gain", caseSensitive: true).Matches == 0 && library.Search("gain").Matches == 12, "Case sensitivity was ignored.");
        Check(library.Search(@"Gain\s*\*\s*Gain", regex: true).Matches == 1, "Regex search failed.");

        var capped = library.Search("Gain", "Other.fx", contextLines: 0, maxResults: 3);
        Check(capped.Matches == 3 && capped.Truncated && capped.ToText().Contains("More matches exist"), "maxResults did not cap and report truncation.");
        Fails<ArgumentException>(() => library.Search("(?<=x)y", regex: true), "A backtracking-only regex was accepted.");
        Fails<ArgumentException>(() => library.Search("(", regex: true), "An invalid regex was accepted.");
        Fails<ArgumentException>(() => library.Search(""), "An empty pattern was accepted.");
        Fails<ArgumentException>(() => library.Search("x", contextLines: 21), "contextLines above the cap was accepted.");
        Fails<FileNotFoundException>(() => library.Search("x", "Absent.fx"), "An unknown effect name was accepted.");

        var excerpt = library.Read("Shared/common.fxh");
        Check(excerpt.File == "Shared/common.fxh" && excerpt.StartLine == 1 && excerpt.EndLine == 2 && excerpt.TotalLines == 2 && excerpt.Text.Contains("2 | float Helper()"), "Relative read failed.");
        Check(library.Read("common.fxh", 2, 1).Text.StartsWith(" 2 | float Helper()"), "A unique bare file name did not resolve.");
        Check(library.Read(Path.Combine(root, "Main.fx"), 8, 1).Text.Contains("Gain * Gain"), "An absolute path inside the directory was rejected.");
        Fails<ArgumentException>(() => library.Read("deep.fxh"), "An ambiguous bare file name was accepted.");
        Fails<UnauthorizedAccessException>(() => library.Read("../Outside/Secret.fx"), "Parent traversal escaped the effect directory.");
        Fails<UnauthorizedAccessException>(() => library.Read(Path.Combine(outside, "Secret.fx")), "An absolute path outside the effect directory was read.");
        Fails<UnauthorizedAccessException>(() => library.Read("Shared/../../Outside/Secret.fx"), "A nested traversal escaped the effect directory.");
        Fails<UnauthorizedAccessException>(() => library.Read("./notes.txt"), "A non-shader file was read.");
        Fails<UnauthorizedAccessException>(() => library.Read("./Main.fx:hidden.fx"), "An alternate data stream path was accepted.");
        Fails<FileNotFoundException>(() => library.Read("Shared/absent.fxh"), "A missing file did not report not found.");
        Fails<ArgumentException>(() => library.Read("Main.fx", 99), "A start line past the end was accepted.");
        Fails<ArgumentException>(() => library.Read("Main.fx", 1, EffectSourceLibrary.MaxReadLines + 1), "lineCount above the cap was accepted.");
        var limited = library.Read("Long.fxh", 1, 120);
        Check(limited.Truncated && limited.Text.Length <= EffectSourceLibrary.MaxOutputChars && limited.EndLine < 120 && limited.ToText().Contains($"startLine={limited.EndLine + 1}"),
            "A long read was not capped with a continuation point.");
        if (haveJunction)
        {
            Check(!library.Search("outside the effect directory").Hunks.Any(), "Search followed a junction out of the effect directory.");
            Fails<UnauthorizedAccessException>(() => library.Read("Linked/Secret.fx"), "Read followed a junction out of the effect directory.");
            Directory.Delete(junction);
        }
        else Console.WriteLine("SKIP: junction checks (mklink /J unavailable).");

        // The MCP tools take the effect directory from get_operation_status, both from an independent
        // engine and through the WinUI automation broker.
        var input = new RenderInput(Path.Combine(root, "Main.fx"), OutputPath: Path.Combine(folder, "effect-source", "out.png"), EffectDir: root);
        Task<JsonElement> Call(string method, object? args, CancellationToken token) =>
            Task.FromResult(AutomationEngine.Json(new { width = 8, height = 8, loading = false, frameCount = 1, inputGeneration = 1 }));
        var engine = new AutomationEngine(Call, (_, _) => Task.CompletedTask, () => input);
        Check(EffectSourceLibrary.EffectDirectoryFrom(await engine.ExecuteAsync("get_operation_status", AutomationEngine.Json(new { }))) == null,
            "A disconnected session reported an effect directory.");
        await engine.ExecuteAsync("get_runtime_info", AutomationEngine.Json(new { }));
        Check(EffectSourceLibrary.EffectDirectoryFrom(await engine.ExecuteAsync("get_operation_status", AutomationEngine.Json(new { }))) == root,
            "The applied input's effect directory was not found in get_operation_status.");
        using (var broker = new AutomationBroker(engine.ExecuteAsync, "effect source tests"))
        {
            using var connection = new RuntimeConnection();
            await connection.ConnectAsync(broker.Descriptor.PipeName);
            Check(EffectSourceLibrary.EffectDirectoryFrom(await connection.CallAsync("get_operation_status")) == root, "Attached sessions lost the effect directory.");
        }
        var waitingRoot = Path.Combine(folder, "effect-source", "Waiting"); Directory.CreateDirectory(waitingRoot);
        var listener = new AutomationEngine(Call, (_, _) => Task.CompletedTask, () => new RenderInput(""));
        await listener.ExecuteAsync("listen_input", AutomationEngine.Json(input with { ColorPath = Path.Combine(waitingRoot, "coloroutput.png"), EffectDir = waitingRoot }));
        Check(EffectSourceLibrary.EffectDirectoryFrom(await listener.ExecuteAsync("get_operation_status", AutomationEngine.Json(new { }))) == waitingRoot,
            "A listening session did not report the effect directory it is waiting with.");
        await listener.ExecuteAsync("stop_listening", AutomationEngine.Json(new { }));
        Console.WriteLine("PASS: effect source search/read scope, include resolution, output caps, regex limits, path and link containment, session effect directory");
    }
}
