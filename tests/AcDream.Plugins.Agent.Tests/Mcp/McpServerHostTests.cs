using AcDream.Plugins.Agent.Mcp;

namespace AcDream.Plugins.Agent.Tests.Mcp;

public sealed class McpServerHostTests
{
    [Theory]
    [InlineData("127.0.0.1:31337", true)]
    [InlineData("localhost:31337", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("[::1]:31337", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("rebound.example:31337", false)]
    [InlineData("127.0.0.1.nip.io:31337", false)]
    [InlineData("localhost.evil:31337", false)]
    [InlineData("127.0.0.1:8080", false)]
    [InlineData("127.0.0.1:31337:1", false)]
    [InlineData("[::1", false)]
    [InlineData("[::1]x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyLoopbackHostNamesAreServed(string? host, bool allowed)
    {
        Assert.Equal(allowed, McpServer.HostAllowed(host, 31337));
    }
}
