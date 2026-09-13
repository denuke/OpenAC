using System.Text.Json;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;
using AcDream.Plugins.Agent.Tests.Fakes;
using AcDream.Plugins.Agent.Verbs;

namespace AcDream.Plugins.Agent.Tests.Verbs;

public sealed class LootVerbsTests
{
    private const uint Corpse = 0x70000020u;
    private const uint Sword = 0x70000021u;

    [Fact]
    public void ListingNeedsAnOpenContainer()
    {
        var (_, verbs, _, _, ring) = Build();

        Assert.Equal("refused", verbs.Handle(Line("loot list")).Outcome);
        Assert.Single(Records(ring, RecordKinds.InventoryRefused));
    }

    [Fact]
    public void ListingNamesTheContainerAndItsItems()
    {
        var (host, verbs, _, _, ring) = Build();
        host.FakeAutomation.FakeLoot.CurrentContainerId = Corpse;
        host.FakeAutomation.FakeLoot.Contents.Add(Item(Sword, "Iron Sword", Corpse));

        verbs.Handle(Line("loot list"));

        JsonElement contents = Records(ring, RecordKinds.ContainerContents).Single();
        Assert.Equal("0x70000020", contents.GetProperty("container").GetString());
        Assert.Equal("Iron Sword", contents.GetProperty("items")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void CorpsesAreListedWithinTheRange()
    {
        var (host, verbs, _, _, ring) = Build();
        host.FakeAutomation.FakeLoot.Corpses.Add(new PluginLootContainer(Corpse, 1u, "Corpse of Drudge", 4f, false, false, false));
        host.FakeAutomation.FakeLoot.Corpses.Add(new PluginLootContainer(0x70000022u, 1u, "Corpse of Far Drudge", 50f, false, false, false));

        verbs.Handle(Line("loot corpses 20"));

        JsonElement corpses = Records(ring, RecordKinds.Corpses).Single().GetProperty("corpses");
        Assert.Equal(1, corpses.GetArrayLength());
        Assert.Equal("Corpse of Drudge", corpses[0].GetProperty("name").GetString());
    }

    [Fact]
    public void TakingAnItemPicksItUpAndCompletesOnItsAnswer()
    {
        var (host, verbs, correlator, _, ring) = Build();
        host.FakeAutomation.FakeLoot.CurrentContainerId = Corpse;

        Assert.Equal("handled", verbs.Handle(Line($"loot 0x{Sword:X8}")).Outcome);
        host.FakeAutomation.FakeItems.LastInventoryCompletion =
            new PluginInventoryCompletion(1, PluginInventoryCommandKind.Pickup, Sword, 0u);
        correlator.Tick(inWorld: true);

        Assert.Equal([$"pickup:{Sword:X8}"], host.FakeAutomation.FakeLoot.Calls);
        Assert.Equal("completed", Records(ring, RecordKinds.InventoryOutcome).Single().GetProperty("outcome").GetString());
    }

    [Fact]
    public void TakingWithNoOpenContainerIsRefusedBeforeSending()
    {
        var (host, verbs, _, _, _) = Build();

        Assert.Equal("refused", verbs.Handle(Line($"loot 0x{Sword:X8}")).Outcome);
        Assert.Empty(host.FakeAutomation.FakeLoot.Calls);
    }

    [Fact]
    public void AnInventoryRefusalFromTheServerResolvesRefused()
    {
        var (host, verbs, correlator, _, ring) = Build();
        host.FakeAutomation.FakeLoot.CurrentContainerId = Corpse;

        verbs.Handle(Line($"loot 0x{Sword:X8}"));
        host.FakeAutomation.FakeItems.LastInventoryCompletion =
            new PluginInventoryCompletion(1, PluginInventoryCommandKind.Pickup, Sword, 0x001Du);
        correlator.Tick(inWorld: true);

        Assert.Equal("refused", Records(ring, RecordKinds.InventoryOutcome).Single().GetProperty("outcome").GetString());
    }

    internal static PluginInventoryItem Item(uint id, string name, uint container) =>
        new(id, 1u, name, 1u, container, 0u, 0u, 0u, 0u, 0u, 0u,
            1, 0, 0, 0u, 0, 0, 0u, false, 0d, 0, 0, 0, 0d, 0, 0, 0);

    private static CommandLine Line(string text) => CommandLine.Parse(30, text, "mcp");

    private static IEnumerable<JsonElement> Records(RecordRing ring, string kind) =>
        ring.Read(-1, new HashSet<string> { kind }).Records
            .Select(record => JsonDocument.Parse(record.Json).RootElement);

    private static (FakePluginHost Host, LootVerbs Verbs, OutcomeCorrelator Correlator, AgentClock Clock, RecordRing Ring) Build()
    {
        var host = new FakePluginHost();
        var clock = new AgentClock();
        var ring = new RecordRing(64);
        var publisher = new Publisher(clock, ring);
        var correlator = new OutcomeCorrelator(publisher, clock);
        return (host, new LootVerbs(host, publisher, correlator), correlator, clock, ring);
    }
}
