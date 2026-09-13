using System.Text.Json.Nodes;

namespace AcDream.Plugins.Agent.Contract;

/// <summary>
/// Builds the shapes records use for values that may not be known. A consumer
/// can always tell a fact the client holds from one it does not hold, and never
/// receives a zero standing in for "not received".
/// </summary>
internal static class Facts
{
    internal static JsonObject Observed(JsonNode? value, string? because = null) =>
        Shape(Presence.Observed, value, because);

    internal static JsonObject Unknown(string because)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(because);
        return Shape(Presence.Unknown, null, because);
    }

    internal static JsonObject Unsupported(string because)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(because);
        return Shape(Presence.Unsupported, null, because);
    }

    /// <summary>
    /// A value this plugin computed rather than one the server stated.
    /// <paramref name="from"/> names what it was computed from.
    /// </summary>
    internal static JsonObject Derived(JsonNode? value, string from, string because)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(because);
        return new JsonObject
        {
            ["presence"] = WireName(Presence.Observed),
            ["value"] = value,
            ["from"] = from,
            ["because"] = because,
        };
    }

    /// <summary>An object id, with its name once the client knows it.</summary>
    internal static JsonObject Id(uint id, string? name = null) =>
        name is null
            ? new JsonObject { ["id"] = Hex(id), ["resolved"] = false }
            : new JsonObject
            {
                ["id"] = Hex(id),
                ["resolved"] = true,
                ["name"] = name,
            };

    internal static string Hex(uint id) => $"0x{id:X8}";

    internal static string WireName(Presence presence) => presence switch
    {
        Presence.Observed => "observed",
        Presence.Unsupported => "unsupported",
        _ => "unknown",
    };

    private static JsonObject Shape(
        Presence presence,
        JsonNode? value,
        string? because) => new()
    {
        ["presence"] = WireName(presence),
        ["value"] = value,
        ["because"] = because,
    };
}
