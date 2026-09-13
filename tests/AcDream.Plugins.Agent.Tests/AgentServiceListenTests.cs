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
    public void ListeningStartsOnTheDefaultPortAndSaysHowToConnect()
    {
        var (host, service, listeners) = Build();

        service.StartListening();

        Assert.Equal(AgentService.DefaultPort, Assert.Single(listeners).Port);
        Assert.Equal(DefaultUrl, service.ListenerUrl);
        Assert.True(service.IsActive);
        string connect = "Agent: connect an AI client with: claude mcp add --transport http openac " + DefaultUrl;
        Assert.Contains(connect, host.FakeAutomation.FakeChat.SystemMessages);
        Assert.Contains(connect, host.FakeLog.Lines);
    }

    [Fact]
    public void ASavedPortIsUsed()
    {
        var (host, service, listeners) = Build();
        host.FakeStorage.Files[AgentService.PortSettingKey] = "40000";

        service.StartListening();

        Assert.Equal(40000, Assert.Single(listeners).Port);
    }

    [Fact]
    public void ASavedPortThatIsNotAPortFallsBackToTheDefault()
    {
        var (host, service, listeners) = Build();
        host.FakeStorage.Files[AgentService.PortSettingKey] = "banana";

        service.StartListening();

        Assert.Equal(AgentService.DefaultPort, Assert.Single(listeners).Port);
    }

    [Fact]
    public void APortInUseFallsBackToAFreePortAndSaysSo()
    {
        var host = new FakePluginHost();
        using var service = new AgentService(host, _ => new RecordingSink(), (endpoint, port) =>
            port == AgentService.DefaultPort
                ? throw new SocketException((int)SocketError.AddressAlreadyInUse)
                : new FakeListener(endpoint, port == 0 ? 50123 : port));

        service.StartListening();

        Assert.Equal("http://127.0.0.1:50123/mcp", service.ListenerUrl);
        Assert.Contains(
            host.FakeAutomation.FakeChat.SystemMessages,
            line => line.StartsWith("Agent: port 31337 could not be used", StringComparison.Ordinal));
    }

    [Fact]
    public void WhenNoPortOpensNothingListensAndItIsReported()
    {
        var host = new FakePluginHost();
        using var service = new AgentService(host, _ => new RecordingSink(),
            (_, _) => throw new SocketException((int)SocketError.AccessDenied));

        service.StartListening();

        Assert.Null(service.ListenerUrl);
        Assert.False(service.IsActive);
        Assert.StartsWith(
            "Agent: AI clients cannot connect",
            Assert.Single(host.FakeAutomation.FakeChat.SystemMessages));
    }

    [Fact]
    public void StartingAgainWhileListeningDoesNothing()
    {
        var (host, service, listeners) = Build();
        service.StartListening();
        host.FakeAutomation.FakeChat.SystemMessages.Clear();

        service.StartListening();

        Assert.Single(listeners);
        Assert.Empty(host.FakeAutomation.FakeChat.SystemMessages);
    }

    [Theory]
    [InlineData("listen")]
    [InlineData("stop")]
    public void ListenAndStopAreNotCommands(string command)
    {
        var (host, service, listeners) = Build();

        Run(service, command);

        Assert.Empty(listeners);
        Assert.Equal(
            $"Unknown /agent command '{command}'. Use /agent help.",
            Assert.Single(host.FakeAutomation.FakeChat.SystemMessages));
    }

    [Fact]
    public void StatusWhileListeningShowsTheAddressAndHowToConnect()
    {
        var (host, service, _) = Build();
        service.StartListening();
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

        service.StartListening();

        Assert.NotNull(service.Ring.Latest(RecordKinds.Session));
    }

    [Fact]
    public void ListeningFirstSkipsEventsFromBeforeIt()
    {
        var (host, service, _) = Build();
        host.FakeAutomation.FakeChat.Receive("old news");

        service.StartListening();
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

        service.StartListening();
        service.OnTick(0.016);

        Assert.Contains(
            service.Ring.Read(-1, new HashSet<string> { RecordKinds.Chat }).Records,
            record => record.Json.Contains("arrived before listening", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisposingTheServiceClosesTheListenerAndEndsEverySession()
    {
        var (_, service, listeners) = Build();
        service.StartListening();
        McpEndpoint endpoint = listeners[0].Endpoint;
        McpHttpResponse initialized = await endpoint.HandleAsync(
            new McpHttpRequest("POST", "/mcp", null, "application/json", null,
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}"""),
            CancellationToken.None);
        string session = initialized.Headers.Single(header => header.Key == McpEndpoint.SessionHeader).Value;

        service.Dispose();

        Assert.True(listeners[0].Disposed);
        Assert.Null(service.ListenerUrl);
        McpHttpResponse after = await endpoint.HandleAsync(
            new McpHttpRequest("POST", "/mcp", session, "application/json", null,
                """{"jsonrpc":"2.0","id":2,"method":"ping"}"""),
            CancellationToken.None);
        Assert.Equal(404, after.Status);
    }

    [Fact]
    public void ADisposedServiceDoesNotListen()
    {
        var (_, service, listeners) = Build();
        service.Dispose();

        service.StartListening();

        Assert.Empty(listeners);
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
}
