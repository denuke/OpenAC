using System.Text.Json;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;
using AcDream.Plugins.Agent.Tests.Fakes;
using AcDream.Plugins.Agent.Verbs;

namespace AcDream.Plugins.Agent.Tests.Verbs;

public sealed class CapabilityVerbsTests
{
    private const uint Self = 0x50000001u;
    private const uint Drudge = 0x70000001u;
    private const uint Shopkeeper = 0x70000002u;
    private const uint Door = 0x70000003u;
    private const uint Corpse = 0x70000004u;
    private const uint Scarab = 0x50000A01u;

    [Fact]
    public void TheWholeViewGradesEveryObjectNearbyWouldListAsTheOneObjectAnswerDoes()
    {
        var (_, verbs, ring) = Scene();

        Assert.Equal("handled", verbs.Handle(Line("capabilities")).Outcome);

        JsonElement view = Latest(ring, RecordKinds.PlayerCapabilities);
        JsonElement[] rows = [.. view.GetProperty("entities").EnumerateArray()];
        Assert.Equal(
            ["Drudge Skulker", "Shopkeeper", "Door", "Corpse of a Drudge"],
            rows.Select(row => row.GetProperty("name").GetString()));
        JsonElement[] legend = [.. view.GetProperty("legend").EnumerateArray()];
        foreach (JsonElement row in rows)
        {
            string guid = row.GetProperty("guid").GetString()!;
            verbs.Handle(Line($"capabilities {guid}"));
            string[] alone = [.. Latest(ring, RecordKinds.Capabilities).GetProperty("actions").EnumerateArray().Select(Describe)];
            string[] combined =
            [
                .. legend.Select(line => Describe(line).Replace(CapabilityVerbs.GuidHole, guid, StringComparison.Ordinal)),
                .. row.GetProperty("actions").EnumerateArray().Select(Describe),
            ];
            Assert.Equal(alone.Order(StringComparer.Ordinal), combined.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void ALineGradedAlikeForEveryObjectIsSaidOnceAndOneThatDiffersStaysOnItsRow()
    {
        var (_, verbs, ring) = Scene();

        verbs.Handle(Line("capabilities"));

        JsonElement view = Latest(ring, RecordKinds.PlayerCapabilities);
        string[] legend = [.. view.GetProperty("legend").EnumerateArray().Select(line => line.GetProperty("verb").GetString()!)];
        Assert.Contains("inspect", legend);
        Assert.Contains("cast", legend);
        Assert.DoesNotContain("open", legend);
        Assert.Contains(view.GetProperty("legend").EnumerateArray(),
            line => line.GetProperty("line").GetString() == "inspect <guid>");
        JsonElement corpse = view.GetProperty("entities").EnumerateArray()
            .Single(row => row.GetProperty("name").GetString() == "Corpse of a Drudge");
        JsonElement open = Action(corpse, "open");
        Assert.Equal("available", open.GetProperty("verdict").GetString());
        Assert.Equal("open 0x70000004", open.GetProperty("line").GetString());
    }

    [Fact]
    public void AttackIsAvailableOnlyOnAHostileCreatureInAMeleeOrMissileStance()
    {
        var (host, verbs, ring) = Scene();

        Assert.Equal("available", Verdict(verbs, ring, Drudge, "attack"));
        Assert.Equal(AttackVerbs.NotHostile, Because(verbs, ring, Shopkeeper, "attack"));

        host.FakeAutomation.FakeCombat.Snapshot = host.FakeAutomation.FakeCombat.Snapshot with { Mode = PluginCombatMode.Peace };
        Assert.Equal(AttackVerbs.WrongStance, Because(verbs, ring, Drudge, "attack"));
        Assert.Equal(AttackVerbs.WrongStance, Because(verbs, ring, Shopkeeper, "attack"));
    }

    [Fact]
    public void ACarriedItemIsGradedAsAnItemAndItsSaleAsTheClientWouldJudgeIt()
    {
        var (host, verbs, ring) = Scene();
        host.FakeAutomation.FakeObjects.Add(new PluginWorldObject(
            Scarab, 1u, "Lead Scarab", PluginObjectClass.Misc, 8u, Self, 0u) { IsOwned = true, LastIdTime = 4200 });

        verbs.Handle(Line($"capabilities 0x{Scarab:X8}"));
        JsonElement carried = Latest(ring, RecordKinds.Capabilities);
        Assert.Equal("carried", carried.GetProperty("reach").GetString());
        Assert.Equal("available", Action(carried, "use").GetProperty("verdict").GetString());
        Assert.Equal(VendorVerbs.NoVendor, Action(carried, "sell").GetProperty("because").GetString());
        Assert.DoesNotContain(carried.GetProperty("actions").EnumerateArray(),
            line => line.GetProperty("verb").GetString() == "go to");

        host.FakeAutomation.FakeItems.ActiveVendorObjectId = Shopkeeper;
        verbs.Handle(Line($"capabilities 0x{Scarab:X8}"));
        JsonElement sell = Action(Latest(ring, RecordKinds.Capabilities), "sell");
        Assert.Equal("available", sell.GetProperty("verdict").GetString());
        Assert.Equal("sell it to Shopkeeper", sell.GetProperty("label").GetString());

        host.FakeAutomation.FakeItems.SaleCheck =
            new PluginItemCommandResult(PluginItemCommandStatus.Refused, "This item cannot be sold");
        verbs.Handle(Line($"capabilities 0x{Scarab:X8}"));
        Assert.Equal("This item cannot be sold",
            Action(Latest(ring, RecordKinds.Capabilities), "sell").GetProperty("because").GetString());

        host.FakeAutomation.FakeItems.SaleCheck = new PluginItemCommandResult(PluginItemCommandStatus.Unavailable);
        verbs.Handle(Line($"capabilities 0x{Scarab:X8}"));
        Assert.Equal("unknown", Action(Latest(ring, RecordKinds.Capabilities), "sell").GetProperty("verdict").GetString());

        host.FakeAutomation.FakeItems.SaleCheck = new PluginItemCommandResult(PluginItemCommandStatus.Started);
        host.FakeAutomation.FakeObjects.Add(new PluginWorldObject(
            Scarab, 1u, "Lead Scarab", PluginObjectClass.Misc, 8u, Self, 0u) { IsOwned = true });
        verbs.Handle(Line($"capabilities 0x{Scarab:X8}"));
        JsonElement unappraised = Action(Latest(ring, RecordKinds.Capabilities), "sell");
        Assert.Equal("unknown", unappraised.GetProperty("verdict").GetString());
        Assert.Contains("appraises it first", unappraised.GetProperty("because").GetString());
    }

    [Fact]
    public void AnItemInsideACorpseIsLootedOnlyWhileThatCorpseIsOpen()
    {
        const uint sword = 0x70000030u;
        var (host, verbs, ring) = Scene();
        host.FakeAutomation.FakeObjects.Add(
            new PluginWorldObject(sword, 1u, "Rusty Sword", PluginObjectClass.MeleeWeapon, 1u, Corpse, 0u));

        Assert.Equal(LootVerbs.NoContainer, Because(verbs, ring, sword, "loot"));

        host.FakeAutomation.FakeLoot.CurrentContainerId = Corpse;
        Assert.Equal("available", Verdict(verbs, ring, sword, "loot"));
        Assert.Equal("inside", Latest(ring, RecordKinds.Capabilities).GetProperty("reach").GetString());

        host.FakeAutomation.FakeLoot.CurrentContainerId = 0x70000031u;
        Assert.Contains("not the open container", Because(verbs, ring, sword, "loot"));
    }

    [Fact]
    public void TheCharacterLinesNameTheOpenVendorAndSayWhatIsMissing()
    {
        var (host, verbs, ring) = Scene();
        host.FakeAutomation.FakeItems.ActiveVendorObjectId = Shopkeeper;

        verbs.Handle(Line("capabilities"));

        JsonElement character = Latest(ring, RecordKinds.PlayerCapabilities).GetProperty("character");
        Assert.Equal("melee", character.GetProperty("stance").GetString());
        Assert.Equal("Shopkeeper", character.GetProperty("vendor").GetProperty("name").GetString());
        JsonElement buy = Action(character, "buy");
        Assert.Equal("available", buy.GetProperty("verdict").GetString());
        Assert.Equal("buy from Shopkeeper", buy.GetProperty("label").GetString());
        Assert.Equal(LootVerbs.NoContainer, Action(character, "loot").GetProperty("because").GetString());
        Assert.Equal("nothing is selected", Action(character, "untarget").GetProperty("because").GetString());
        Assert.Equal("available", Action(character, "stance combat").GetProperty("verdict").GetString());
    }

    [Fact]
    public void AWalkBeyondItsLimitIsGradedUnavailableWithTheDistance()
    {
        var (host, verbs, ring) = Scene();
        host.FakeAutomation.FakeObjects.Add(Placed(0x70000009u, "Faraway Drudge", PluginObjectClass.Monster, 5d));

        Assert.Contains("a walk is planned to at most", Because(verbs, ring, 0x70000009u, "go to"));
    }

    [Theory]
    [InlineData("capabilities")]
    [InlineData("capabilities 0x70000001")]
    public void WithNoCharacterInTheWorldTheAnswerIsRefused(string text)
    {
        var (host, verbs, ring) = Scene();
        host.FakeAutomation.IsAvailable = false;

        Assert.Equal("refused", verbs.Handle(Line(text)).Outcome);

        Assert.Equal("no character is in the world", Latest(ring, RecordKinds.EntityRefused).GetProperty("reason").GetString());
        Assert.Empty(ring.Read(-1, new HashSet<string> { RecordKinds.Capabilities, RecordKinds.PlayerCapabilities }).Records);
    }

    [Theory]
    [InlineData("capabilities 0x70000099", "the client holds no object with that id")]
    [InlineData("capabilities all", "leave it out to ask about everything")]
    public void ALineTheClientCannotAnswerIsRefusedWithTheReason(string text, string reason)
    {
        var (_, verbs, ring) = Scene();

        Assert.Equal("refused", verbs.Handle(Line(text)).Outcome);

        Assert.Contains(reason, Latest(ring, RecordKinds.EntityRefused).GetProperty("reason").GetString());
    }

    private static string Verdict(CapabilityVerbs verbs, RecordRing ring, uint id, string verb) =>
        Answer(verbs, ring, id, verb).GetProperty("verdict").GetString()!;

    private static string Because(CapabilityVerbs verbs, RecordRing ring, uint id, string verb) =>
        Answer(verbs, ring, id, verb).GetProperty("because").GetString()!;

    private static JsonElement Answer(CapabilityVerbs verbs, RecordRing ring, uint id, string verb)
    {
        verbs.Handle(Line($"capabilities 0x{id:X8}"));
        return Action(Latest(ring, RecordKinds.Capabilities), verb);
    }

    private static JsonElement Action(JsonElement record, string verb) =>
        record.GetProperty("actions").EnumerateArray().Single(line => line.GetProperty("verb").GetString() == verb);

    private static string Describe(JsonElement line) =>
        string.Join('|',
            line.GetProperty("verb").GetString(),
            line.GetProperty("label").GetString(),
            line.GetProperty("line").GetString(),
            line.GetProperty("verdict").GetString(),
            line.GetProperty("because").GetString());

    private static PluginWorldObject Placed(uint id, string name, PluginObjectClass objectClass, double northSouth) =>
        new(id, 1u, name, objectClass, 16u, 0u, 0u)
        {
            HasPosition = true,
            IsLandscape = true,
            Position = new PluginNavigationPosition(0xA9B40021u, 0d, northSouth, 0d, 0f, true),
        };

    private static CommandLine Line(string text) => CommandLine.Parse(12, text, "mcp");

    private static JsonElement Latest(RecordRing ring, string kind) =>
        JsonDocument.Parse(ring.Read(-1, new HashSet<string> { kind }).Records.Last().Json).RootElement;

    private static (FakePluginHost Host, CapabilityVerbs Verbs, RecordRing Ring) Scene()
    {
        var host = new FakePluginHost();
        FakeAutomation automation = host.FakeAutomation;
        automation.FakeNavigation.Snapshot = new PluginNavigationSnapshot(
            IsAvailable: true,
            IsPortalSpace: false,
            LocalObjectId: Self,
            Position: new PluginNavigationPosition(0xA9B40021u, 0d, 0d, 0d, 0f, true),
            IsMoving: false,
            IsAirborne: false);
        automation.FakeCombat.Snapshot = automation.FakeCombat.Snapshot with { Mode = PluginCombatMode.Melee };
        automation.FakeObjects.Add(Placed(Drudge, "Drudge Skulker", PluginObjectClass.Monster, 0.01));
        automation.FakeObjects.Add(Placed(Shopkeeper, "Shopkeeper", PluginObjectClass.Vendor, 0.02));
        automation.FakeObjects.Add(Placed(Door, "Door", PluginObjectClass.Door, 0.03));
        automation.FakeObjects.Add(Placed(Corpse, "Corpse of a Drudge", PluginObjectClass.Corpse, 0.04) with { IsOpenable = true });
        automation.FakeCombat.Hostiles.Add(default(PluginCombatTarget) with
        {
            ObjectId = Drudge,
            Name = "Drudge Skulker",
            Distance = 2.4f,
        });
        var clock = new AgentClock();
        var ring = new RecordRing(256);
        var publisher = new Publisher(clock, ring);
        return (host, new CapabilityVerbs(host, publisher, new OutcomeCorrelator(publisher, clock)), ring);
    }
}
