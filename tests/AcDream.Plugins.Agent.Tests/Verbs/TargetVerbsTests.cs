using System.Text.Json;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;
using AcDream.Plugins.Agent.Tests.Fakes;
using AcDream.Plugins.Agent.Verbs;

namespace AcDream.Plugins.Agent.Tests.Verbs;

public sealed class TargetVerbsTests
{
    [Fact]
    public void TargetingAKnownObjectSelectsItAndResolvesCompleted()
    {
        var (host, verbs, correlator, ring) = Build();
        host.FakeAutomation.FakeObjects.Add(new PluginWorldObject(
            0x70000001u, 1u, "Drudge Skulker", PluginObjectClass.Monster, 16u, 0u, 0u));

        Assert.Equal("handled", verbs.Handle(Line("target 0x70000001")).Outcome);
        correlator.Tick(inWorld: true);

        Assert.Equal(0x70000001u, host.FakeSelection.SelectedObjectId);
        Assert.Equal("Drudge Skulker", Latest(ring, RecordKinds.TargetSent).GetProperty("name").GetString());
        Assert.Equal("completed", Latest(ring, RecordKinds.TargetOutcome).GetProperty("outcome").GetString());
    }

    [Fact]
    public void AnIdTheClientDoesNotHoldIsRefusedAndNothingIsSelected()
    {
        var (host, verbs, _, ring) = Build();

        Assert.Equal("refused", verbs.Handle(Line("target 0x70000009")).Outcome);

        Assert.Null(host.FakeSelection.SelectedObjectId);
        Assert.Equal("target", Latest(ring, RecordKinds.TargetRefused).GetProperty("verb").GetString());
    }

    [Fact]
    public void TargetNearestPicksTheClosestOfThatKind()
    {
        var (host, verbs, _, _) = Build();
        host.FakeAutomation.FakeObjects.Add(Placed(0x70000001u, "Far Drudge", PluginObjectClass.Monster, 0.05));
        host.FakeAutomation.FakeObjects.Add(Placed(0x70000002u, "Near Vendor", PluginObjectClass.Vendor, 0.001));
        host.FakeAutomation.FakeObjects.Add(Placed(0x70000003u, "Near Drudge", PluginObjectClass.Monster, 0.01));

        verbs.Handle(Line("target nearest monster"));

        Assert.Equal(0x70000003u, host.FakeSelection.SelectedObjectId);
    }

    [Fact]
    public void TargetNearestWithNothingOfThatKindIsRefused()
    {
        var (host, verbs, _, _) = Build();
        host.FakeAutomation.FakeObjects.Add(Placed(0x70000002u, "Near Vendor", PluginObjectClass.Vendor, 0.001));

        Assert.Equal("refused", verbs.Handle(Line("target nearest monster")).Outcome);
    }

    [Fact]
    public void UntargetClearsTheSelection()
    {
        var (host, verbs, correlator, ring) = Build();
        host.FakeSelection.Select(0x70000001u);

        verbs.Handle(Line("untarget"));
        correlator.Tick(inWorld: true);

        Assert.Null(host.FakeSelection.SelectedObjectId);
        Assert.Equal("completed", Latest(ring, RecordKinds.TargetOutcome).GetProperty("outcome").GetString());
    }

    private static PluginWorldObject Placed(uint id, string name, PluginObjectClass objectClass, double northSouth) =>
        new(id, 1u, name, objectClass, 16u, 0u, 0u)
        {
            HasPosition = true,
            IsLandscape = true,
            Position = new PluginNavigationPosition(0u, 0d, northSouth, 0d, 0f, true),
        };

    private static CommandLine Line(string text) => CommandLine.Parse(9, text, "mcp");

    private static JsonElement Latest(RecordRing ring, string kind) =>
        JsonDocument.Parse(ring.Read(-1, new HashSet<string> { kind }).Records.Last().Json).RootElement;

    private static (FakePluginHost Host, TargetVerbs Verbs, OutcomeCorrelator Correlator, RecordRing Ring) Build()
    {
        var host = new FakePluginHost();
        host.FakeAutomation.FakeNavigation.Snapshot = new PluginNavigationSnapshot(
            true, false, 0x50000001u, new PluginNavigationPosition(0u, 0d, 0d, 0d, 0f, true), false, false);
        var clock = new AgentClock();
        var ring = new RecordRing(32);
        var publisher = new Publisher(clock, ring);
        var correlator = new OutcomeCorrelator(publisher, clock);
        return (host, new TargetVerbs(host, publisher, correlator), correlator, ring);
    }
}
