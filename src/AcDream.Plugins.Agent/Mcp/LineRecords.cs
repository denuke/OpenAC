using System.Globalization;
using System.Text.Json.Nodes;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;

namespace AcDream.Plugins.Agent.Mcp;

/// <summary>The record kinds that say an action was accepted or has ended.</summary>
internal static class ActionKinds
{
    internal static readonly IReadOnlySet<string> Accepted = new HashSet<string>(StringComparer.Ordinal)
    {
        RecordKinds.TargetSent,
        RecordKinds.GoalAccepted,
        RecordKinds.CastSent,
        RecordKinds.ObjectAction,
        RecordKinds.InventoryAction,
        RecordKinds.AttackSent,
    };

    internal static readonly IReadOnlySet<string> Terminal = OutcomeTable.Kinds
        .Where(kind => kind != RecordKinds.CommandOutcome)
        .ToHashSet(StringComparer.Ordinal);
}

internal static class ToolRecords
{
    /// <summary>A record as tools return it: every field except the schema, which all records share.</summary>
    internal static JsonObject Parse(AgentRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        JsonObject parsed = JsonNode.Parse(record.Json)!.AsObject();
        parsed.Remove("schema");
        return parsed;
    }
}

/// <summary>
/// Every record carrying one command line's handle, gathered from the stream,
/// and what they say about how far the line has got.
/// </summary>
internal sealed class LineRecords
{
    internal const string Pending = "pending";
    internal const string Unknown = "unknown";
    internal const string Done = "done";
    internal const string Refused = "refused";
    internal const string SentAsChat = "sent-as-chat";

    private readonly long _id;
    private readonly string _beforeComma;
    private readonly string _beforeBrace;

    internal LineRecords(long id, long cursor = -1)
    {
        _id = id;
        Cursor = cursor;
        string number = id.ToString(CultureInfo.InvariantCulture);
        _beforeComma = $"\"id\":{number},";
        _beforeBrace = $"\"id\":{number}}}";
    }

    internal List<JsonObject> Records { get; } = [];

    internal long Cursor { get; private set; }

    internal void Scan(RecordRing ring)
    {
        RecordRead read = ring.Read(Cursor);
        foreach (AgentRecord record in read.Records)
        {
            if (!record.Json.Contains(_beforeComma, StringComparison.Ordinal)
                && !record.Json.Contains(_beforeBrace, StringComparison.Ordinal))
            {
                continue;
            }
            JsonObject parsed = ToolRecords.Parse(record);
            if (Clause.TryNumber(parsed["id"], out double id) && id == _id)
                Records.Add(parsed);
        }
        Cursor = read.NextSeq;
    }

    internal JsonObject? CommandOutcome => First(kind => kind == RecordKinds.CommandOutcome);

    internal JsonObject? Accepted => First(ActionKinds.Accepted.Contains);

    internal JsonObject? Terminal => First(ActionKinds.Terminal.Contains);

    internal JsonObject? Refusal => First(kind => kind.EndsWith("-refused", StringComparison.Ordinal));

    internal string Status
    {
        get
        {
            if (Terminal is not null)
                return Done;
            if (Refusal is not null)
                return Refused;
            if (CommandOutcome is not { } command)
                return Records.Count == 0 ? Unknown : Pending;
            return JsonRpc.Text(command["outcome"]) switch
            {
                "refused" or "failed" => Refused,
                "chat" => SentAsChat,
                _ => Accepted is not null ? Pending : Done,
            };
        }
    }

    internal bool Settled => Status is not (Pending or Unknown);

    /// <summary>How the line ended, if it has, with every record it produced.</summary>
    internal JsonObject Result()
    {
        string status = Status;
        JsonObject? terminal = Terminal;
        JsonObject? answer = terminal ?? (status is Pending or Unknown ? null : CommandOutcome);
        string? outcomeClass = JsonRpc.Text(answer?["class"]);
        var records = new JsonArray();
        foreach (JsonObject record in Records)
            records.Add(record.DeepClone());
        return new JsonObject
        {
            ["handle"] = _id.ToString(CultureInfo.InvariantCulture),
            ["status"] = status,
            ["outcome"] = JsonRpc.Text(answer?["outcome"]),
            ["class"] = outcomeClass,
            ["confirmed"] = outcomeClass is null ? null : (JsonNode)(outcomeClass == "confirmed"),
            ["reason"] = Reason(status, terminal),
            ["records"] = records,
        };
    }

    /// <summary>A read's answer: the record it produced, or the record saying why it was refused.</summary>
    internal JsonObject ReadResult()
    {
        string status = Status;
        JsonObject? answer = First(kind => kind != RecordKinds.CommandOutcome);
        return new JsonObject
        {
            ["status"] = status,
            ["reason"] = status == Done ? null : Reason(status, null),
            ["record"] = answer?.DeepClone(),
        };
    }

    private string? Reason(string status, JsonObject? terminal) => status switch
    {
        Unknown => "no record carries this handle; it was never issued, or is older than the records kept",
        Pending => null,
        _ => JsonRpc.Text(terminal?["reason"])
            ?? (status == Refused ? JsonRpc.Text(CommandOutcome?["reason"]) : null),
    };

    private JsonObject? First(Func<string, bool> kind) =>
        Records.FirstOrDefault(record => JsonRpc.Text(record["kind"]) is { } name && kind(name));
}
