using System.Text.Json;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;
using AcDream.Plugins.Agent.Tests.Fakes;
using AcDream.Plugins.Agent.Verbs;

namespace AcDream.Plugins.Agent.Tests.Verbs;

public sealed class MotorVerbsTests
{
    [Fact]
    public void TurnToAHeadingResolvesWhenTheBodyFacesIt()
    {
        var (host, verbs, correlator, clock, ring) = Build();

        Assert.Equal("handled", verbs.Handle(Line("turn to 90")).Outcome);
        correlator.Tick(inWorld: true);
        Assert.Empty(Kinds(ring, RecordKinds.GoalResolved));

        host.FakeAutomation.FakeNavigation.Snapshot = Body(heading: 87f);
        correlator.Tick(inWorld: true);

        Assert.Equal([90f], host.FakeAutomation.FakeNavigation.Headings);
        Assert.Equal("completed", Kinds(ring, RecordKinds.GoalResolved).Single().GetProperty("outcome").GetString());
    }

    [Fact]
    public void ATurnThatNeverArrivesIsUnconfirmed()
    {
        var (_, verbs, correlator, clock, ring) = Build();

        verbs.Handle(Line("turn to 180"));
        clock.Advance(MotorVerbs.TurnWindowSeconds);
        correlator.Tick(inWorld: true);

        Assert.Equal("unconfirmed", Kinds(ring, RecordKinds.GoalResolved).Single().GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData("turn 90")]
    [InlineData("turn to north")]
    [InlineData("turn to")]
    public void AMalformedTurnIsRefused(string text)
    {
        var (host, verbs, _, _, ring) = Build();

        Assert.Equal("refused", verbs.Handle(Line(text)).Outcome);
        Assert.Empty(host.FakeAutomation.FakeNavigation.Headings);
        Assert.Single(Kinds(ring, RecordKinds.GoalRefused));
    }

    [Fact]
    public void FaceTurnsTowardAnObjectsBearing()
    {
        var (host, verbs, _, _, _) = Build();
        host.FakeAutomation.FakeObjects.Add(new PluginWorldObject(
            0x70000001u, 1u, "Drudge", PluginObjectClass.Monster, 16u, 0u, 0u)
        {
            HasPosition = true,
            IsLandscape = true,
            Position = new PluginNavigationPosition(0u, 0.01, 0d, 0d, 0f, true),
        });

        verbs.Handle(Line("face 0x70000001"));

        Assert.Equal(90f, Assert.Single(host.FakeAutomation.FakeNavigation.Headings), 3);
    }

    [Fact]
    public void JumpSetsAndClearsTheIntentAndResolvesWhenAirborne()
    {
        var (host, verbs, correlator, _, ring) = Build();

        verbs.Handle(Line("jump"));
        host.FakeAutomation.FakeNavigation.Snapshot = Body(heading: 0f) with { IsAirborne = true };
        correlator.Tick(inWorld: true);

        Assert.True(Assert.Single(host.FakeAutomation.FakeNavigation.Intents).Jump);
        Assert.Equal(1, host.FakeAutomation.FakeNavigation.Clears);
        Assert.Equal("completed", Kinds(ring, RecordKinds.GoalResolved).Single().GetProperty("outcome").GetString());
    }

    [Fact]
    public void StanceEntersTheModeAndResolvesWhenTheModeIsReported()
    {
        var (host, verbs, correlator, _, ring) = Build();

        verbs.Handle(Line("stance magic"));
        host.FakeAutomation.FakeCombat.Snapshot = host.FakeAutomation.FakeCombat.Snapshot with
        {
            Mode = PluginCombatMode.Magic,
        };
        correlator.Tick(inWorld: true);

        Assert.Equal(["mode:Magic"], host.FakeAutomation.FakeCombat.Calls);
        Assert.Equal("completed", Kinds(ring, RecordKinds.GoalResolved).Single().GetProperty("outcome").GetString());
    }

    [Fact]
    public void AStanceTheClientRefusesIsRefusedWithItsStatus()
    {
        var (host, verbs, _, _, ring) = Build();
        host.FakeAutomation.FakeCombat.NextStatus = PluginCombatCommandStatus.Busy;

        Assert.Equal("refused", verbs.Handle(Line("stance melee")).Outcome);
        Assert.Contains("busy", Kinds(ring, RecordKinds.GoalRefused).Single().GetProperty("reason").GetString());
    }

    [Fact]
    public void CancelStopsMovementAttacksAndPendingGoals()
    {
        var (host, verbs, correlator, _, ring) = Build();
        verbs.Handle(Line("turn to 180"));

        verbs.Handle(CommandLine.Parse(10, "cancel", "mcp"));

        JsonElement[] resolved = Kinds(ring, RecordKinds.GoalResolved).ToArray();
        Assert.Equal(["cancelled", "completed"], resolved.Select(r => r.GetProperty("outcome").GetString()));
        Assert.Equal(1, host.FakeAutomation.FakeNavigation.Clears);
        Assert.Contains("abort", host.FakeAutomation.FakeCombat.Calls);
        Assert.Equal(0, correlator.PendingCount);
    }

    private static PluginNavigationSnapshot Body(float heading) =>
        new(true, false, 0x50000001u, new PluginNavigationPosition(0u, 0d, 0d, 0d, heading, true), false, false);

    private static CommandLine Line(string text) => CommandLine.Parse(9, text, "mcp");

    private static IEnumerable<JsonElement> Kinds(RecordRing ring, string kind) =>
        ring.Read(-1, new HashSet<string> { kind }).Records
            .Select(record => JsonDocument.Parse(record.Json).RootElement);

    private static (FakePluginHost Host, MotorVerbs Verbs, OutcomeCorrelator Correlator, AgentClock Clock, RecordRing Ring) Build()
    {
        var host = new FakePluginHost();
        host.FakeAutomation.FakeNavigation.Snapshot = Body(heading: 0f);
        var clock = new AgentClock();
        var ring = new RecordRing(64);
        var publisher = new Publisher(clock, ring);
        var correlator = new OutcomeCorrelator(publisher, clock);
        return (host, new MotorVerbs(host, publisher, correlator), correlator, clock, ring);
    }
}
