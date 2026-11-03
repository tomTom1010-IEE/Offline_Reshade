using OfflineReShade.Core;

internal static class WorkspaceTests
{
    public static async Task RunAsync(string folder)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static void Invalid(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid dimensions accepted."); }
        var defaults = new WorkspaceConfiguration { Editor = new() { ColorPath = "default.png", EffectDir = "Effects" } };
        const string legacy = """
            {"ColorPath":"作品.png","DepthPath":"depth.rfloat","DepthProfile":"kk","RenderWidth":"1280","RenderHeight":"720",
             "PresetPath":"custom.ini","SessionPresetPath":"session.ini","OutputPath":"render.png","InputMode":"Gallery",
             "PreviewTransport":"CPU","ShowFps":false,"SliderSymLog":true,"SliderDragSensitivity":4,
             "GalleryInputFolder":"photos","GalleryOutputFolder":"exports","GalleryBatchFrameDelay":"8",
             "KkPaths":{"ColorPath":"kk.png"},"KksPaths":{"ColorPath":"kks.png"}}
            """;
        var migrated = WorkspaceConfigurationCodec.Read(legacy, defaults);
        Check(migrated.SchemaVersion == 2 && migrated.Editor.ColorPath == "作品.png" && migrated.Editor.EffectDir == "Effects", "Legacy document/defaults lost.");
        Check(migrated.Editor.PresetPath == "custom.ini" && migrated.SessionPresetPath == "session.ini" && migrated.Editor.InputMode == "Gallery", "Legacy session/preset lost.");
        Check(migrated.Preferences is { PreviewTransport: "CPU", ShowFps: false, SliderSymLog: true, SliderDragSensitivity: 4, GalleryBatchFrameDelay: "8" }, "Preferences migration changed values.");
        Check(migrated.Preferences.KkPaths.ColorPath == "kk.png" && migrated.Preferences.KksPaths.ColorPath == "kks.png", "Game profile paths lost.");
        Check(WorkspaceConfigurationCodec.Read(WorkspaceConfigurationCodec.Write(migrated), defaults) == migrated, "V2 configuration did not round-trip.");
        var nullableLegacy = WorkspaceConfigurationCodec.Read("{\"ColorPath\":null,\"ShowFps\":null}", defaults);
        Check(nullableLegacy.Editor.ColorPath == "default.png" && nullableLegacy.Preferences.ShowFps, "Legacy nulls overwrote defaults.");

        var session = new EditingSession(migrated.Editor);
        var input = session.Draft.ToRenderInput();
        Check(!input.WatchInput && input.Width == 1280 && input.Height == 720, "Document converted incorrectly.");
        var opening = session.BeginApply(input);
        Check(session.AppliedSuccessfully(opening) && !session.HasPendingChanges, "Initial apply not represented.");
        session.Edit(session.Draft with { RenderWidth = "1920", RenderHeight = "1080" });
        Check(session.HasPendingChanges && session.Applied!.Input.Width == 1280, "Draft edit changed runtime state before apply.");
        var failed = session.BeginApply(session.Draft.ToRenderInput());
        // No success acknowledgement: a failed operation leaves previous applied state intact.
        Check(session.HasPendingChanges && session.Applied!.Input == input, "Failed apply reported the new input as active.");
        var newer = session.BeginApply(session.Draft.ToRenderInput());
        Check(session.AppliedSuccessfully(newer) && !session.AppliedSuccessfully(failed), "Stale completion overwrote current state.");
        Check(!session.HasPendingChanges, "Successful apply left a false pending state.");
        session.Edit(session.Draft with { RenderWidth = "broken" });
        Check(session.ValidationError != null && session.HasPendingChanges && session.Applied!.Input.Width == 1920, "Invalid draft changed applied dimensions.");
        Invalid(() => session.Draft.ToRenderInput());
        foreach (var size in new[] { "-1", "1.5", "16385", "999999999999999" }) Invalid(() => (migrated.Editor with { RenderWidth = size }).ToRenderInput());
        Invalid(() => (migrated.Editor with { RenderWidth = "", RenderHeight = "720" }).ToRenderInput());
        Invalid(() => (migrated.Editor with { RenderWidth = "16384", RenderHeight = "16384" }).ToRenderInput());
        var beforeClose = session.BeginApply(input);
        session.Disconnected();
        Check(session.Applied == null && !session.AppliedSuccessfully(beforeClose), "Late reply resurrected a disconnected session.");

        var portable = Path.Combine(folder, "portable.json");
        var destination = Path.Combine(folder, "migrated-settings.json");
        await File.WriteAllTextAsync(portable, legacy);
        var store = new WorkspaceSettingsFile(destination);
        Check(store.Load(defaults, portable) == migrated, "Portable fallback was not loaded.");
        await store.SaveAsync(migrated);
        Check(await File.ReadAllTextAsync(destination + ".before-v2.bak") == legacy, "Migration did not preserve original bytes.");
        Check(new WorkspaceSettingsFile(destination).Load(defaults) == migrated, "Restart did not restore migrated values.");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await store.SaveAsync(defaults, cancelled.Token); } catch (OperationCanceledException) { }
            Check(new WorkspaceSettingsFile(destination).Load(defaults) == migrated, "Cancelled save overwrote settings.");
        }
        var saves = Enumerable.Range(0, 30).Select(i => store.SaveAsync(migrated with { Editor = migrated.Editor with { OutputPath = i + ".png" } })).ToArray();
        await Task.WhenAll(saves);
        Check(new WorkspaceSettingsFile(destination).Load(defaults)!.Editor.OutputPath == "29.png", "An older autosave overwrote the newest request.");
        Check(!Directory.EnumerateFiles(folder, "*.tmp").Any(), "Temporary settings leaked.");
        foreach (var corrupt in new[] { "{", "{\"schemaVersion\":999}", "{\"schemaVersion\":2}" })
        {
            await File.WriteAllTextAsync(destination, corrupt);
            var protectedStore = new WorkspaceSettingsFile(destination);
            Check(protectedStore.Load(defaults) == null && protectedStore.LoadError != null, "Unreadable/future configuration was not protected.");
            try { await protectedStore.SaveAsync(defaults); throw new Exception("Protected settings overwritten."); } catch (IOException) { }
            Check(await File.ReadAllTextAsync(destination) == corrupt, "Failed migration destroyed original settings.");
        }
        Console.WriteLine("PASS: document/applied separation, strict dimensions, stale completion, legacy settings migration, atomic ordered persistence and future/corrupt file protection.");
    }
}
