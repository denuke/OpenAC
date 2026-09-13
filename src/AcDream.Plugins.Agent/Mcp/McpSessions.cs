using System.Collections.Concurrent;

namespace AcDream.Plugins.Agent.Mcp;

/// <summary>One connected MCP client.</summary>
internal sealed class McpSession
{
    internal McpSession(string id, string protocolVersion)
    {
        Id = id;
        ProtocolVersion = protocolVersion;
    }

    internal string Id { get; }

    internal string ProtocolVersion { get; }
}

/// <summary>The MCP clients connected right now, safe to use from any thread.</summary>
internal sealed class McpSessions
{
    private readonly ConcurrentDictionary<string, McpSession> _sessions = new(StringComparer.Ordinal);

    internal int Count => _sessions.Count;

    internal McpSession Create(string protocolVersion)
    {
        var session = new McpSession(Guid.NewGuid().ToString("N"), protocolVersion);
        _sessions[session.Id] = session;
        return session;
    }

    internal bool TryGet(string id, out McpSession session) =>
        _sessions.TryGetValue(id, out session!);

    internal bool Remove(string id) => _sessions.TryRemove(id, out _);

    internal IReadOnlyList<McpSession> Snapshot() => _sessions.Values.ToArray();
}
