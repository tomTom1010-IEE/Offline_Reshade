using System.ComponentModel;
using ModelContextProtocol.Server;

namespace OfflineReShade.Mcp;

[McpServerResourceType]
public sealed class McpResources
{
    public const string AgentGuideUri = "offline-reshade://guide/agent";

    // Clients show this to the model at connection time, so it stays short and points at the guide.
    public const string Instructions =
        "Offline ReShade applies ReShade FX to still images (color plus optional depth) and returns previews and exports.\n" +
        "Workflow: open_input (or attach_session), wait until get_status.loading is false, then list_effects filtered by effectName. " +
        "Enable one effect at a time, render_preview, adjust, then stack the next; finish with save_preset or export_image.\n" +
        "Technique label/ui_tooltip often state setup and order rules (for example \"enable and move to the top\"); apply them with reorder_techniques. " +
        "Techniques marked hidden, forceEnabled or timeoutMs are shader-managed passes: do not toggle them.\n" +
        "Uniform value and defaultValue are shader units, and labels do not reveal non-linear use: some sliders are squared or remapped in code. " +
        "When a parameter's effect is unclear, call search_effect_source with its name and read the surrounding code with get_effect_source.\n" +
        "Before tuning an effect, call get_effect_knowledge for notes earlier sessions verified. As soon as you verify something non-obvious " +
        "(a cited shader line or a render_preview A/B), record it with record_effect_note; never record per-image values such as focus or exposure.\n" +
        "Read the resource " + AgentGuideUri + " for depth conventions, ordering heuristics and known traps.";

    private static readonly Lazy<string> Guide = new(() =>
    {
        using var stream = typeof(McpResources).Assembly.GetManifestResourceStream("OfflineReShade.Mcp.AgentGuide.md")
            ?? throw new InvalidOperationException("The agent guide is missing from this build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    [McpServerResource(UriTemplate = AgentGuideUri, Name = "agent_guide", Title = "Offline ReShade agent guide", MimeType = "text/markdown")]
    [Description("How to tune ReShade FX through this server: the isolate, preview and stack loop; technique and uniform metadata; reading shader source; depth conventions; ordering heuristics; known traps.")]
    public static string AgentGuide() => Guide.Value;
}
