using System.Globalization;
using System.Text.Json.Nodes;
using AcDream.Plugins.Agent.Contract;

namespace AcDream.Plugins.Agent.Mcp;

internal static class ToolResults
{
    internal static JsonObject Of(JsonObject structured, bool isError = false) => new()
    {
        ["content"] = new JsonArray(new JsonObject
        {
            ["type"] = "text",
            ["text"] = structured.ToJsonString(AgentJson.Options),
        }),
        ["structuredContent"] = structured,
        ["isError"] = isError,
    };

    internal static JsonObject Error(string message) =>
        Of(new JsonObject { ["error"] = message }, isError: true);
}

internal static class ToolArguments
{
    internal static string? Text(JsonObject arguments, string name) =>
        JsonRpc.Text(arguments[name]);

    /// <summary>
    /// An optional number, given as a JSON number or as a numeric string.
    /// False when the argument is present but is not a number.
    /// </summary>
    internal static bool TryNumber(JsonObject arguments, string name, out double? value)
    {
        value = null;
        JsonNode? node = arguments[name];
        if (node is null)
            return true;
        if (Clause.TryNumber(node, out double number))
        {
            value = number;
            return true;
        }
        if (JsonRpc.Text(node) is { } text
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && double.IsFinite(parsed))
        {
            value = parsed;
            return true;
        }
        return false;
    }

    internal static double? Number(JsonObject arguments, string name) =>
        TryNumber(arguments, name, out double? value) ? value : null;

    /// <summary>A whole number given as a JSON number or as a string of digits.</summary>
    internal static long? Whole(JsonObject arguments, string name) =>
        Number(arguments, name) is { } number && number == Math.Floor(number) && Math.Abs(number) < 1e15
            ? (long)number
            : null;

    internal static double WaitSeconds(JsonObject arguments) =>
        WaitSeconds(arguments, McpToolHost.MaximumWaitSeconds);

    internal static double WaitSeconds(JsonObject arguments, double maximum) =>
        Math.Clamp(Number(arguments, "waitSeconds") ?? 0d, 0d, maximum);
}

internal static class ToolDefinitions
{
    internal static JsonObject Make(
        string name,
        string title,
        string description,
        JsonObject properties,
        IReadOnlyList<string> required,
        bool readOnly) => new()
    {
        ["name"] = name,
        ["title"] = title,
        ["description"] = description,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(required.Select(field => (JsonNode?)field).ToArray()),
        },
        ["annotations"] = new JsonObject
        {
            ["title"] = title,
            ["readOnlyHint"] = readOnly,
            ["destructiveHint"] = !readOnly,
            ["idempotentHint"] = readOnly,
            ["openWorldHint"] = true,
        },
    };

    internal static JsonObject WaitProperty() => WaitProperty(McpToolHost.MaximumWaitSeconds);

    internal static JsonObject WaitProperty(double maximum) => new()
    {
        ["type"] = "number",
        ["description"] = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Seconds to wait for an answer, up to {maximum:0}. Omit to answer at once."),
    };
}
