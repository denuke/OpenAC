using System.Text.Json;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;
using AcDream.Plugins.Agent.Tests.Fakes;
using AcDream.Plugins.Agent.Verbs;

namespace AcDream.Plugins.Agent.Tests.Verbs;

public sealed class CastVerbsTests
{
    private static readonly PluginSpellInfo Strength = FakeSpells.Spell(2u, "Strength Self VI");
    private static readonly PluginSpellInfo Bolt =
        FakeSpells.Spell(5u, "Flame Bolt VI", selfTargeted: false, beneficial: false);

    [Fact]
    public void ASelfSpellByNameIsSentAndAcceptedOnItsOwnCompletion()
    {
        var (host, verbs, correlator, _, ring) = Build();

        Assert.Equal("handled", verbs.Handle(Line("cast strength self vi")).Outcome);
        correlator.Tick(inWorld: true);
        Assert.Empty(Records(ring, RecordKinds.CastOutcome));

        host.FakeAutomation.FakeMagic.LastCompletion = new PluginCastCompletion(1, 2u, 0u, 0u);
        correlator.Tick(inWorld: true);

        Assert.Equal([(2u, (uint?)null)], host.FakeAutomation.FakeMagic.Requests);
        Assert.Equal("Strength Self VI", Records(ring, RecordKinds.CastSent).Single().GetProperty("name").GetString());
        JsonElement outcome = Records(ring, RecordKinds.CastOutcome).Single();
        Assert.Equal("accepted", outcome.GetProperty("outcome").GetString());
        Assert.Equal("confirmed", outcome.GetProperty("class").GetString());
    }

    [Fact]
    public void AServerRefusalResolvesRefusedWithTheError()
    {
        var (host, verbs, correlator, _, ring) = Build();

        verbs.Handle(Line("cast 2"));
        host.FakeAutomation.FakeMagic.LastCompletion = new PluginCastCompletion(1, 2u, 0u, 0x0402u);
        correlator.Tick(inWorld: true);

        JsonElement outcome = Records(ring, RecordKinds.CastOutcome).Single();
        Assert.Equal("refused", outcome.GetProperty("outcome").GetString());
        Assert.Equal(0x0402, outcome.GetProperty("weenieError").GetInt32());
    }

    [Fact]
    public void ADifferentSpellsAnswerIsUnattributable()
    {
        var (host, verbs, correlator, _, ring) = Build();

        verbs.Handle(Line("cast 2"));
        host.FakeAutomation.FakeMagic.LastCompletion = new PluginCastCompletion(1, 99u, 0u, 0u);
        correlator.Tick(inWorld: true);

        Assert.Equal("unattributable", Records(ring, RecordKinds.CastOutcome).Single().GetProperty("outcome").GetString());
    }

    [Fact]
    public void AnAnswerFromBeforeTheCastDoesNotCount()
    {
        var (host, verbs, correlator, clock, ring) = Build();
        host.FakeAutomation.FakeMagic.LastCompletion = new PluginCastCompletion(4, 2u, 0u, 0u);

        verbs.Handle(Line("cast 2"));
        correlator.Tick(inWorld: true);
        clock.Advance(CastVerbs.WindowSeconds);
        correlator.Tick(inWorld: true);

        Assert.Equal("unconfirmed", Records(ring, RecordKinds.CastOutcome).Single().GetProperty("outcome").GetString());
    }

    [Fact]
    public void AnAmbiguousNameIsRefusedWithCandidatesAndNothingIsSent()
    {
        var (host, verbs, _, _, ring) = Build();
        host.FakeAutomation.FakeSpells.KnownSpells =
        [
            FakeSpells.Spell(10u, "Flame Bolt I", selfTargeted: false, beneficial: false),
            FakeSpells.Spell(11u, "Flame Bolt II", selfTargeted: false, beneficial: false),
        ];

        Assert.Equal("refused", verbs.Handle(Line("cast flame bolt")).Outcome);

        JsonElement refusal = Records(ring, RecordKinds.CastRefused).Single();
        Assert.Equal("ambiguous", refusal.GetProperty("word").GetString());
        Assert.Equal(2, refusal.GetProperty("candidates").GetArrayLength());
        Assert.Empty(host.FakeAutomation.FakeMagic.Requests);
    }

    [Fact]
    public void AnUnknownSpellIsRefused()
    {
        var (host, verbs, _, _, _) = Build();

        Assert.Equal("refused", verbs.Handle(Line("cast Portal Recall")).Outcome);
        Assert.Empty(host.FakeAutomation.FakeMagic.Requests);
    }

    [Fact]
    public void ATargetedSpellWithNothingSelectedIsRefused()
    {
        var (host, verbs, _, _, ring) = Build();

        Assert.Equal("refused", verbs.Handle(Line("cast Flame Bolt VI")).Outcome);
        Assert.Equal("no-target-selected", Records(ring, RecordKinds.CastRefused).Single().GetProperty("word").GetString());
        Assert.Empty(host.FakeAutomation.FakeMagic.Requests);
    }

    [Fact]
    public void OnATargetCastsAtItAndSaysTheSelectionChanged()
    {
        var (host, verbs, _, _, ring) = Build();
        host.FakeAutomation.FakeObjects.Add(new PluginWorldObject(
            0x70000001u, 1u, "Drudge", PluginObjectClass.Monster, 16u, 0u, 0u));

        Assert.Equal("handled", verbs.Handle(Line("cast Flame Bolt VI on 0x70000001")).Outcome);

        Assert.Equal([(5u, (uint?)0x70000001u)], host.FakeAutomation.FakeMagic.Requests);
        JsonElement sent = Records(ring, RecordKinds.CastSent).Single();
        Assert.Equal("0x70000001", sent.GetProperty("target").GetString());
        Assert.True(sent.GetProperty("selectionChanged").GetBoolean());
    }

    [Fact]
    public void ASelfSpellGivenATargetIsRefused()
    {
        var (host, verbs, _, _, _) = Build();
        host.FakeAutomation.FakeObjects.Add(new PluginWorldObject(
            0x70000001u, 1u, "Drudge", PluginObjectClass.Monster, 16u, 0u, 0u));

        Assert.Equal("refused", verbs.Handle(Line("cast Strength Self VI on 0x70000001")).Outcome);
        Assert.Empty(host.FakeAutomation.FakeMagic.Requests);
    }

    [Fact]
    public void MissingComponentsAreRefusedBeforeSending()
    {
        var (host, verbs, _, _, ring) = Build();
        host.FakeAutomation.FakeMagic.MissingComponents.Add(2u);

        Assert.Equal("refused", verbs.Handle(Line("cast 2")).Outcome);
        Assert.Equal("missing-components", Records(ring, RecordKinds.CastRefused).Single().GetProperty("word").GetString());
        Assert.Empty(host.FakeAutomation.FakeMagic.Requests);
    }

    [Fact]
    public void AGateThatIsNotReadyIsRefusedWithItsWord()
    {
        var (host, verbs, _, _, ring) = Build();
        host.FakeAutomation.FakeMagic.Gate = PluginCastGate.Busy;

        Assert.Equal("refused", verbs.Handle(Line("cast 2")).Outcome);
        Assert.Equal("busy", Records(ring, RecordKinds.CastRefused).Single().GetProperty("word").GetString());
    }

    [Fact]
    public void ASpellNameContainingOnStaysWhole()
    {
        var (host, verbs, _, _, _) = Build();
        host.FakeAutomation.FakeSpells.KnownSpells = [FakeSpells.Spell(30u, "Hand on Heart")];

        Assert.Equal("handled", verbs.Handle(Line("cast Hand on Heart")).Outcome);
        Assert.Equal([(30u, (uint?)null)], host.FakeAutomation.FakeMagic.Requests);
    }

    private static CommandLine Line(string text) => CommandLine.Parse(12, text, "mcp");

    private static IEnumerable<JsonElement> Records(RecordRing ring, string kind) =>
        ring.Read(-1, new HashSet<string> { kind }).Records
            .Select(record => JsonDocument.Parse(record.Json).RootElement);

    private static (FakePluginHost Host, CastVerbs Verbs, OutcomeCorrelator Correlator, AgentClock Clock, RecordRing Ring) Build()
    {
        var host = new FakePluginHost();
        host.FakeAutomation.FakeSpells.KnownSpells = [Strength, Bolt];
        var clock = new AgentClock();
        var ring = new RecordRing(64);
        var publisher = new Publisher(clock, ring);
        var correlator = new OutcomeCorrelator(publisher, clock);
        return (host, new CastVerbs(host, publisher, correlator), correlator, clock, ring);
    }
}
