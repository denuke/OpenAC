using System.Net.Sockets;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Mcp;
using AcDream.Plugins.Agent.Tests.Fakes;

namespace AcDream.Plugins.Agent.Tests;

public sealed class AgentServiceListenTests
{
    private const string DefaultUrl = "http://127.0.0.1:31337/mcp";

    [Fact]
    public void ListenStartsOnTheDefaultPortAndSaysHowToConnect()
    {
        var (host, service, listeners) = Build();

        Run(service, "listen");

        Assert.Equal(AgentService.DefaultPort, Assert.Single(listeners).Port);
        Assert.Equal(DefaultUrl, service.ListenerUrl);
        Assert.True(service.IsActive);
        Assert.Contains(
            "Agent: connect an AI client with: claude mcp add --transport http openac " + DefaultUrl,
            host.FakeAutomation.FakeChat.SystemMessages);
    }

    [Fact]
    public void AGivenPortIsUsedAndRememberedForNextTime()
    {
        var (host, service, listeners) = Build();

        Run(service, "listen 40000");
        Run(service, "stop");
        Run(service, "listen");

        Assert.Equal([40000, 40000], listeners.Select(listener => listener.Port));
        Assert.Equal("40000", host.FakeStorage.Files[AgentService.PortSettingKey]);
    }

    [Fact]
    public void ASavedPortThatIsNotAPortFallsBackToTheDefault()
    {
        var (host, service, listeners) = Build();
        host.FakeStorage.Files[AgentService.PortSettingKey] = "banana";

        Run(service, "listen");

        Assert.Equal(AgentService.DefaultPort, Assert.Single(listeners).Port);
    }

    [Theory]
    [InlineData("listen 0")]
    [InlineData("listen 65536")]
    [InlineData("listen http")]
    public void ABadPortIsRefusedWithUsage(string command)
    {
        var (host, service, listeners) = Build();

        Run(service, command);

        Assert.Empty(listeners);
        Assert.StartsWith("Usage: /agent listen", Assert.Single(host.FakeAutomation.FakeChat.SystemMessages));
    }

    [Fact]
    public void APortInUseIsReportedAndNothingListens()
    {
        var host = new FakePluginHost();
        using var service = new AgentService(host, _ => new RecordingSink(),
            (_, _) => throw new SocketException((int)SocketError.AddressAlreadyInUse));

        Run(service, "listen 31337");

        Assert.Null(service.ListenerUrl);
        Assert.False(service.IsActive);
        Assert.StartsWith(
            "Agent: could not listen on port 31337",
            Assert.Single(host.FakeAutomation.FakeChat.SystemMessages));
        Assert.False(host.FakeStorage.Files.ContainsKey(AgentService.PortSettingKey));
    }

    [Fact]
    public void ListeningAgainSaysItAlreadyIs()
    {
        var (host, service, listeners) = Build();
        Run(service, "listen");
        host.FakeAutomation.FakeChat.SystemMessages.Clear();

        Run(service, "listen 40000");

        Assert.Single(listeners);
        Assert.Equal(
            "Agent: already listening on " + DefaultUrl + ".",
            Assert.Single(host.FakeAutomation.FakeChat.SystemMessages));
    }

    [Fact]
    public async Task StopClosesTheListenerAndEndsEverySession()
    {
        var (host, service, listeners) = Build();
        Run(service, "listen");
        McpEndpoint endpoint = listeners[0].Endpoint;
        McpHttpResponse initialized = await endpoint.HandleAsync(
            new McpHttpRequest("POST", "/mcp", null, "application/json", null,
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}"""),
            CancellationToken.None);
        string session = initialized.Headers.Single(header => header.Key == McpEndpoint.SessionHeader).Value;

        Run(service, "stop");

        Assert.True(listeners[0].Disposed);
        Assert.Null(service.ListenerUrl);
        Assert.False(service.IsActive);
        Assert.Equal("Agent: stopped listening.", host.FakeAutomation.FakeChat.SystemMessages.Last());
        McpHttpResponse after = await endpoint.HandleAsync(
            new McpHttpRequest("POST", "/mcp", session, "application/json", null,
                """{"jsonrpc":"2.0","id":2,"method":"ping"}"""),
            CancellationToken.None);
        Assert.Equal(404, after.Status);
    }

    [Fact]
    public void StopWhenNotListeningSaysSo()
    {
        var (host, service, _) = Build();

        Run(service, "stop");

        Assert.Equal("Agent: not listening.", Assert.Single(host.FakeAutomation.FakeChat.SystemMessages));
    }

    [Fact]
    public void StatusWhileListeningShowsTheAddressAndHowToConnect()
    {
        var (host, service, _) = Build();
        Run(service, "listen");
        host.FakeAutomation.FakeChat.SystemMessages.Clear();

        Run(service, "status");

        Assert.Equal(
            [
                "Agent: listening on " + DefaultUrl + " with 0 client sessions. Not recording.",
                "Agent: connect an AI client with: claude mcp add --transport http openac " + DefaultUrl,
            ],
            host.FakeAutomation.FakeChat.SystemMessages);
    }

    [Fact]
    public void ListeningPublishesTheStateForClients()
    {
        var (_, service, _) = Build();

        Run(service, "listen");

        Assert.NotNull(service.Ring.Latest(RecordKinds.Session));
    }

    [Fact]
    public void ListeningFirstSkipsEventsFromBeforeIt()
    {
        var (host, service, _) = Build();
        host.FakeAutomation.FakeChat.Receive("old news");

        Run(service, "listen");
        service.OnTick(0.016);

        Assert.Empty(service.Ring.Read(-1, new HashSet<string> { RecordKinds.Chat }).Records);
    }

    [Fact]
    public void ListeningWhileRecordingKeepsEventsNotYetPublished()
    {
        var (host, service, _) = Build();
        Run(service, "record " + Path.Combine(Path.GetTempPath(), "agent-listen.jsonl"));
        service.OnTick(0.016);
        host.FakeAutomation.FakeChat.Receive("arrived before listening");

        Run(service, "listen");
        service.OnTick(0.016);

        Assert.Contains(
            service.Ring.Read(-1, new HashSet<string> { RecordKinds.Chat }).Records,
            record => record.Json.Contains("arrived before listening", StringComparison.Ordinal));
    }

    [Fact]
    public void DisposingTheServiceStopsListening()
    {
        var (_, service, listeners) = Build();
        Run(service, "listen");

        service.Dispose();

        Assert.True(Assert.Single(listeners).Disposed);
    }

    private static void Run(AgentService service, string arguments) =>
        service.HandleCommand(new PluginCommand("agent", arguments, "/agent " + arguments));

    private static (FakePluginHost Host, AgentService Service, List<FakeListener> Listeners) Build()
    {
        var host = new FakePluginHost();
        var listeners = new List<FakeListener>();
        var service = new AgentService(host, _ => new RecordingSink(), (endpoint, port) =>
        {
            var listener = new FakeListener(endpoint, port);
            listeners.Add(listener);
            return listener;
        });
        return (host, service, listeners);
    }

    private sealed class FakeListener(McpEndpoint endpoint, int port) : IMcpListener
    {
        internal McpEndpoint Endpoint { get; } = endpoint;

        internal int Port { get; } = port;

        internal bool Disposed { get; private set; }

        public string Url => $"http://127.0.0.1:{Port}/mcp";

        public void Dispose() => Disposed = true;
    }
}
