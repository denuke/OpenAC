using AcDream.Plugins.Agent.Tests.Fakes;

namespace AcDream.Plugins.Agent.Tests;

public sealed class AgentPluginTests
{
    [Fact]
    public void EnableRegistersTheAgentVerbAndTheTick()
    {
        var host = new FakePluginHost();
        var plugin = new AgentPlugin();

        plugin.Initialize(host);
        plugin.Enable();

        Assert.Contains("agent", host.FakeCommands.Verbs);
        Assert.Equal(1, host.FakeEvents.TickSubscriberCount);
    }

    [Fact]
    public void DisableReleasesTheVerbAndTheTick()
    {
        var host = new FakePluginHost();
        var plugin = new AgentPlugin();
        plugin.Initialize(host);
        plugin.Enable();

        plugin.Disable();

        Assert.Empty(host.FakeCommands.Verbs);
        Assert.Equal(0, host.FakeEvents.TickSubscriberCount);
    }

    [Fact]
    public void StatusSaysTheAgentIsNotListeningUntilTurnedOn()
    {
        var host = new FakePluginHost();
        var plugin = new AgentPlugin();
        plugin.Initialize(host);
        plugin.Enable();

        Assert.True(host.FakeCommands.Run("/agent status"));

        Assert.Equal(
            "Agent: not listening. Not recording.",
            Assert.Single(host.FakeAutomation.FakeChat.SystemMessages));
    }

    [Fact]
    public void BareVerbAndHelpListTheCommands()
    {
        var host = new FakePluginHost();
        var plugin = new AgentPlugin();
        plugin.Initialize(host);
        plugin.Enable();

        host.FakeCommands.Run("/agent");
        int bare = host.FakeAutomation.FakeChat.SystemMessages.Count;
        host.FakeCommands.Run("/agent help");

        Assert.True(bare > 0);
        Assert.Equal(bare * 2, host.FakeAutomation.FakeChat.SystemMessages.Count);
        Assert.Contains(
            host.FakeAutomation.FakeChat.SystemMessages,
            line => line.StartsWith("/agent status", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownSubcommandPointsAtHelp()
    {
        var host = new FakePluginHost();
        var plugin = new AgentPlugin();
        plugin.Initialize(host);
        plugin.Enable();

        host.FakeCommands.Run("/agent dance");

        Assert.Equal(
            "Unknown /agent command 'dance'. Use /agent help.",
            Assert.Single(host.FakeAutomation.FakeChat.SystemMessages));
    }
}
