using System.Text.Json.Nodes;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;

namespace AcDream.Plugins.Agent.Mcp.Tools;

internal sealed class EventsTool(AgentContext context) : IMcpTool
{
    internal const int DefaultLimit = 100;
    internal const int MaximumLimit = 500;

    public string Name => "events";

    public JsonObject Definition() => ToolDefinitions.Make(
        Name,
        "What has happened? (waits)",
        "Records the client published after a cursor, optionally only some kinds. With "
        + "waitSeconds it waits until something arrives instead of returning empty, so there is no "
        + "need to poll. until wakes the call when any record meets a condition, even a kind not "
        + "returned, for example "
        + "{\"kind\":\"vital-changed\",\"match\":{\"vital\":\"health\"},\"compare\":[\"value\",\"<\",100]}. "
        + "Pass nextSeq back as sinceSeq to continue. Omit sinceSeq to start from now, or pass -1 "
        + "for every record still kept. Sends nothing.",
        new JsonObject
        {
            ["sinceSeq"] = new JsonObject
            {
                ["type"] = "number",
                ["description"] = "The nextSeq of an earlier answer.",
            },
            ["kinds"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "Record kinds to return, such as chat, vital-changed or cast-outcome. Omit for all.",
            },
            ["until"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "object" },
                ["description"] = "Conditions of kind, match and compare; the call returns when a record meets any.",
            },
            ["limit"] = new JsonObject
            {
                ["type"] = "number",
                ["description"] = "Most records to return, up to 500.",
            },
            ["waitSeconds"] = ToolDefinitions.WaitProperty(McpToolHost.MaximumEventWaitSeconds),
        },
        [],
        readOnly: true);

    public IMcpToolRun Start(JsonObject arguments, McpSession session)
    {
        HashSet<string>? kinds = null;
        if (arguments["kinds"] is { } kindList)
        {
            if (kindList is not JsonArray items || items.Any(item => JsonRpc.Text(item) is null))
                return Refuse("kinds must be a list of record kinds");
            kinds = items.Select(item => JsonRpc.Text(item)!).ToHashSet(StringComparer.Ordinal);
        }

        List<Clause>? until = null;
        if (arguments["until"] is { } conditions)
        {
            if (conditions is not JsonArray clauses)
                return Refuse("until must be a list of conditions");
            until = [];
            foreach (JsonNode? node in clauses)
            {
                if (!Clause.TryParse(node, out Clause? clause, out string? error))
                    return Refuse(error!);
                until.Add(clause!);
            }
        }

        if (!ToolArguments.TryNumber(arguments, "sinceSeq", out double? since)
            || since is { } given && given != Math.Floor(given))
        {
            return Refuse("sinceSeq must be a whole number, the nextSeq of an earlier answer");
        }
        if (!ToolArguments.TryNumber(arguments, "limit", out double? limitGiven)
            || limitGiven is { } asked && !(asked >= 1d))
        {
            return Refuse("limit must be a positive number");
        }

        long lastSeq = context.Ring.LastSeq;
        long cursor = since is { } start ? (long)Math.Max(start, -1d) : lastSeq;
        bool cursorReset = cursor > lastSeq;
        if (cursorReset)
            cursor = lastSeq;
        int limit = (int)Math.Min(limitGiven ?? DefaultLimit, MaximumLimit);
        double deadline = context.Clock.Now + ToolArguments.WaitSeconds(arguments, McpToolHost.MaximumEventWaitSeconds);

        var collected = new JsonArray();
        long missed = 0;
        int filtered = 0;
        JsonObject? woke = null;

        return new PollingRun(() =>
        {
            if (woke is null && collected.Count < limit)
            {
                RecordRead read = context.Ring.Read(cursor);
                missed += read.Missed;
                foreach (AgentRecord record in read.Records)
                {
                    cursor = record.Seq;
                    bool wanted = kinds is null || kinds.Contains(record.Kind);
                    if (!wanted && until is null)
                    {
                        filtered++;
                        continue;
                    }
                    JsonObject parsed = ToolRecords.Parse(record);
                    int clause = until?.FindIndex(condition => condition.Matches(parsed)) ?? -1;
                    if (wanted)
                        collected.Add(parsed);
                    else
                        filtered++;
                    if (clause >= 0)
                    {
                        woke = new JsonObject
                        {
                            ["clause"] = clause,
                            ["record"] = wanted ? parsed.DeepClone() : parsed,
                        };
                        break;
                    }
                    if (collected.Count >= limit)
                        break;
                }
            }

            bool answered = woke is not null
                || (until is null && collected.Count > 0)
                || collected.Count >= limit
                || context.Clock.Now >= deadline;
            if (!answered)
                return null;
            return ToolResults.Of(new JsonObject
            {
                ["nextSeq"] = cursor,
                ["more"] = context.Ring.LastSeq > cursor,
                ["woke"] = woke,
                ["filtered"] = filtered,
                ["missed"] = missed,
                ["cursorReset"] = cursorReset,
                ["records"] = collected,
            });
        });
    }

    private static FinishedRun Refuse(string error) => new(ToolResults.Error(error));
}
