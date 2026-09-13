using System.Text.Json.Nodes;

namespace AcDream.Plugins.Agent.Mcp.Tools;

internal sealed class ActTool(AgentContext context) : IMcpTool
{
    public string Name => "act";

    public JsonObject Definition() => ToolDefinitions.Make(
        Name,
        "Do something (the only tool that sends)",
        "Runs one command line, such as 'use 0x70000010', 'buy prismatic taper 10', "
        + "'cast Strength Self VI', 'target nearest monster', 'run forward 60s', 'turn left 10', 'attack', "
        + "'loot list' or 'say hello'. The read tools such as spells, inventory, vendor and nearby "
        + "list the names and ids to use. Returns a handle and a status: pending is not done, so "
        + "call outcome with the handle, or pass waitSeconds, to learn what actually happened. "
        + "A word no verb claims is said aloud as chat. This is the only tool that can make the "
        + "character act.",
        new JsonObject
        {
            ["line"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "One command line.",
            },
            ["waitSeconds"] = ToolDefinitions.WaitProperty(),
        },
        ["line"],
        readOnly: false);

    public IMcpToolRun Start(JsonObject arguments, McpSession session)
    {
        string? line = ToolArguments.Text(arguments, "line");
        if (string.IsNullOrWhiteSpace(line))
            return new FinishedRun(ToolResults.Error("line must be one command line"));
        if (line.Contains('\n') || line.Contains('\r'))
            return new FinishedRun(ToolResults.Error("send one command line per call"));
        LineRecords records = ToolLines.Deliver(context, line);
        return ToolLines.Settle(context, records, ToolArguments.WaitSeconds(arguments));
    }
}
