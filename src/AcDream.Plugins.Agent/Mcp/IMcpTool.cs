using System.Text.Json.Nodes;

namespace AcDream.Plugins.Agent.Mcp;

/// <summary>One MCP tool. Started and stepped only on the update thread.</summary>
internal interface IMcpTool
{
    string Name { get; }

    JsonObject Definition();

    IMcpToolRun Start(JsonObject arguments, McpSession session);
}

/// <summary>A tool call in progress.</summary>
internal interface IMcpToolRun
{
    /// <summary>The MCP result when the call is done, or <see langword="null"/> to be stepped again.</summary>
    JsonObject? Step();
}

internal sealed class FinishedRun(JsonObject result) : IMcpToolRun
{
    public JsonObject? Step() => result;
}

internal sealed class PollingRun(Func<JsonObject?> step) : IMcpToolRun
{
    public JsonObject? Step() => step();
}
