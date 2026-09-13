using System.Text.Json.Nodes;
using AcDream.Plugins.Agent.Contract;

namespace AcDream.Plugins.Agent.Mcp;

/// <summary>
/// The Model Context Protocol over Streamable HTTP, independent of any socket.
/// POST carries one JSON-RPC message and DELETE ends a session. There is no
/// server-initiated stream, so GET is refused as the transport allows. Only
/// loopback origins are served.
/// </summary>
internal sealed class McpEndpoint
{
    internal const string SessionHeader = "Mcp-Session-Id";
    internal const string ServerName = "acdream-agent";
    internal const string ServerVersion = "0.1.0";

    internal static readonly IReadOnlyList<string> SupportedVersions = ["2025-06-18", "2025-03-26"];

    internal const string Instructions =
        "You are playing Asheron's Call through the OpenAC game client. "
        + "Use observe to see the character's state and nearby to see what is around it; "
        + "spells, skills, buffs, inventory, equipment, vendor, container and corpses list what "
        + "the character has and the names and ids to act on. "
        + "act runs one command line and returns a handle; call outcome with the handle to learn "
        + "what actually happened, because an accepted action is not a finished one. "
        + "events waits for records, so you never need to poll. "
        + "A value the client has not received is unknown, never zero.";

    private readonly McpSessions _sessions;
    private readonly IMcpTools _tools;

    internal McpEndpoint(McpSessions sessions, IMcpTools tools)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(tools);
        _sessions = sessions;
        _tools = tools;
    }

    internal async Task<McpHttpResponse> HandleAsync(
        McpHttpRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Path is not ("/mcp" or "/mcp/"))
            return Plain(404, "not found");
        if (!OriginAllowed(request.Origin))
            return Plain(403, "only loopback origins may connect");

        return request.Method.ToUpperInvariant() switch
        {
            "POST" => await PostAsync(request, cancellationToken).ConfigureAwait(false),
            "DELETE" => Delete(request),
            _ => Plain(405, "this endpoint takes POST, and DELETE to end a session; it offers no event stream") with
            {
                Headers = [new("Allow", "POST, DELETE")],
            },
        };
    }

    private async Task<McpHttpResponse> PostAsync(McpHttpRequest request, CancellationToken cancellationToken)
    {
        if (!JsonRpc.TryParse(request.Body, out JsonRpc.Message message, out JsonObject? parseError))
            return Json(400, parseError!);

        if (message.IsRequest && message.Method == "initialize")
            return Initialize(message);

        if (request.SessionId is null)
        {
            return Json(400, JsonRpc.Error(message.Id, JsonRpc.InvalidRequest,
                $"missing {SessionHeader} header; call initialize first"));
        }
        if (!_sessions.TryGet(request.SessionId, out McpSession session))
        {
            return Json(404, JsonRpc.Error(message.Id, JsonRpc.InvalidRequest,
                "unknown or ended session; call initialize again"));
        }
        if (!message.IsRequest)
            return new McpHttpResponse(202, null, null, []);

        JsonObject response = message.Method switch
        {
            "ping" => JsonRpc.Result(message.Id, new JsonObject()),
            "tools/list" => JsonRpc.Result(message.Id, new JsonObject { ["tools"] = _tools.List() }),
            "tools/call" => await CallAsync(message, session, cancellationToken).ConfigureAwait(false),
            _ => JsonRpc.Error(message.Id, JsonRpc.MethodNotFound,
                $"method '{message.Method}' is not supported"),
        };
        return Json(200, response);
    }

    private McpHttpResponse Initialize(JsonRpc.Message message)
    {
        string? requested = JsonRpc.Text(message.Params?["protocolVersion"]);
        string version = requested is not null && SupportedVersions.Contains(requested)
            ? requested
            : SupportedVersions[0];
        McpSession session = _sessions.Create(version);
        var result = new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = ServerName,
                ["version"] = ServerVersion,
            },
            ["instructions"] = Instructions,
        };
        return Json(200, JsonRpc.Result(message.Id, result)) with
        {
            Headers = [new(SessionHeader, session.Id)],
        };
    }

    private async Task<JsonObject> CallAsync(
        JsonRpc.Message message,
        McpSession session,
        CancellationToken cancellationToken)
    {
        string? name = JsonRpc.Text(message.Params?["name"]);
        if (name is null)
            return JsonRpc.Error(message.Id, JsonRpc.InvalidParams, "tools/call needs a tool name");
        if (!_tools.Has(name))
            return JsonRpc.Error(message.Id, JsonRpc.InvalidParams, $"there is no tool named '{name}'");
        JsonObject arguments = message.Params?["arguments"] is JsonObject given
            ? (JsonObject)given.DeepClone()
            : new JsonObject();
        try
        {
            JsonObject result = await _tools.CallAsync(name, arguments, session, cancellationToken)
                .ConfigureAwait(false);
            return JsonRpc.Result(message.Id, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return JsonRpc.Error(message.Id, JsonRpc.InternalError, $"the tool failed: {error.Message}");
        }
    }

    private McpHttpResponse Delete(McpHttpRequest request)
    {
        if (Session(request, out McpSession? session) is { } refusal)
            return refusal;
        _sessions.Remove(session!.Id);
        return new McpHttpResponse(200, null, null, []);
    }

    private McpHttpResponse? Session(McpHttpRequest request, out McpSession? session)
    {
        session = null;
        if (request.SessionId is null)
            return Plain(400, $"missing {SessionHeader} header");
        if (!_sessions.TryGet(request.SessionId, out McpSession found))
            return Plain(404, "unknown or ended session");
        session = found;
        return null;
    }

    internal static bool OriginAllowed(string? origin)
    {
        if (string.IsNullOrEmpty(origin))
            return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)
            && uri.Host is "localhost" or "127.0.0.1" or "[::1]" or "::1";
    }

    private static McpHttpResponse Json(int status, JsonObject body) =>
        new(status, "application/json", body.ToJsonString(AgentJson.Options), []);

    private static McpHttpResponse Plain(int status, string text) =>
        new(status, "text/plain; charset=utf-8", text, []);
}
