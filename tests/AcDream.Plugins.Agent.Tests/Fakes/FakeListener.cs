using AcDream.Plugins.Agent.Mcp;

namespace AcDream.Plugins.Agent.Tests.Fakes;

/// <summary>A listener that records the port it was asked for and whether it was closed, without opening a socket.</summary>
internal sealed class FakeListener(McpEndpoint endpoint, int port) : IMcpListener
{
    internal McpEndpoint Endpoint { get; } = endpoint;

    internal int Port { get; } = port;

    internal bool Disposed { get; private set; }

    public string Url => $"http://127.0.0.1:{Port}/mcp";

    public void Dispose() => Disposed = true;
}
