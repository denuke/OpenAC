using System.Text.Json;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;
using AcDream.Plugins.Agent.Tests.Fakes;
using AcDream.Plugins.Agent.Verbs;

namespace AcDream.Plugins.Agent.Tests.Verbs;

public sealed class PluginSettingsVerbsTests
{
    [Fact]
    public void SettingsListsThePluginsThatShareTheirs()
    {
        var (_, verbs, ring, _) = Build();

        Assert.Equal("handled", verbs.Handle(Line("settings")).Outcome);

        JsonElement plugin = Assert.Single(Single(ring, RecordKinds.PluginSettings).GetProperty("plugins").EnumerateArray());
        Assert.Equal("acdream.mosstank/settings", plugin.GetProperty("plugin").GetString());
        Assert.Equal("MossTank", plugin.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("settings mosstank")]
    [InlineData("settings MossTank")]
    [InlineData("settings acdream.mosstank")]
    [InlineData("settings ACDREAM.MOSSTANK/SETTINGS")]
    public void SettingsReadsAPluginsSettingsByAnyOfItsNames(string text)
    {
        var (_, verbs, ring, _) = Build();

        Assert.Equal("handled", verbs.Handle(Line(text)).Outcome);

        JsonElement read = Single(ring, RecordKinds.PluginSettings);
        Assert.Equal("acdream.mosstank/settings", read.GetProperty("plugin").GetString());
        Assert.Contains("options", read.GetProperty("howToChange").GetString());
        Assert.True(read.GetProperty("settings").GetProperty("options").GetProperty("EnableCombat").GetBoolean());
        Assert.Equal(JsonValueKind.Array, read.GetProperty("settings").GetProperty("monsters").ValueKind);
    }

    [Fact]
    public void SettingsReadsOneSectionAndRefusesOneThePluginLacks()
    {
        var (_, verbs, ring, _) = Build();

        Assert.Equal("handled", verbs.Handle(Line("settings mosstank options")).Outcome);
        JsonElement read = Single(ring, RecordKinds.PluginSettings);
        Assert.Equal("options", read.GetProperty("section").GetString());
        Assert.False(read.GetProperty("settings").TryGetProperty("monsters", out _));

        Assert.Equal("refused", verbs.Handle(Line("settings mosstank loot")).Outcome);
        Assert.Contains("loot", Single(ring, RecordKinds.SettingsRefused).GetProperty("reason").GetString());
    }

    [Fact]
    public void SettingsSaysWhichPluginsShareWhenAWordNamesNone()
    {
        var (_, verbs, ring, _) = Build();

        VerbResult result = verbs.Handle(Line("settings vtank"));

        Assert.Equal("refused", result.Outcome);
        Assert.Contains("MossTank (acdream.mosstank/settings)", result.Reason);
        Assert.Single(Kinds(ring, RecordKinds.SettingsRefused));
    }

    [Fact]
    public void ConfigureHandsThePluginItsChangeAndReportsWhatChanged()
    {
        var (_, verbs, ring, provider) = Build();

        Assert.Equal("handled", verbs.Handle(Line("""configure mosstank { "options": { "EnableCombat": false } }""")).Outcome);

        Assert.Equal("""{"options":{"EnableCombat":false}}""", Assert.Single(provider.Changes));
        JsonElement changed = Single(ring, RecordKinds.SettingsChanged);
        Assert.Equal("Set 1 option.", changed.GetProperty("message").GetString());
        Assert.False(changed.GetProperty("settings").GetProperty("options").GetProperty("EnableCombat").GetBoolean());
    }

    [Fact]
    public void ConfigureGivesThePluginsReasonsForRefusingAChange()
    {
        var (_, verbs, ring, provider) = Build();
        provider.Answer = new(false, "Nothing was changed: 'Speed' is not an option.");

        VerbResult result = verbs.Handle(Line("""configure mosstank {"options":{"Speed":9}}"""));

        Assert.Equal("refused", result.Outcome);
        Assert.Equal("Nothing was changed: 'Speed' is not an option.", result.Reason);
        JsonElement refused = Single(ring, RecordKinds.SettingsRefused);
        Assert.Equal("acdream.mosstank/settings", refused.GetProperty("plugin").GetString());
        Assert.Empty(Kinds(ring, RecordKinds.SettingsChanged));
    }

    [Theory]
    [InlineData("configure")]
    [InlineData("configure mosstank")]
    [InlineData("configure mosstank EnableCombat true")]
    [InlineData("configure mosstank [1, 2]")]
    [InlineData("""configure vtank {"options":{}}""")]
    public void ConfigureRefusesALineItCannotHandToAPlugin(string text)
    {
        var (_, verbs, ring, provider) = Build();

        Assert.Equal("refused", verbs.Handle(Line(text)).Outcome);

        Assert.Empty(provider.Changes);
        Assert.Single(Kinds(ring, RecordKinds.SettingsRefused));
    }

    private static (FakePluginHost Host, PluginSettingsVerbs Verbs, RecordRing Ring, FakeSettingsProvider Provider) Build()
    {
        var host = new FakePluginHost();
        var provider = new FakeSettingsProvider();
        host.FakeSettings.Register("acdream.mosstank/settings", "MossTank", provider);
        var ring = new RecordRing(64);
        var publisher = new Publisher(new AgentClock(), ring);
        return (host, new PluginSettingsVerbs(host, publisher), ring, provider);
    }

    private static CommandLine Line(string text) => CommandLine.Parse(31, text, "mcp");

    private static IEnumerable<JsonElement> Kinds(RecordRing ring, string kind) =>
        ring.Read(-1, new HashSet<string> { kind }).Records.Select(static record => JsonDocument.Parse(record.Json).RootElement);

    private static JsonElement Single(RecordRing ring, string kind) => Assert.Single(Kinds(ring, kind));
}
