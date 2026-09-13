namespace AcDream.Plugins.Agent.Mcp;

/// <summary>The parts of an HTTP request the endpoint reads.</summary>
internal sealed record McpHttpRequest(
    string Method,
    string Path,
    string? SessionId,
    string? Accept,
    string? Origin,
    string Body);

/// <summary>
/// What to send back. When <see cref="StreamSession"/> is set, the transport
/// keeps the response open and writes that session's pushes to it.
/// </summary>
internal sealed record McpHttpResponse(
    int Status,
    string? ContentType,
    string? Body,
    IReadOnlyList<KeyValuePair<string, string>> Headers)
{
    internal McpSession? StreamSession { get; init; }
}
