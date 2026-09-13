using System.Text.Json;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Tests.Fakes;

namespace AcDream.Plugins.Agent.Tests.Verbs;

public sealed class CharacterReadVerbsTests
{
    [Theory]
    [InlineData("vitals", RecordKinds.Vitals)]
    [InlineData("stats", RecordKinds.Stats)]
    [InlineData("location", RecordKinds.Body)]
    public void AStateReadPublishesAFreshRecordCarryingTheLineId(string verb, string kind)
    {
        var host = new FakePluginHost();
        using var service = new AgentService(host, _ => new RecordingSink());

        long id = service.Commands.Deliver(verb, "mcp").Id;

        JsonElement record = Latest(service, kind);
        Assert.Equal(id, record.GetProperty("id").GetInt64());
    }

    [Fact]
    public void AnUnchangedStateIsStillAnsweredWhenAsked()
    {
        var host = new FakePluginHost();
        using var service = new AgentService(host, _ => new RecordingSink());

        service.Commands.Deliver("vitals", "mcp");
        long second = service.Commands.Deliver("vitals", "mcp").Id;

        Assert.Equal(second, Latest(service, RecordKinds.Vitals).GetProperty("id").GetInt64());
    }

    [Fact]
    public void SkillsListTrainingAndValues()
    {
        var host = new FakePluginHost();
        host.FakeAutomation.FakeCharacter.Skills =
        [
            new PluginSkillInfo(34u, "War Magic", PluginSkillTraining.Specialized, 400u) { Base = 380u },
        ];
        using var service = new AgentService(host, _ => new RecordingSink());

        service.Commands.Deliver("skills", "mcp");

        JsonElement skill = Latest(service, RecordKinds.Skills)
            .GetProperty("skills").GetProperty("value")[0];
        Assert.Equal("War Magic", skill.GetProperty("name").GetString());
        Assert.Equal("specialized", skill.GetProperty("training").GetString());
        Assert.Equal(400, skill.GetProperty("current").GetInt32());
        Assert.Equal(380, skill.GetProperty("base").GetInt32());
    }

    [Fact]
    public void BuffsNameTheirSpellWhenTheCatalogKnowsIt()
    {
        var host = new FakePluginHost();
        host.FakeAutomation.FakeCharacter.ActiveEnchantments =
        [
            new PluginActiveEnchantment(2u, 1u, 6, 1799.54),
            new PluginActiveEnchantment(9u, 2u, 1, 60d),
        ];
        host.FakeAutomation.FakeSpells.Catalog[2u] = FakeSpells.Spell(2u, "Strength Self VI");
        using var service = new AgentService(host, _ => new RecordingSink());

        service.Commands.Deliver("buffs", "mcp");

        JsonElement enchantments = Latest(service, RecordKinds.Buffs)
            .GetProperty("enchantments").GetProperty("value");
        Assert.Equal("Strength Self VI", enchantments[0].GetProperty("name").GetString());
        Assert.Equal(1799.5, enchantments[0].GetProperty("secondsRemaining").GetDouble());
        Assert.Equal(JsonValueKind.Null, enchantments[1].GetProperty("name").ValueKind);
    }

    [Fact]
    public void SpellsAreListedOnceAcrossTheHostsListsInNameOrder()
    {
        var host = new FakePluginHost();
        PluginSpellInfo bolt = FakeSpells.Spell(5u, "Flame Bolt VI", selfTargeted: false, beneficial: false);
        host.FakeAutomation.FakeSpells.KnownSelfBuffs = [FakeSpells.Spell(2u, "Strength Self VI")];
        host.FakeAutomation.FakeSpells.KnownAttackSpells = [bolt];
        host.FakeAutomation.FakeSpells.KnownCombatSpells = [bolt];
        host.FakeAutomation.FakeMagic.MissingComponents.Add(5u);
        using var service = new AgentService(host, _ => new RecordingSink());

        service.Commands.Deliver("spells", "mcp");

        JsonElement spells = Latest(service, RecordKinds.Spells)
            .GetProperty("spells").GetProperty("value");
        Assert.Equal(2, spells.GetArrayLength());
        Assert.Equal("Flame Bolt VI", spells[0].GetProperty("name").GetString());
        Assert.False(spells[0].GetProperty("hasComponents").GetBoolean());
        Assert.True(spells[1].GetProperty("hasComponents").GetBoolean());
    }

    [Fact]
    public void OutOfTheWorldAListIsUnknownRatherThanEmpty()
    {
        var host = new FakePluginHost();
        host.FakeAutomation.IsAvailable = false;
        using var service = new AgentService(host, _ => new RecordingSink());

        service.Commands.Deliver("skills", "mcp");

        Assert.Equal(
            "unknown",
            Latest(service, RecordKinds.Skills).GetProperty("skills").GetProperty("presence").GetString());
    }

    [Fact]
    public void SnapshotOnRequestPublishesEveryState()
    {
        var host = new FakePluginHost();
        using var service = new AgentService(host, _ => new RecordingSink());

        service.Commands.Deliver("snapshot", "mcp");

        Assert.Equal(
            RecordKinds.State,
            service.Ring.Read(-1).Records
                .Where(record => record.Kind != RecordKinds.CommandOutcome)
                .Select(record => record.Kind));
    }

    private static JsonElement Latest(AgentService service, string kind) =>
        JsonDocument.Parse(
            service.Ring.Read(-1, new HashSet<string> { kind }).Records.Last().Json)
            .RootElement;
}
