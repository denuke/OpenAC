using System.Text.Json;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;
using AcDream.Plugins.Agent.Tests.Fakes;
using AcDream.Plugins.Agent.Verbs;

namespace AcDream.Plugins.Agent.Tests.Verbs;

public sealed class AttackVerbsTests
{
    private const uint Drudge = 0x70000001u;

    [Fact]
    public void AnAttackIsReleasedAtItsPowerAndEndsOnTheServersAnswer()
    {
        var (host, verbs, correlator, _, ring) = Build(PluginCombatMode.Melee);
        FakeCombat combat = host.FakeAutomation.FakeCombat;

        Assert.Equal("handled", verbs.Handle(Line($"attack 0x{Drudge:X8} 0.4 high")).Outcome);
        combat.Snapshot = combat.Snapshot with { BuildInProgress = true, PowerBarLevel = 0.2f };
        correlator.Tick(inWorld: true);
        Assert.DoesNotContain("release", combat.Calls);

        combat.Snapshot = combat.Snapshot with { PowerBarLevel = 0.45f };
        correlator.Tick(inWorld: true);
        correlator.Tick(inWorld: true);
        Assert.Single(combat.Calls, call => call == "release");

        combat.Snapshot = combat.Snapshot with { BuildInProgress = false, CompletionRevision = 1 };
        correlator.Tick(inWorld: true);

        Assert.Equal($"attack:{Drudge:X8}:High:0.40", combat.Calls[0]);
        JsonElement sent = Records(ring, RecordKinds.AttackSent).Single();
        Assert.Equal("high", sent.GetProperty("height").GetString());
        JsonElement outcome = Records(ring, RecordKinds.AttackOutcome).Single();
        Assert.Equal("ended", outcome.GetProperty("outcome").GetString());
        Assert.Equal("confirmed", outcome.GetProperty("class").GetString());
    }

    [Fact]
    public void AServerRefusalIsRefusedWithTheError()
    {
        var (host, verbs, correlator, _, ring) = Build(PluginCombatMode.Missile);
        FakeCombat combat = host.FakeAutomation.FakeCombat;

        verbs.Handle(Line($"attack 0x{Drudge:X8}"));
        combat.Snapshot = combat.Snapshot with { CompletionRevision = 1, CompletionWeenieError = 0x0402u };
        correlator.Tick(inWorld: true);

        JsonElement outcome = Records(ring, RecordKinds.AttackOutcome).Single();
        Assert.Equal("refused", outcome.GetProperty("outcome").GetString());
        Assert.Equal(0x0402, outcome.GetProperty("weenieError").GetInt32());
    }

    [Fact]
    public void WithRepeatAttacksTheOutcomeWaitsForTheSwingsToStopAndCountsThem()
    {
        var (host, verbs, correlator, _, ring) = Build(PluginCombatMode.Melee);
        FakeCombat combat = host.FakeAutomation.FakeCombat;
        verbs.Handle(Line($"attack 0x{Drudge:X8}"));

        combat.Snapshot = combat.Snapshot with { CompletionRevision = 1, RepeatAttackInProgress = true };
        correlator.Tick(inWorld: true);
        combat.Snapshot = combat.Snapshot with { CompletionRevision = 3 };
        correlator.Tick(inWorld: true);
        Assert.Empty(Records(ring, RecordKinds.AttackOutcome));

        combat.Snapshot = combat.Snapshot with
        {
            CompletionRevision = 4,
            RepeatAttackInProgress = false,
            CompletionWeenieError = 0x0036u,
        };
        correlator.Tick(inWorld: true);

        JsonElement outcome = Records(ring, RecordKinds.AttackOutcome).Single();
        Assert.Equal("ended", outcome.GetProperty("outcome").GetString());
        Assert.Equal(4, outcome.GetProperty("swings").GetInt32());
        Assert.Contains("0x0036", outcome.GetProperty("reason").GetString());
    }

    [Fact]
    public void OutsideAMeleeOrMissileStanceTheAttackIsRefused()
    {
        var (host, verbs, _, _, ring) = Build(PluginCombatMode.Peace);

        Assert.Equal("refused", verbs.Handle(Line($"attack 0x{Drudge:X8}")).Outcome);
        Assert.Empty(host.FakeAutomation.FakeCombat.Calls);
        Assert.Equal("wrong-mode", Records(ring, RecordKinds.AttackRefused).Single().GetProperty("word").GetString());
    }

    [Fact]
    public void WithNothingSelectedAndNoTargetGivenTheAttackIsRefused()
    {
        var (host, verbs, _, _, ring) = Build(PluginCombatMode.Melee);

        Assert.Equal("refused", verbs.Handle(Line("attack")).Outcome);
        Assert.Empty(host.FakeAutomation.FakeCombat.Calls);
        Assert.Equal("no-target", Records(ring, RecordKinds.AttackRefused).Single().GetProperty("word").GetString());
    }

    [Fact]
    public void TheSelectionIsAttackedWhenNoTargetIsGiven()
    {
        var (host, verbs, _, _, _) = Build(PluginCombatMode.Melee);
        host.FakeSelection.Select(Drudge);

        verbs.Handle(Line("attack"));

        Assert.Equal($"attack:{Drudge:X8}:Medium:0.50", host.FakeAutomation.FakeCombat.Calls.Single());
    }

    [Theory]
    [InlineData("attack 0x70000001 1.5")]
    [InlineData("attack 0x70000001 sideways")]
    public void AnUnreadableArgumentIsRefused(string text)
    {
        var (host, verbs, _, _, _) = Build(PluginCombatMode.Melee);

        Assert.Equal("refused", verbs.Handle(Line(text)).Outcome);
        Assert.Empty(host.FakeAutomation.FakeCombat.Calls);
    }

    [Fact]
    public void AStartTheClientDeclinesIsRefusedWithItsWord()
    {
        var (host, verbs, _, _, ring) = Build(PluginCombatMode.Melee);
        host.FakeAutomation.FakeCombat.NextStatus = PluginCombatCommandStatus.InvalidTarget;

        Assert.Equal("refused", verbs.Handle(Line($"attack 0x{Drudge:X8}")).Outcome);
        Assert.Equal("invalid-target", Records(ring, RecordKinds.AttackRefused).Single().GetProperty("word").GetString());
    }

    private static CommandLine Line(string text) => CommandLine.Parse(60, text, "mcp");

    private static IEnumerable<JsonElement> Records(RecordRing ring, string kind) =>
        ring.Read(-1, new HashSet<string> { kind }).Records
            .Select(record => JsonDocument.Parse(record.Json).RootElement);

    private static (FakePluginHost Host, AttackVerbs Verbs, OutcomeCorrelator Correlator, AgentClock Clock, RecordRing Ring) Build(PluginCombatMode mode)
    {
        var host = new FakePluginHost();
        host.FakeAutomation.FakeCombat.Snapshot = new PluginCombatSnapshot(
            SelectedObjectId: 0u,
            Mode: mode,
            AttackHeight: PluginAttackHeight.Medium,
            DesiredPower: 0.5f,
            PowerBarLevel: 0f,
            BuildInProgress: false,
            RequestInProgress: false,
            ServerResponsePending: false,
            RepeatAttackInProgress: false);
        var clock = new AgentClock();
        var ring = new RecordRing(64);
        var publisher = new Publisher(clock, ring);
        var correlator = new OutcomeCorrelator(publisher, clock);
        return (host, new AttackVerbs(host, publisher, correlator), correlator, clock, ring);
    }
}
