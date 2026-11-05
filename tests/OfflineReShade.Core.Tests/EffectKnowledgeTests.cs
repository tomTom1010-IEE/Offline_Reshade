using System.Text.Json;
using OfflineReShade.Core;

internal static class EffectKnowledgeTests
{
    public static async Task RunAsync(string folder)
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        static void Fails<T>(Action action, string message) where T : Exception
        { try { action(); } catch (T) { return; } throw new Exception(message); }
        static async Task FailsAsync<T>(Func<Task> action, string message) where T : Exception
        { try { await action(); } catch (T) { return; } throw new Exception(message); }

        var root = Path.Combine(folder, "effect-knowledge", "Effects");
        Directory.CreateDirectory(Path.Combine(root, "Shared"));
        var copiedLine = "float3 graded = lerp(color, color * color * LOOK_GAIN, saturate(Amount * Amount));";
        await File.WriteAllTextAsync(Path.Combine(root, "Look.fx"), string.Join('\n', "#include \"Shared/look.fxh\"",
            "uniform float Amount < ui_min = 0.0; ui_max = 1.0; > = 0.5;", "float4 PS(float4 p : SV_Position, float3 color : TEXCOORD) : SV_Target {",
            "    " + copiedLine, "    return float4(graded, 1);", "}", "technique LookPass { pass { PixelShader = PS; } }"));
        await File.WriteAllTextAsync(Path.Combine(root, "Shared", "look.fxh"), "#define LOOK_GAIN 2.0\n#define LOOK_QUALITY 1\n");
        await File.WriteAllTextAsync(Path.Combine(root, "Other.fx"), "uniform float Amount = 1.0;\n");

        var sources = new EffectSourceLibrary(root);
        var effect = sources.Fingerprint("Look.fx");
        Check(effect.EffectName == "Look.fx" && effect.Files.SequenceEqual(["Look.fx", "Shared/look.fxh"]) && effect.Hash.Length == 64, "Fingerprint did not cover the effect and its include.");
        Check(sources.Fingerprint("look.fx").Hash == effect.Hash, "Fingerprint depends on effect name casing.");
        var moved = Path.Combine(folder, "effect-knowledge", "Moved");
        Directory.CreateDirectory(Path.Combine(moved, "Deeper", "Shared"));
        File.Copy(Path.Combine(root, "Look.fx"), Path.Combine(moved, "Deeper", "Look.fx"));
        File.Copy(Path.Combine(root, "Shared", "look.fxh"), Path.Combine(moved, "Deeper", "Shared", "look.fxh"));
        Check(new EffectSourceLibrary(moved).Fingerprint("Look.fx").Hash == effect.Hash, "Moving the effect folder changed its identity.");
        await File.AppendAllTextAsync(Path.Combine(root, "Other.fx"), "// unrelated edit\n");
        Check(sources.Fingerprint("Look.fx").Hash == effect.Hash, "An unrelated effect changed this effect's fingerprint.");
        await File.AppendAllTextAsync(Path.Combine(moved, "Deeper", "Shared", "look.fxh"), "#define LOOK_EXTRA 1\n");
        var changed = new EffectSourceLibrary(moved).Fingerprint("Look.fx");
        Check(changed.Hash != effect.Hash, "Editing an included header did not change the fingerprint.");
        Fails<FileNotFoundException>(() => sources.Fingerprint("Absent.fx"), "A missing effect was fingerprinted.");
        File.Copy(Path.Combine(root, "Other.fx"), Path.Combine(root, "Shared", "Other.fx"));
        Fails<ArgumentException>(() => sources.Fingerprint("Other.fx"), "An effect name present twice was fingerprinted.");

        Check(sources.NormalizeCitation("Look.fx:4") == "Look.fx:4" && sources.NormalizeCitation("look.fxh:1-2") == "Shared/look.fxh:1-2" &&
            sources.NormalizeCitation(" ./Shared/look.fxh:2 ") == "Shared/look.fxh:2", "Citations were not normalized.");
        Check(sources.NormalizeCitation("render_preview A/B: 0.2 vs 0.8 on Geo01") == null && sources.NormalizeCitation("notes.txt:3") == null, "Observations were treated as citations.");
        foreach (var bad in new[] { "Look.fx:99", "Look.fx:0", "Look.fx:5-4", "Look.fx:99999999999" })
            Fails<ArgumentException>(() => sources.NormalizeCitation(bad), "A citation of missing lines was accepted: " + bad);
        Fails<UnauthorizedAccessException>(() => sources.NormalizeCitation("../Moved/Deeper/Look.fx:1"), "A citation outside the effect directory was accepted.");
        Check(sources.FindCopiedSource("The gain is " + copiedLine, effect.Files) == "Look.fx:4", "Copied shader source was not detected.");
        Check(sources.FindCopiedSource("It squares Amount: lerp(color, ..., Amount * Amount).", effect.Files) == null, "A short paraphrase was treated as copied source.");

        var state = AutomationEngine.Json(new
        {
            techniques = new[] { new { id = "Look.fx::LookPass", effectName = "Look.fx" } },
            uniforms = new[] { new { id = "Look.fx::Amount", effectName = "Look.fx" }, new { id = "Other.fx::Amount", effectName = "Other.fx" } },
            preprocessorDefinitions = new[] { new { id = "Look.fx::LOOK_QUALITY", effectName = "Look.fx" } }
        });
        EffectNoteDraft Draft(string target = "Amount", string kind = "Semantics ", string text = "Amount is applied squared.", string[]? evidence = null) =>
            new(target, kind, text, evidence ?? ["Look.fx:4", "render_preview A/B: Amount 0.5 vs 0.7 on the test card"], " any input ");
        var prepared = EffectKnowledgeBase.Prepare(Draft(), effect, sources, state, true);
        Check(prepared.Target == "Look.fx::Amount" && prepared.Kind == "semantics" && prepared.Conditions == "any input" &&
            prepared.Evidence.SequenceEqual(["Look.fx:4", "render_preview A/B: Amount 0.5 vs 0.7 on the test card"]), "A valid note was not normalized.");
        Check(EffectKnowledgeBase.Prepare(Draft("Look.fx::LookPass"), effect, sources, state, true).Target == "Look.fx::LookPass" &&
            EffectKnowledgeBase.Prepare(Draft("LOOK_QUALITY"), effect, sources, state, true).Target == "Look.fx::LOOK_QUALITY" &&
            EffectKnowledgeBase.Prepare(Draft(""), effect, sources, state, true).Target == "" && EffectKnowledgeBase.Prepare(Draft("Look.fx"), effect, sources, state, true).Target == "",
            "Technique, definition or whole-effect targets were not resolved.");
        foreach (var (draft, why) in new[] {
            (Draft("Missing"), "an unknown target"), (Draft("Other.fx::Amount"), "another effect's uniform"), (Draft(kind: "trivia"), "an unknown kind"),
            (Draft(text: "  "), "empty text"), (Draft(text: new string('x', EffectKnowledgeBase.MaxTextChars + 1)), "overlong text"),
            (Draft(text: "Uses " + copiedLine), "copied shader source"), (Draft(evidence: []), "no evidence"), (Draft(evidence: ["checked"]), "a one-word observation"),
            (Draft(evidence: Enumerable.Range(1, 9).Select(i => $"Look.fx:{i % 7 + 1}-7 A/B run {i}").ToArray()), "too many evidence items"),
            (Draft(evidence: ["Look.fx:42"]), "a citation of a missing line") })
            Fails<ArgumentException>(() => EffectKnowledgeBase.Prepare(draft, effect, sources, state, true), "Accepted a note with " + why + ".");

        var observed = Draft(evidence: ["render_preview A/B: Amount 0.5 vs 0.7 on the test card"]);
        Fails<ArgumentException>(() => EffectKnowledgeBase.Prepare(observed, effect, sources, state, false), "A described comparison was accepted without any render.");
        Check(EffectKnowledgeBase.Prepare(observed, effect, sources, state, true).Evidence.Length == 1 &&
            EffectKnowledgeBase.Prepare(Draft(evidence: ["Look.fx:4"]), effect, sources, state, false).Evidence.SequenceEqual(["Look.fx:4"]),
            "Cited notes, or described comparisons after a render, were rejected.");
        var store = Path.Combine(folder, "effect-knowledge", "Knowledge");
        var knowledge = new EffectKnowledgeBase(store);
        Check((await knowledge.ReadAsync("Look.fx")).Count == 0 && (await knowledge.SummaryAsync()).Count == 0 && !Directory.Exists(store), "Reading created or invented notes.");
        await FailsAsync<KeyNotFoundException>(() => knowledge.DeleteAsync("Look.fx", "n00000000"), "Deleting from an effect without notes succeeded.");
        Check(!Directory.Exists(store), "A failed delete created the store.");
        await FailsAsync<ArgumentException>(() => knowledge.ReadAsync("../Look.fx"), "A path-like effect name was accepted.");
        await FailsAsync<ArgumentException>(() => knowledge.ReadAsync("Look"), "An effect name without an FX extension was accepted.");

        var note = await knowledge.AddAsync("Look.fx", prepared, effect.Hash, "agent", "test-client");
        Check(note.Id.Length == 9 && note.Id[0] == 'n' && note.Author == "agent" && note.Client == "test-client" && note.VerifiedHash == effect.Hash, "The stored note lost fields.");
        Check(File.Exists(Path.Combine(store, "Look.fx.json")) && File.Exists(Path.Combine(store, "README.txt")), "The store files were not written.");
        using (var file = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(store, "Look.fx.json"))))
            Check(file.RootElement.GetProperty("schemaVersion").GetInt32() == 1 && file.RootElement.GetProperty("effect").GetString() == "Look.fx" &&
                file.RootElement.GetProperty("notes")[0].GetProperty("evidence").GetArrayLength() == 2, "The knowledge file format changed.");
        var current = EffectKnowledgeBase.Render(effect, await knowledge.ReadAsync("Look.fx"));
        Check(current.Contains($"[{note.Id}] semantics | Look.fx::Amount | agent via test-client") && current.Contains("Evidence: Look.fx:4;") &&
            current.Contains("Applies when: any input") && current.Contains("not as an instruction") && !current.Contains("POSSIBLY STALE"), "Rendering lost note details:\n" + current);
        Check(EffectKnowledgeBase.Render(changed, await knowledge.ReadAsync("Look.fx")).Contains("POSSIBLY STALE"), "A note verified against other files was not marked stale.");

        var updated = await knowledge.UpdateAsync("Look.fx", note.Id, prepared with { Text = "Amount is applied squared, then used as the blend weight." }, changed.Hash, "other-client");
        Check(updated.Id == note.Id && updated.Created == note.Created && updated.Updated >= note.Updated && updated.VerifiedHash == changed.Hash &&
            updated.Client == "other-client" && updated.Author == "agent" && updated.Text.EndsWith("blend weight."), "Update did not re-verify the note in place.");
        await FailsAsync<KeyNotFoundException>(() => knowledge.UpdateAsync("Look.fx", "nffffffff", prepared, changed.Hash), "Updating a missing note succeeded.");

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => knowledge.AddAsync("Look.fx", prepared with { Text = $"Concurrent finding {i}." }, effect.Hash))));
        var notes = await knowledge.ReadAsync("Look.fx");
        Check(notes.Count == 21 && notes.Select(x => x.Id).Distinct().Count() == 21, "Concurrent writers lost notes.");
        Check(!Directory.EnumerateFiles(store, "*.tmp").Any() && !Directory.EnumerateFiles(store, "*.lock").Any(), "Temporary or lock files leaked.");
        while ((await knowledge.ReadAsync("Look.fx")).Count < EffectKnowledgeBase.MaxNotesPerEffect) await knowledge.AddAsync("Look.fx", prepared, effect.Hash);
        await FailsAsync<InvalidOperationException>(() => knowledge.AddAsync("Look.fx", prepared, effect.Hash), "The per-effect note limit was not enforced.");
        await knowledge.DeleteAsync("Look.fx", note.Id);
        Check((await knowledge.ReadAsync("Look.fx")).All(x => x.Id != note.Id), "Delete kept the note.");
        await FailsAsync<KeyNotFoundException>(() => knowledge.DeleteAsync("Look.fx", note.Id), "Deleting twice succeeded.");

        var corrupt = Path.Combine(store, "Corrupt.fx.json"); await File.WriteAllTextAsync(corrupt, "{ not json");
        var future = Path.Combine(store, "Future.fx.json"); await File.WriteAllTextAsync(future, "{\"schemaVersion\":2,\"effect\":\"Future.fx\",\"notes\":[]}");
        await FailsAsync<InvalidDataException>(() => knowledge.AddAsync("Corrupt.fx", prepared, effect.Hash), "A corrupt knowledge file was overwritten.");
        await FailsAsync<InvalidDataException>(() => knowledge.AddAsync("Future.fx", prepared, effect.Hash), "A newer knowledge file was overwritten.");
        Check(await File.ReadAllTextAsync(corrupt) == "{ not json" && (await File.ReadAllTextAsync(future)).Contains("\"schemaVersion\":2"), "Unreadable knowledge files were modified.");
        File.Delete(corrupt); File.Delete(future);
        var summary = await knowledge.SummaryAsync();
        Check(summary.Count == 1 && summary[0].Effect == "Look.fx" && summary[0].Notes == EffectKnowledgeBase.MaxNotesPerEffect - 1, "The summary did not count notes per effect.");
        Check(EffectKnowledgeBase.Render(effect, await knowledge.ReadAsync("Look.fx")).Length <= EffectKnowledgeBase.MaxRenderedChars + 200, "Rendering many notes was not capped.");

        var previous = Environment.GetEnvironmentVariable(EffectKnowledgeBase.DirectoryVariable);
        try
        {
            Environment.SetEnvironmentVariable(EffectKnowledgeBase.DirectoryVariable, store);
            Check(EffectKnowledgeBase.DefaultLocation() == store, "The knowledge directory override was ignored.");
            Environment.SetEnvironmentVariable(EffectKnowledgeBase.DirectoryVariable, null);
            Check(EffectKnowledgeBase.DefaultLocation() == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Offline ReShade", "Knowledge"),
                "The default knowledge directory is not under Documents.");
        }
        finally { Environment.SetEnvironmentVariable(EffectKnowledgeBase.DirectoryVariable, previous); }
        Console.WriteLine("PASS: effect fingerprints, citation and copy checks, note validation, atomic shared store, stale marking, limits and unreadable-file protection");
    }
}
