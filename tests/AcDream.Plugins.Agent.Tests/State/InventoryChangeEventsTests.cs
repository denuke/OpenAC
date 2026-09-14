using System.Text.Json;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.State;
using AcDream.Plugins.Agent.Tests.Fakes;
using AcDream.Plugins.Agent.Tests.Verbs;

namespace AcDream.Plugins.Agent.Tests.State;

public sealed class InventoryChangeEventsTests
{
    [Fact]
    public void LootArrivingAndComponentsSpentArePublishedByNameWithWhatIsHeldNow()
    {
        var (host, clock, events, publisher, ring) = Build();
        host.FakeAutomation.FakeItems.Owned.Add(Item(1u, "Prismatic Taper", 50));
        events.Rebase();

        host.FakeAutomation.FakeItems.Owned[0] = Item(1u, "Prismatic Taper", 47);
        host.FakeAutomation.FakeItems.Owned.Add(Item(2u, "Pyreal", 120));
        clock.Advance(InventoryChangeEvents.SampleSeconds);
        events.Poll(publisher);

        JsonElement[] records = Records(ring);
        Assert.Equal(["item-gained", "item-spent"], records.Select(record => record.GetProperty("kind").GetString()));
        Assert.Equal("Pyreal", records[0].GetProperty("name").GetString());
        Assert.Equal(120, records[0].GetProperty("count").GetInt64());
        Assert.Equal(120, records[0].GetProperty("total").GetInt64());
        Assert.Equal("Prismatic Taper", records[1].GetProperty("name").GetString());
        Assert.Equal(3, records[1].GetProperty("count").GetInt64());
        Assert.Equal(47, records[1].GetProperty("total").GetInt64());
    }

    [Fact]
    public void SplittingOrMovingAStackChangesNothing()
    {
        var (host, clock, events, publisher, ring) = Build();
        host.FakeAutomation.FakeItems.Owned.Add(Item(1u, "Lead Scarab", 10));
        events.Rebase();

        host.FakeAutomation.FakeItems.Owned[0] = Item(1u, "Lead Scarab", 4);
        host.FakeAutomation.FakeItems.Owned.Add(Item(2u, "Lead Scarab", 6) with { ContainerObjectId = 0x40000002u });
        clock.Advance(InventoryChangeEvents.SampleSeconds);
        events.Poll(publisher);

        Assert.Empty(ring.Read(-1).Records);
    }

    [Fact]
    public void TheInventoryTheClientHearsOfOnEnteringTheWorldIsNotCountedAsGained()
    {
        var (host, clock, events, publisher, ring) = Build();
        host.FakeAutomation.FakeCharacter.IsInWorld = false;
        events.Rebase();

        host.FakeAutomation.FakeCharacter.IsInWorld = true;
        clock.Advance(InventoryChangeEvents.SampleSeconds);
        events.Poll(publisher);
        host.FakeAutomation.FakeItems.Owned.Add(Item(1u, "Pyreal", 500));
        clock.Advance(TrendTracker.SettleSeconds);
        events.Poll(publisher);
        clock.Advance(InventoryChangeEvents.SampleSeconds);
        events.Poll(publisher);
        Assert.Empty(ring.Read(-1).Records);

        host.FakeAutomation.FakeItems.Owned.Add(Item(2u, "Pyreal", 5));
        clock.Advance(InventoryChangeEvents.SampleSeconds);
        events.Poll(publisher);

        JsonElement gained = Assert.Single(Records(ring));
        Assert.Equal(5, gained.GetProperty("count").GetInt64());
        Assert.Equal(505, gained.GetProperty("total").GetInt64());
    }

    internal static PluginInventoryItem Item(uint id, string name, int stack) =>
        LootVerbsTests.Item(id, name, 0x50000001u) with { StackSize = stack };

    private static JsonElement[] Records(RecordRing ring) =>
        ring.Read(-1).Records.Select(record => JsonDocument.Parse(record.Json).RootElement.Clone()).ToArray();

    private static (FakePluginHost Host, AgentClock Clock, InventoryChangeEvents Events, Publisher Publisher, RecordRing Ring) Build()
    {
        var host = new FakePluginHost();
        var clock = new AgentClock();
        var ring = new RecordRing(16);
        return (host, clock, new InventoryChangeEvents(host, clock), new Publisher(clock, ring), ring);
    }
}
