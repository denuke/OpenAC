using System.Text.Json;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;
using AcDream.Plugins.Agent.Tests.Fakes;
using AcDream.Plugins.Agent.Verbs;

namespace AcDream.Plugins.Agent.Tests.Verbs;

public sealed class ObjectVerbsTests
{
    private const uint Potion = 0x50000123u;
    private const uint Shopkeeper = 0x70000010u;

    [Fact]
    public void ACarriedItemIsUsedAsAnItemAndCompletesOnItsAnswer()
    {
        var (host, verbs, correlator, _, ring) = Build();

        Assert.Equal("handled", verbs.Handle(Line($"use 0x{Potion:X8}")).Outcome);
        host.FakeAutomation.FakeItems.LastCompletion = new PluginItemUseCompletion(1, Potion, 0u, 0u);
        correlator.Tick(inWorld: true);

        Assert.Equal([$"use:{Potion:X8}"], host.FakeAutomation.FakeItems.Calls);
        Assert.Empty(host.FakeAutomation.FakeObjects.Uses);
        Assert.Equal("item", Records(ring, RecordKinds.ObjectAction).Single().GetProperty("path").GetString());
        Assert.Equal("completed", Records(ring, RecordKinds.ObjectOutcome).Single().GetProperty("outcome").GetString());
    }

    [Fact]
    public void AVendorIsUsedAsAWorldObjectAndCompletesWhenItOpens()
    {
        var (host, verbs, correlator, _, ring) = Build();

        verbs.Handle(Line($"use 0x{Shopkeeper:X8}"));
        correlator.Tick(inWorld: true);
        Assert.Empty(Records(ring, RecordKinds.ObjectOutcome));
        host.FakeAutomation.FakeItems.ActiveVendorObjectId = Shopkeeper;
        correlator.Tick(inWorld: true);

        Assert.Equal([Shopkeeper], host.FakeAutomation.FakeObjects.Uses);
        Assert.Empty(host.FakeAutomation.FakeItems.Calls);
        JsonElement outcome = Records(ring, RecordKinds.ObjectOutcome).Single();
        Assert.Equal("completed", outcome.GetProperty("outcome").GetString());
        Assert.Equal("the vendor opened", outcome.GetProperty("reason").GetString());
    }

    [Fact]
    public void AServerRefusalResolvesRefusedWithTheError()
    {
        var (host, verbs, correlator, _, ring) = Build();

        verbs.Handle(Line($"use 0x{Shopkeeper:X8}"));
        host.FakeAutomation.FakeItems.LastCompletion = new PluginItemUseCompletion(1, Shopkeeper, 0u, 0x0512u);
        correlator.Tick(inWorld: true);

        JsonElement outcome = Records(ring, RecordKinds.ObjectOutcome).Single();
        Assert.Equal("refused", outcome.GetProperty("outcome").GetString());
        Assert.Equal(0x0512, outcome.GetProperty("weenieError").GetInt32());
    }

    [Fact]
    public void AnAnswerAboutAnotherObjectDoesNotResolveIt()
    {
        var (host, verbs, correlator, clock, ring) = Build();

        verbs.Handle(Line($"use 0x{Shopkeeper:X8}"));
        host.FakeAutomation.FakeItems.LastCompletion = new PluginItemUseCompletion(1, Potion, 0u, 0u);
        correlator.Tick(inWorld: true);
        clock.Advance(ObjectVerbs.WindowSeconds);
        correlator.Tick(inWorld: true);

        Assert.Equal("unconfirmed", Records(ring, RecordKinds.ObjectOutcome).Single().GetProperty("outcome").GetString());
    }

    [Fact]
    public void UseOnATargetAppliesTheCarriedItem()
    {
        var (host, verbs, _, _, ring) = Build();

        verbs.Handle(Line($"use 0x{Potion:X8} on 0x70000001"));

        Assert.Equal([$"apply:{Potion:X8}:70000001"], host.FakeAutomation.FakeItems.Calls);
        Assert.Equal("0x70000001", Records(ring, RecordKinds.ObjectAction).Single().GetProperty("target").GetString());
    }

    [Fact]
    public void AnUncarriedItemCannotBeUsedOnSomething()
    {
        var (host, verbs, _, _, _) = Build();

        Assert.Equal("refused", verbs.Handle(Line($"use 0x{Shopkeeper:X8} on 0x70000001")).Outcome);
        Assert.Empty(host.FakeAutomation.FakeItems.Calls);
    }

    [Fact]
    public void AnObjectTheClientDoesNotHoldIsRefused()
    {
        var (host, verbs, _, _, _) = Build();

        Assert.Equal("refused", verbs.Handle(Line("use 0x70000099")).Outcome);
        Assert.Empty(host.FakeAutomation.FakeObjects.Uses);
    }

    [Fact]
    public void AUseTheClientDeclinesIsRefusedWithItsStatus()
    {
        var (host, verbs, _, _, ring) = Build();
        host.FakeAutomation.FakeObjects.UseStatus = PluginItemCommandStatus.Busy;

        Assert.Equal("refused", verbs.Handle(Line($"use 0x{Shopkeeper:X8}")).Outcome);
        Assert.Equal("busy", Records(ring, RecordKinds.ObjectRefused).Single().GetProperty("word").GetString());
    }

    [Fact]
    public void OpenCompletesWhenTheContainerIsOpen()
    {
        var (host, verbs, correlator, _, ring) = Build();

        verbs.Handle(Line("open 0x70000020"));
        host.FakeAutomation.FakeLoot.CurrentContainerId = 0x70000020u;
        correlator.Tick(inWorld: true);

        Assert.Equal(["open:70000020"], host.FakeAutomation.FakeLoot.Calls);
        Assert.Equal("completed", Records(ring, RecordKinds.ObjectOutcome).Single().GetProperty("outcome").GetString());
    }

    [Fact]
    public void OpeningAnObjectThatDoesNotOpenIsRefusedBeforeAnythingIsSent()
    {
        var (host, verbs, _, _, ring) = Build();

        Assert.Equal("refused", verbs.Handle(Line($"open 0x{Shopkeeper:X8}")).Outcome);

        Assert.Empty(host.FakeAutomation.FakeLoot.Calls);
        Assert.Contains("does not open", Records(ring, RecordKinds.ObjectRefused).Single().GetProperty("reason").GetString());
    }

    [Fact]
    public void CloseIsRefusedWithAReason()
    {
        var (_, verbs, _, _, ring) = Build();

        Assert.Equal("refused", verbs.Handle(Line("close")).Outcome);
        Assert.Single(Records(ring, RecordKinds.ObjectRefused));
    }

    private static CommandLine Line(string text) => CommandLine.Parse(20, text, "mcp");

    private static IEnumerable<JsonElement> Records(RecordRing ring, string kind) =>
        ring.Read(-1, new HashSet<string> { kind }).Records
            .Select(record => JsonDocument.Parse(record.Json).RootElement);

    private static (FakePluginHost Host, ObjectVerbs Verbs, OutcomeCorrelator Correlator, AgentClock Clock, RecordRing Ring) Build()
    {
        var host = new FakePluginHost();
        host.FakeAutomation.FakeItems.Owned.Add(new PluginInventoryItem(
            Potion, 1u, "Health Potion", 128u, 0x50000001u, 0u, 0u, 0u, 0u, 0u, 0u,
            1, 0, 0, 0u, 0, 0, 0u, false, 0d, 0, 0, 0, 0d, 0, 0, 0));
        host.FakeAutomation.FakeObjects.Add(new PluginWorldObject(
            Shopkeeper, 1u, "Shopkeeper", PluginObjectClass.Vendor, 16u, 0u, 0u));
        var clock = new AgentClock();
        var ring = new RecordRing(64);
        var publisher = new Publisher(clock, ring);
        var correlator = new OutcomeCorrelator(publisher, clock);
        return (host, new ObjectVerbs(host, publisher, correlator), correlator, clock, ring);
    }
}
