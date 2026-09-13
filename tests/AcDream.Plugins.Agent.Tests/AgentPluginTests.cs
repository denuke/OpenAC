using AcDream.Plugins.Agent.Tests.Fakes;

namespace AcDream.Plugins.Agent.Tests;

public sealed class AgentPluginTests
{
    [Fact]
    public void EnableRegistersTheAgentVerbAndTheTickAndListens()
    {
        var (host, plugin, listeners) = Build();

        plugin.Enable();

        Assert.Contains("agent", host.FakeCommands.Verbs);
        Assert.Equal(1, host.FakeEvents.TickSubscriberCount);
        Assert.Equal(AgentService.DefaultPort, Assert.Single(listeners).Port);
    }

    [Fact]
    public void DisableReleasesTheVerbAndTheTickAndStopsListening()
    {
        var (host, plugin, listeners) = Build();
        plugin.Enable();

        plugin.Disable();

        Assert.Empty(host.FakeCommands.Verbs);
        Assert.Equal(0, host.FakeEvents.TickSubscriberCount);
        Assert.True(Assert.Single(listeners).Disposed);
    }

    [Fact]
    public void EnablingAgainListensAgain()
    {
        var (host, plugin, listeners) = Build();
        plugin.Enable();
        plugin.Disable();

        plugin.Enable();

        Assert.Equal(2, listeners.Count);
        Assert.False(listeners[1].Disposed);
        Assert.Contains("agent", host.FakeCommands.Verbs);
    }

    [Fact]
    public void StatusSaysWhereClientsConnect()
    {
        var (host, plugin, _) = Build();
        plugin.Enable();
        host.FakeAutomation.FakeChat.SystemMessages.Clear();

        Assert.True(host.FakeCommands.Run("/agent status"));

        Assert.Equal(
            "Agent: listening on http://127.0.0.1:31337/mcp with 0 client sessions. Not recording.",
            host.FakeAutomation.FakeChat.SystemMessages[0]);
    }

    [Fact]
    public void BareVerbAndHelpListTheCommands()
    {
        var (host, plugin, _) = Build();
        plugin.Enable();
        host.FakeAutomation.FakeChat.SystemMessages.Clear();

        host.FakeCommands.Run("/agent");
        int bare = host.FakeAutomation.FakeChat.SystemMessages.Count;
        host.FakeCommands.Run("/agent help");

        Assert.True(bare > 0);
        Assert.Equal(bare * 2, host.FakeAutomation.FakeChat.SystemMessages.Count);
        Assert.Contains(
            host.FakeAutomation.FakeChat.SystemMessages,
            line => line.StartsWith("/agent status", StringComparison.Ordinal));
        Assert.DoesNotContain(
            host.FakeAutomation.FakeChat.SystemMessages,
            line => line.StartsWith("/agent listen", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownSubcommandPointsAtHelp()
    {
        var (host, plugin, _) = Build();
        plugin.Enable();
        host.FakeAutomation.FakeChat.SystemMessages.Clear();

        host.FakeCommands.Run("/agent dance");

        Assert.Equal(
            "Unknown /agent command 'dance'. Use /agent help.",
            Assert.Single(host.FakeAutomation.FakeChat.SystemMessages));
    }

    private static (FakePluginHost Host, AgentPlugin Plugin, List<FakeListener> Listeners) Build()
    {
        var host = new FakePluginHost();
        var listeners = new List<FakeListener>();
        var plugin = new AgentPlugin(pluginHost => new AgentService(pluginHost, _ => new RecordingSink(), (endpoint, port) =>
        {
            var listener = new FakeListener(endpoint, port);
            listeners.Add(listener);
            return listener;
        }));
        plugin.Initialize(host);
        return (host, plugin, listeners);
    }
}
