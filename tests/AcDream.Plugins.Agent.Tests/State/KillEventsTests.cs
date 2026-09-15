using System.Text.Json;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.State;
using AcDream.Plugins.Agent.Tests.Fakes;

namespace AcDream.Plugins.Agent.Tests.State;

public sealed class KillEventsTests
{
    [Fact]
    public void EachKillAfterTheRebaseIsPublishedOnceNamingTheCreature()
    {
        var host = new FakePluginHost();
        var ring = new RecordRing(16);
        var publisher = new Publisher(new AgentClock(), ring);
        var events = new KillEvents(host);
        host.FakeAutomation.FakeCombat.Kills.Add(new PluginKill(1, 0x80000001u, "Drudge Robber"));
        events.Rebase();
        host.FakeAutomation.FakeCombat.Kills.Add(new PluginKill(2, 0x80000002u, "Drudge Servant"));

        events.Poll(publisher);
        events.Poll(publisher);

        using JsonDocument record = JsonDocument.Parse(Assert.Single(ring.Read(-1).Records).Json);
        Assert.Equal("kill", record.RootElement.GetProperty("kind").GetString());
        Assert.Equal("Drudge Servant", record.RootElement.GetProperty("victim").GetString());
        Assert.Equal("0x80000002", record.RootElement.GetProperty("victimGuid").GetString());
    }
}
