using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using OfflineReShade.Core;
using OfflineReShade.Mcp;

var native = Path.Combine(AppContext.BaseDirectory, "OfflineReShadePrototype.exe");
if (!File.Exists(native)) native = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "OfflineReShadePrototype.exe"));
string? session = null;
var knowledgeDirectory = EffectKnowledgeBase.DefaultLocation();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--native" && i + 1 < args.Length) native = Path.GetFullPath(args[++i]);
    else if (args[i] == "--session" && i + 1 < args.Length) session = args[++i];
    else if (args[i] == "--knowledge-dir" && i + 1 < args.Length) knowledgeDirectory = Path.GetFullPath(args[++i]);
    else if (args[i] == "--help")
    {
        Console.Error.WriteLine("OfflineReShade.Mcp [--native <OfflineReShadePrototype.exe>] [--session <WinUI session id>] [--knowledge-dir <directory>]\n" +
            "Transport: MCP stdio. Protocol output is reserved for stdout.\n" +
            $"Effect notes default to {EffectKnowledgeBase.DefaultLocation()} (or %{EffectKnowledgeBase.DirectoryVariable}%).");
        return;
    }
    else throw new ArgumentException("Unknown or incomplete argument: " + args[i]);
}
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
var bridge = new McpBridge(native);
if (session != null) await bridge.AttachAsync(session, CancellationToken.None);
builder.Services.AddSingleton(bridge);
builder.Services.AddSingleton(new EffectKnowledgeBase(knowledgeDirectory));
builder.Services.AddMcpServer(options => options.ServerInstructions = McpResources.Instructions)
    .WithStdioServerTransport().WithTools<McpTools>().WithResources<McpResources>();
using var host = builder.Build();
try { await host.RunAsync(); }
finally { await bridge.DisposeAsync(); }
