using System.Text.Json.Nodes;

namespace AcDream.Plugins.Agent.Mcp;

/// <summary>
/// A condition on one record: its kind, optionally exact field values, and
/// optionally a numeric comparison, such as
/// <c>{"kind":"vital-changed","match":{"vital":"health"},"compare":["value","&lt;",100]}</c>.
/// Field names may be dotted paths into nested objects.
/// </summary>
internal sealed class Clause
{
    private static readonly HashSet<string> Operators =
        new(StringComparer.Ordinal) { "<", "<=", ">", ">=", "==", "!=" };

    private Clause(
        string kind,
        IReadOnlyList<(string Path, JsonNode? Value)> match,
        (string Path, string Operator, double Value)? compare)
    {
        Kind = kind;
        MatchFields = match;
        Compare = compare;
    }

    internal string Kind { get; }

    internal IReadOnlyList<(string Path, JsonNode? Value)> MatchFields { get; }

    internal (string Path, string Operator, double Value)? Compare { get; }

    internal static bool TryParse(JsonNode? node, out Clause? clause, out string? error)
    {
        clause = null;
        error = null;
        if (node is not JsonObject body || string.IsNullOrWhiteSpace(JsonRpc.Text(body["kind"])))
        {
            error = "a clause is an object with a kind";
            return false;
        }

        var match = new List<(string, JsonNode?)>();
        if (body["match"] is { } given)
        {
            if (given is not JsonObject fields)
            {
                error = "match must be an object of field values";
                return false;
            }
            foreach (KeyValuePair<string, JsonNode?> field in fields)
                match.Add((field.Key, field.Value?.DeepClone()));
        }

        (string, string, double)? compare = null;
        if (body["compare"] is { } comparison)
        {
            if (comparison is not JsonArray parts
                || parts.Count != 3
                || JsonRpc.Text(parts[0]) is not { } path
                || JsonRpc.Text(parts[1]) is not { } op
                || !Operators.Contains(op)
                || !TryNumber(parts[2], out double value))
            {
                error = "compare must be [field, operator, number] using < <= > >= == or !=";
                return false;
            }
            compare = (path, op, value);
        }

        clause = new Clause(JsonRpc.Text(body["kind"])!, match, compare);
        return true;
    }

    internal bool Matches(JsonObject record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (JsonRpc.Text(record["kind"]) != Kind)
            return false;
        foreach ((string path, JsonNode? expected) in MatchFields)
        {
            if (!JsonNode.DeepEquals(Resolve(record, path), expected))
                return false;
        }
        if (Compare is not { } comparison)
            return true;
        if (!TryNumber(Resolve(record, comparison.Path), out double actual))
            return false;
        return comparison.Operator switch
        {
            "<" => actual < comparison.Value,
            "<=" => actual <= comparison.Value,
            ">" => actual > comparison.Value,
            ">=" => actual >= comparison.Value,
            "==" => actual == comparison.Value,
            _ => actual != comparison.Value,
        };
    }

    internal JsonObject ToJson()
    {
        var body = new JsonObject { ["kind"] = Kind };
        if (MatchFields.Count > 0)
        {
            var match = new JsonObject();
            foreach ((string path, JsonNode? value) in MatchFields)
                match[path] = value?.DeepClone();
            body["match"] = match;
        }
        if (Compare is { } comparison)
            body["compare"] = new JsonArray(comparison.Path, comparison.Operator, comparison.Value);
        return body;
    }

    internal static JsonNode? Resolve(JsonObject record, string path)
    {
        JsonNode? current = record;
        foreach (string part in path.Split('.'))
        {
            current = current is JsonObject parent ? parent[part] : null;
            if (current is null)
                return null;
        }
        return current;
    }

    internal static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0d;
        if (node is not JsonValue number)
            return false;
        if (number.TryGetValue(out double asDouble))
            value = asDouble;
        else if (number.TryGetValue(out long asLong))
            value = asLong;
        else if (number.TryGetValue(out int asInt))
            value = asInt;
        else if (number.TryGetValue(out uint asUint))
            value = asUint;
        else
            return false;
        return double.IsFinite(value);
    }
}
