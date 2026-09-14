using System.Text.Json;
using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Mcp.Tools;
using AcDream.Plugins.Agent.Tests.Fakes;
using AcDream.Plugins.Agent.Tests.Verbs;
using AcDream.Plugins.Agent.Verbs;

namespace AcDream.Plugins.Agent.Tests.Mcp;

public sealed class ReadToolsTests
{
    [Theory]
    [InlineData("nearby", "{}", "nearby")]
    [InlineData("nearby", """{"kind":"monster","range":20}""", "nearby monster 20")]
    [InlineData("nearby", """{"range":"12.5"}""", "nearby 12.5")]
    [InlineData("explore", "{}", "explore")]
    [InlineData("explore", """{"limit":5}""", "explore limit 5")]
    [InlineData("inspect", """{"guid":"0x70000001"}""", "inspect 0x70000001")]
    [InlineData("spells", "{}", "spells")]
    [InlineData("spells", """{"search":" strength "}""", "spells strength")]
    [InlineData("skills", "{}", "skills")]
    [InlineData("buffs", "{}", "buffs")]
    [InlineData("inventory", """{"search":"healing kit"}""", "inventory healing kit")]
    [InlineData("equipment", "{}", "equipment")]
    [InlineData("vendor", "{}", "vendor")]
    [InlineData("container", "{}", "loot list")]
    [InlineData("corpses", "{}", "loot corpses")]
    [InlineData("corpses", """{"range":12}""", "loot corpses 12")]
    [InlineData("spells", """{"search":"bolt","limit":5}""", "spells bolt limit 5")]
    [InlineData("inventory", """{"limit":3}""", "inventory limit 3")]
    [InlineData("vendor", """{"limit":20}""", "vendor limit 20")]
    [InlineData("container", """{"limit":2}""", "loot list limit 2")]
    [InlineData("capabilities", "{}", "capabilities")]
    [InlineData("capabilities", """{"guid":"  "}""", "capabilities")]
    [InlineData("capabilities", """{"guid":"0x70000001"}""", "capabilities 0x70000001")]
    [InlineData("settings", "{}", "settings")]
    [InlineData("settings", """{"plugin":" mosstank "}""", "settings mosstank")]
    [InlineData("settings", """{"plugin":"mosstank","section":"options"}""", "settings mosstank options")]
    public async Task EachReadRunsItsLine(string tool, string arguments, string line)
    {
        using var harness = new McpToolHarness();

        await harness.CallAsync(tool, JsonNode.Parse(arguments)!.AsObject());

        using JsonDocument outcome = JsonDocument.Parse(harness.Service.Ring
            .Read(-1, new HashSet<string> { RecordKinds.CommandOutcome }).Records.Single().Json);
        Assert.Equal(line, outcome.RootElement.GetProperty("line").GetString());
        Assert.Equal("mcp", outcome.RootElement.GetProperty("source").GetString());
    }

    [Theory]
    [InlineData("nearby", """{"kind":"big monster"}""")]
    [InlineData("nearby", """{"kind":""}""")]
    [InlineData("nearby", """{"range":-5}""")]
    [InlineData("nearby", """{"range":"far"}""")]
    [InlineData("explore", """{"limit":0}""")]
    [InlineData("inspect", """{"guid":"the drudge"}""")]
    [InlineData("inspect", "{}")]
    [InlineData("spells", """{"search":"a\nsay hi"}""")]
    [InlineData("inventory", """{"search":7}""")]
    [InlineData("corpses", """{"range":0}""")]
    [InlineData("spells", """{"limit":0}""")]
    [InlineData("inventory", """{"limit":501}""")]
    [InlineData("vendor", """{"limit":2.5}""")]
    [InlineData("container", """{"limit":"all"}""")]
    [InlineData("capabilities", """{"guid":"everything"}""")]
    [InlineData("settings", """{"section":"options"}""")]
    [InlineData("settings", """{"plugin":"moss tank"}""")]
    [InlineData("settings", """{"plugin":"mosstank","section":"two words"}""")]
    public async Task BadArgumentsAreAnErrorAndRunNothing(string tool, string arguments)
    {
        using var harness = new McpToolHarness();

        JsonObject result = await harness.CallAsync(tool, JsonNode.Parse(arguments)!.AsObject());

        Assert.True(McpToolHarness.IsError(result));
        Assert.Equal(0, harness.Service.Ring.Count);
    }

    [Fact]
    public async Task ASearchLongerThanTheLimitIsAnError()
    {
        using var harness = new McpToolHarness();

        JsonObject result = await harness.CallAsync("spells",
            new JsonObject { ["search"] = new string('a', ReadTools.MaximumSearchLength + 1) });

        Assert.True(McpToolHarness.IsError(result));
    }

    [Fact]
    public async Task SpellsReturnsTheSpellsRecordNarrowedBySearch()
    {
        using var harness = new McpToolHarness();
        harness.Host.FakeAutomation.FakeSpells.KnownSpells =
        [
            FakeSpells.Spell(2u, "Strength Self VI"),
            FakeSpells.Spell(5u, "Flame Bolt VI", selfTargeted: false, beneficial: false),
        ];

        JsonElement result = McpToolHarness.Structured(await harness.CallAsync("spells",
            new JsonObject { ["search"] = "strength" }));

        Assert.Equal("done", result.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("reason").ValueKind);
        JsonElement record = result.GetProperty("record");
        Assert.Equal("spells", record.GetProperty("kind").GetString());
        Assert.False(record.TryGetProperty("schema", out _));
        Assert.Equal("Strength Self VI", Assert.Single(record.GetProperty("spells").GetProperty("value").EnumerateArray())
            .GetProperty("name").GetString());
    }

    [Fact]
    public async Task ASpellListLongerThanItsLimitSaysHowManyMatched()
    {
        using var harness = new McpToolHarness();
        harness.Host.FakeAutomation.FakeSpells.KnownSpells =
        [
            FakeSpells.Spell(1u, "Strength Self I"),
            FakeSpells.Spell(2u, "Strength Self II"),
            FakeSpells.Spell(3u, "Strength Self III"),
            FakeSpells.Spell(4u, "Flame Bolt I", selfTargeted: false, beneficial: false),
        ];

        JsonElement record = McpToolHarness.Structured(await harness.CallAsync("spells",
            new JsonObject { ["search"] = "strength", ["limit"] = 2 })).GetProperty("record");

        Assert.Equal(3, record.GetProperty("matched").GetInt32());
        Assert.Equal(2, record.GetProperty("shown").GetInt32());
        Assert.Equal(1, record.GetProperty("beyondLimit").GetInt32());
        Assert.Equal(2, record.GetProperty("spells").GetProperty("value").GetArrayLength());
    }

    [Fact]
    public async Task ASpellListWithNoLimitShowsTheDefaultRows()
    {
        using var harness = new McpToolHarness();
        harness.Host.FakeAutomation.FakeSpells.KnownSpells = Enumerable.Range(1, CharacterReadVerbs.DefaultSpellRows + 7)
            .Select(index => FakeSpells.Spell((uint)index, $"Spell {index:000}"))
            .ToArray();

        JsonElement record = McpToolHarness.Structured(await harness.CallAsync("spells", new JsonObject()))
            .GetProperty("record");

        Assert.Equal(CharacterReadVerbs.DefaultSpellRows + 7, record.GetProperty("matched").GetInt32());
        Assert.Equal(CharacterReadVerbs.DefaultSpellRows, record.GetProperty("shown").GetInt32());
    }

    [Fact]
    public async Task ACapabilitiesGuidThatIsNotAnIdSaysToLeaveItOut()
    {
        using var harness = new McpToolHarness();

        JsonObject result = await harness.CallAsync("capabilities", new JsonObject { ["guid"] = "all" });

        Assert.True(McpToolHarness.IsError(result));
        Assert.Contains("leave it out", result.ToJsonString());
    }

    [Fact]
    public async Task InventoryReturnsWhatIsCarried()
    {
        using var harness = new McpToolHarness();
        harness.Host.FakeAutomation.FakeItems.Owned.Add(LootVerbsTests.Item(0x50000201u, "Lead Scarab", 0x50000001u));

        JsonElement record = McpToolHarness.Structured(await harness.CallAsync("inventory", new JsonObject()))
            .GetProperty("record");

        Assert.Equal("inventory", record.GetProperty("kind").GetString());
        Assert.Equal("0x50000201", record.GetProperty("items")[0].GetProperty("guid").GetString());
    }

    [Fact]
    public async Task NearbyReturnsTheObjectsAround()
    {
        using var harness = new McpToolHarness();
        harness.Host.FakeAutomation.FakeObjects.Add(new PluginWorldObject(
            0x70000001u, 1u, "Drudge", PluginObjectClass.Monster, 16u, 0u, 0u)
        {
            HasPosition = true,
            IsLandscape = true,
            Position = new PluginNavigationPosition(0u, 0d, 0.01, 0d, 0f, true),
        });

        JsonElement record = McpToolHarness.Structured(await harness.CallAsync("nearby",
            new JsonObject { ["kind"] = "monster" })).GetProperty("record");

        Assert.Equal("Drudge", record.GetProperty("entities")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task ContainerWithNothingOpenIsRefusedWithTheReason()
    {
        using var harness = new McpToolHarness();

        JsonElement result = McpToolHarness.Structured(await harness.CallAsync("container", new JsonObject()));

        Assert.Equal("refused", result.GetProperty("status").GetString());
        Assert.Contains("no container is open", result.GetProperty("reason").GetString());
        Assert.Equal("inventory-refused", result.GetProperty("record").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task VendorWithNothingOpenIsDoneWithUnknownListings()
    {
        using var harness = new McpToolHarness();

        JsonElement record = McpToolHarness.Structured(await harness.CallAsync("vendor", new JsonObject()))
            .GetProperty("record");

        Assert.False(record.GetProperty("open").GetBoolean());
        Assert.Equal("unknown", record.GetProperty("items").GetProperty("presence").GetString());
    }
}
