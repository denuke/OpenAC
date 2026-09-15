using System.Globalization;
using System.Text.Json.Nodes;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;

namespace AcDream.Plugins.Agent.Mcp.Tools;

internal sealed class ObserveTool(AgentContext context) : IMcpTool
{
    internal const int RecentOutcomes = 8;

    public string Name => "observe";

    public JsonObject Definition() => ToolDefinitions.Make(
        Name,
        "Where am I and what is happening?",
        "The character's current state: whether it is in the world, where its body is, its "
        + "vitals, level and attributes, what is selected, its combat stance, the actions still "
        + "waiting for an answer, and the last few action outcomes. Every value carries a presence: "
        + "unknown means the client has not been told, never zero.",
        new JsonObject(),
        [],
        readOnly: true);

    public IMcpToolRun Start(JsonObject arguments, McpSession session)
    {
        var state = new JsonObject();
        foreach (string kind in RecordKinds.State)
            state[kind] = context.Ring.Latest(kind) is { } record ? ToolRecords.Parse(record) : null;

        var recent = new JsonArray();
        foreach (AgentRecord record in context.Ring.Recent(RecentOutcomes, ActionKinds.Terminal))
            recent.Add(ToolRecords.Parse(record));

        var pending = new JsonArray();
        foreach ((long id, string verb, string outcomeKind) in context.Outcomes.PendingActions)
        {
            pending.Add(new JsonObject
            {
                ["handle"] = id.ToString(CultureInfo.InvariantCulture),
                ["verb"] = verb,
                ["waitingFor"] = outcomeKind,
            });
        }

        return new FinishedRun(ToolResults.Of(new JsonObject
        {
            ["schema"] = AgentJson.Schema,
            ["lastSeq"] = context.Ring.LastSeq,
            ["state"] = state,
            ["pendingActions"] = pending,
            ["recentOutcomes"] = recent,
        }));
    }
}
