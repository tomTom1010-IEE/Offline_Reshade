using OfflineReShade.Core;
using System.Diagnostics;

internal static class NativeProcessTests
{
    public static async Task RunAsync(string executable, string fixtures)
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        static bool Exists(int id) { try { using var p = Process.GetProcessById(id); return !p.HasExited; } catch (ArgumentException) { return false; } }
        var work = Path.Combine(Path.GetFullPath(fixtures), "owned-runtime");
        var preset = Path.Combine(fixtures, "IntegrationPreset.ini");
        var original = await File.ReadAllBytesAsync(preset);
        await using var session = new NativeSession(executable, new(work));
        var input = new RenderInput(Path.Combine(fixtures, "first.png"), Path.Combine(fixtures, "first.rfloat"),
            EffectDir: Path.Combine(fixtures, "Effects"), PresetPath: preset, OutputPath: Path.Combine(fixtures, "owned.png"));
        async Task<double> Gain()
        {
            await RuntimeOperations.WaitForFramesAsync(session.CallAsync, 0, CancellationToken.None);
            var state = await session.CallAsync("list_state");
            return state.GetProperty("uniforms").EnumerateArray().Single(x => x.GetProperty("id").GetString() == "Integration.fx::Gain").GetProperty("value")[0].GetDouble();
        }
        await session.OpenAsync(input);
        await Gain();
        var first = session.ProcessId!.Value;
        await session.CallAsync("set_uniform", new { id = "Integration.fx::Gain", value = 0.37 });
        await session.OpenAsync(input with { Width = 160, Height = 120 });
        Check(!Exists(first) && Math.Abs(await Gain() - 0.37) < 0.001, "Rebuild leaked old host or lost parameters.");
        var second = session.ProcessId!.Value;
        await session.StopAsync();
        Check(!Exists(second), "Stop left a rendering process alive.");
        await session.OpenAsync(input);
        Check(Math.Abs(await Gain() - 0.37) < 0.001, "Stop/restart lost parameters.");
        await session.CallAsync("set_uniform", new { id = "Integration.fx::Gain", value = 0.62 });
        await session.CallAsync("set_effects_state", new { enabled = false });
        // Kill only the child created above to verify recovery from an actual process exit.
        using (var process = Process.GetProcessById(session.ProcessId!.Value)) { process.Kill(true); await process.WaitForExitAsync(); }
        await session.OpenAsync(input);
        Check(Math.Abs(await Gain() - 0.62) < 0.001, "Crash recovery used an obsolete snapshot.");
        Check(!(await session.CallAsync("get_runtime_info")).GetProperty("effectsEnabled").GetBoolean(), "Crash recovery lost the global effects switch.");
        await session.CallAsync("reload_effects");
        Check(Math.Abs(await Gain() - 0.62) < 0.001, "Reload lost parameters.");
        var final = session.ProcessId!.Value;
        await session.DisposeAsync();
        Check(!Exists(final) && !session.IsConnected, "Dispose left a process/connection alive.");
        Check(original.SequenceEqual(await File.ReadAllBytesAsync(preset)), "Lifecycle operations modified the user's preset.");
        Console.WriteLine("PASS: real native process teardown, restart, crash recovery and reload preserve unsaved edits without touching the source preset.");
    }
}
