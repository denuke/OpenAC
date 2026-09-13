using System.Text.Json;
using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Mcp;
using AcDream.Plugins.Agent.Tests.Fakes;

namespace AcDream.Plugins.Agent.Tests.Mcp;

/// <summary>A real agent service with its MCP tools, driven one update tick at a time.</summary>
internal sealed class McpToolHarness : IDisposable
{
    internal static readonly McpSession Session = new McpSessions().Create("2025-06-18");

    internal McpToolHarness()
    {
        Host.FakeAutomation.FakeNavigation.Snapshot = Body(heading: 0f);
        Service = new AgentService(Host, _ => new RecordingSink());
        Tools = McpTools.Create(Service.Context);
    }

    internal FakePluginHost Host { get; } = new();

    internal AgentService Service { get; }

    internal McpToolHost Tools { get; }

    internal static PluginNavigationSnapshot Body(float heading) =>
        new(true, false, 0x50000001u, new PluginNavigationPosition(0u, 0d, 0d, 0d, heading, true), false, false);

    /// <summary>Attaches a consumer, so state and events are tracked as they are when a client listens.</summary>
    internal void StartTracking() =>
        Service.HandleCommand(new PluginCommand(
            "agent",
            "record " + Path.Combine(Path.GetTempPath(), "mcp-tools.jsonl"),
            "/agent record"));

    /// <summary>Calls a tool and runs the one update tick that starts it.</summary>
    internal async Task<JsonObject> CallAsync(string name, JsonObject arguments)
    {
        Task<JsonObject> call = Begin(name, arguments);
        Tools.Tick();
        if (!call.IsCompleted)
            throw new InvalidOperationException($"the {name} call did not answer within one update tick");
        return await call;
    }

    internal Task<JsonObject> Begin(string name, JsonObject arguments) =>
        Tools.CallAsync(name, arguments, Session, CancellationToken.None);

    /// <summary>One game update: the service first, then the tools, as the plugin runs them.</summary>
    internal void Tick(double elapsedSeconds = 0.016)
    {
        Service.OnTick(elapsedSeconds);
        Tools.Tick();
    }

    internal static JsonElement Structured(JsonObject result) =>
        JsonDocument.Parse(result["structuredContent"]!.ToJsonString()).RootElement;

    internal static bool IsError(JsonObject result) => result["isError"]!.GetValue<bool>();

    public void Dispose() => Service.Dispose();
}
