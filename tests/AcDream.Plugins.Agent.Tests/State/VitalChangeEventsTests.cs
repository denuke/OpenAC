using System.Text.Json;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.State;
using AcDream.Plugins.Agent.Tests.Fakes;

namespace AcDream.Plugins.Agent.Tests.State;

public sealed class VitalChangeEventsTests
{
    [Fact]
    public void AChangePublishesTheNewValueTheOldValueAndTheDifference()
    {
        var (host, events, publisher, ring) = Build();
        host.FakeAutomation.FakeCharacter.MaxHealth = 300u;
        host.FakeAutomation.FakeCharacter.CurrentHealth = 300u;
        events.Rebase();

        host.FakeAutomation.FakeCharacter.CurrentHealth = 250u;
        events.Poll(publisher);

        using JsonDocument record = JsonDocument.Parse(Assert.Single(ring.Read(-1).Records).Json);
        JsonElement root = record.RootElement;
        Assert.Equal("vital-changed", root.GetProperty("kind").GetString());
        Assert.Equal("health", root.GetProperty("vital").GetString());
        Assert.Equal(250, root.GetProperty("value").GetInt32());
        Assert.Equal(300, root.GetProperty("previous").GetInt32());
        Assert.Equal(-50, root.GetProperty("change").GetInt32());
    }

    [Fact]
    public void RebaseForgetsWhatChangedBeforeIt()
    {
        var (host, events, publisher, ring) = Build();
        host.FakeAutomation.FakeCharacter.MaxHealth = 300u;
        host.FakeAutomation.FakeCharacter.CurrentHealth = 100u;
        events.Poll(publisher);
        host.FakeAutomation.FakeCharacter.CurrentHealth = 200u;

        events.Rebase();
        events.Poll(publisher);

        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void AVitalWithNoMaximumPublishesNothing()
    {
        var (host, events, publisher, ring) = Build();
        events.Rebase();

        host.FakeAutomation.FakeCharacter.CurrentStamina = 50u;
        events.Poll(publisher);

        Assert.Equal(0, ring.Count);
    }

    private static (FakePluginHost Host, VitalChangeEvents Events, Publisher Publisher, RecordRing Ring) Build()
    {
        var host = new FakePluginHost();
        var ring = new RecordRing(16);
        return (host, new VitalChangeEvents(host), new Publisher(new AgentClock(), ring), ring);
    }
}
