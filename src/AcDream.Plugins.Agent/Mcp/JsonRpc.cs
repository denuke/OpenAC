using System.Text.Json;
using System.Text.Json.Nodes;

namespace AcDream.Plugins.Agent.Mcp;

/// <summary>JSON-RPC 2.0 envelopes, one message per HTTP request.</summary>
internal static class JsonRpc
{
    internal const int ParseError = -32700;
    internal const int InvalidRequest = -32600;
    internal const int MethodNotFound = -32601;
    internal const int InvalidParams = -32602;
    internal const int InternalError = -32603;

    /// <summary>One parsed message. <see cref="Id"/> is kept exactly as sent.</summary>
    internal readonly record struct Message(
        JsonNode? Id,
        string? Method,
        JsonObject? Params,
        bool HasId)
    {
        internal bool IsRequest => Method is not null && HasId;
    }

    internal static bool TryParse(string body, out Message message, out JsonObject? error)
    {
        message = default;
        error = null;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            error = Error(null, ParseError, "the body is not valid JSON");
            return false;
        }

        if (node is JsonArray)
        {
            error = Error(null, InvalidRequest, "batches are not supported; send one message per request");
            return false;
        }
        if (node is not JsonObject envelope || Text(envelope["jsonrpc"]) != "2.0")
        {
            error = Error(null, InvalidRequest, "expected a JSON-RPC 2.0 object");
            return false;
        }

        bool hasId = envelope.ContainsKey("id");
        JsonNode? id = envelope["id"];
        string? method = Text(envelope["method"]);
        bool isResponse = method is null
            && (envelope.ContainsKey("result") || envelope.ContainsKey("error"));
        if (method is null && !isResponse)
        {
            error = Error(id, InvalidRequest, "a message needs a method, or a result or error");
            return false;
        }
        if (envelope["params"] is { } parameters && parameters is not JsonObject)
        {
            error = Error(id, InvalidParams, "params must be an object");
            return false;
        }

        message = new Message(id, method, envelope["params"] as JsonObject, hasId);
        return true;
    }

    internal static JsonObject Result(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result,
    };

    internal static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
    };

    internal static JsonObject Notification(string method, JsonObject parameters) => new()
    {
        ["jsonrpc"] = "2.0",
        ["method"] = method,
        ["params"] = parameters,
    };

    internal static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}
