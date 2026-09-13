namespace AcDream.Plugins.Agent.Mcp;

/// <summary>The parts of an HTTP request the endpoint reads.</summary>
internal sealed record McpHttpRequest(
    string Method,
    string Path,
    string? SessionId,
    string? Accept,
    string? Origin,
    string Body);

/// <summary>What to send back.</summary>
internal sealed record McpHttpResponse(
    int Status,
    string? ContentType,
    string? Body,
    IReadOnlyList<KeyValuePair<string, string>> Headers);
