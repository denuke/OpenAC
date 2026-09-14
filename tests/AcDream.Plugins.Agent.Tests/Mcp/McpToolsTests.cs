using System.Text.Json;
using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Mcp;
using AcDream.Plugins.Agent.Tests.Fakes;

namespace AcDream.Plugins.Agent.Tests.Mcp;

public sealed class McpToolsTests
{
    [Fact]
    public void ToolsListNamesEveryToolAndOnlyActSends()
    {
        using var harness = new McpToolHarness();

        JsonObject[] definitions = harness.Tools.List().Select(node => node!.AsObject()).ToArray();

        Assert.Equal(
            [
                "act", "buffs", "capabilities", "characters", "configure", "container", "corpses", "equipment", "events",
                "inspect", "inventory", "nearby", "observe", "outcome", "settings", "skills", "spells", "vendor",
            ],
            definitions.Select(tool => tool["name"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        foreach (JsonObject tool in definitions)
        {
            bool readOnly = tool["annotations"]!["readOnlyHint"]!.GetValue<bool>();
            Assert.Equal(tool["name"]!.GetValue<string>() is not ("act" or "configure"), readOnly);
        }
    }

    [Fact]
    public async Task ConfigureHandsAPluginItsChangeAndAnswersWithWhatChanged()
    {
        using var harness = new McpToolHarness();
        var provider = new FakeSettingsProvider();
        harness.Host.FakeSettings.Register("acdream.mosstank/settings", "MossTank", provider);

        JsonObject result = await harness.CallAsync("configure", new JsonObject
        {
            ["plugin"] = "mosstank",
            ["change"] = new JsonObject { ["options"] = new JsonObject { ["EnableCombat"] = false } },
        });

        JsonElement answer = McpToolHarness.Structured(result);
        Assert.False(McpToolHarness.IsError(result));
        Assert.Equal("done", answer.GetProperty("status").GetString());
        Assert.Equal("""{"options":{"EnableCombat":false}}""", Assert.Single(provider.Changes));
        Assert.Contains(
            answer.GetProperty("records").EnumerateArray(),
            record => record.GetProperty("kind").GetString() == "settings-changed");
    }

    [Theory]
    [InlineData("""{"change":{"options":{}}}""")]
    [InlineData("""{"plugin":"moss tank","change":{"options":{}}}""")]
    [InlineData("""{"plugin":"mosstank","change":"EnableCombat"}""")]
    public async Task ConfigureRefusesArgumentsItCannotSend(string arguments)
    {
        using var harness = new McpToolHarness();

        JsonObject result = await harness.CallAsync("configure", JsonNode.Parse(arguments)!.AsObject());

        Assert.True(McpToolHarness.IsError(result));
    }

    [Fact]
    public void ListingTwiceGivesTheSameDefinitions()
    {
        using var harness = new McpToolHarness();

        Assert.Equal(harness.Tools.List().ToJsonString(), harness.Tools.List().ToJsonString());
    }

    [Fact]
    public async Task ObserveShowsTheStateAndTheActionsStillPending()
    {
        using var harness = new McpToolHarness();
        harness.StartTracking();
        await harness.CallAsync("act", new JsonObject { ["line"] = "turn to 90" });

        JsonElement observed = McpToolHarness.Structured(await harness.CallAsync("observe", new JsonObject()));

        Assert.Equal("in-world", observed.GetProperty("state").GetProperty("session").GetProperty("state").GetString());
        Assert.False(observed.GetProperty("state").GetProperty("session").TryGetProperty("schema", out _));
        Assert.Equal("turn", observed.GetProperty("pendingActions")[0].GetProperty("verb").GetString());
    }

    [Fact]
    public async Task ActReturnsAHandleAndOutcomeReportsHowItEnded()
    {
        using var harness = new McpToolHarness();

        JsonElement act = McpToolHarness.Structured(await harness.CallAsync("act", new JsonObject { ["line"] = "turn to 90" }));
        Assert.Equal("pending", act.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, act.GetProperty("confirmed").ValueKind);

        harness.Host.FakeAutomation.FakeNavigation.End(PluginMoveChannel.Turn, PluginMoveState.Completed, covered: 90f);
        harness.Tick();

        JsonElement outcome = McpToolHarness.Structured(await harness.CallAsync("outcome",
            new JsonObject { ["handle"] = act.GetProperty("handle").GetString() }));
        Assert.Equal("done", outcome.GetProperty("status").GetString());
        Assert.Equal("completed", outcome.GetProperty("outcome").GetString());
        Assert.True(outcome.GetProperty("confirmed").GetBoolean());
    }

    [Fact]
    public async Task ActWithAWaitAnswersOnceTheActionSettles()
    {
        using var harness = new McpToolHarness();

        Task<JsonObject> call = harness.Begin("act", new JsonObject { ["line"] = "turn to 90", ["waitSeconds"] = 5 });
        harness.Tick();
        Assert.False(call.IsCompleted);

        harness.Host.FakeAutomation.FakeNavigation.End(PluginMoveChannel.Turn, PluginMoveState.Completed, covered: 90f);
        harness.Tick();

        Assert.True(call.IsCompleted);
        JsonElement result = McpToolHarness.Structured(await call);
        Assert.Equal("done", result.GetProperty("status").GetString());
        Assert.Equal("completed", result.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task ActWithAWaitStillPendingAtTheDeadlineSaysPending()
    {
        using var harness = new McpToolHarness();

        Task<JsonObject> call = harness.Begin("act", new JsonObject { ["line"] = "turn to 90", ["waitSeconds"] = 1 });
        harness.Tick();
        harness.Tick(elapsedSeconds: 1.0);

        Assert.True(call.IsCompleted);
        JsonElement result = McpToolHarness.Structured(await call);
        Assert.Equal("pending", result.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("outcome").ValueKind);
    }

    [Theory]
    [InlineData("go to Mite Maze", "refused", "refused")]
    [InlineData("hello there", "sent-as-chat", "chat")]
    [InlineData("say hello", "done", "handled")]
    public async Task ActReportsLinesThatSettleAtOnce(string line, string status, string outcome)
    {
        using var harness = new McpToolHarness();

        JsonElement result = McpToolHarness.Structured(await harness.CallAsync("act", new JsonObject { ["line"] = line }));

        Assert.Equal(status, result.GetProperty("status").GetString());
        Assert.Equal(outcome, result.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task ARefusedLineSaysWhy()
    {
        using var harness = new McpToolHarness();

        JsonElement result = McpToolHarness.Structured(await harness.CallAsync("act", new JsonObject { ["line"] = "go to Mite Maze" }));

        Assert.False(result.GetProperty("confirmed").GetBoolean());
        Assert.False(string.IsNullOrEmpty(result.GetProperty("reason").GetString()));
    }

    [Theory]
    [InlineData("say a\nsay b")]
    [InlineData("   ")]
    public async Task ActRefusesAnythingButOneLine(string line)
    {
        using var harness = new McpToolHarness();

        JsonObject result = await harness.CallAsync("act", new JsonObject { ["line"] = line });

        Assert.True(McpToolHarness.IsError(result));
        Assert.Empty(harness.Host.FakeAutomation.FakeChat.Submitted);
    }

    [Fact]
    public async Task OutcomeOfAHandleNoRecordCarriesIsUnknownAtOnceEvenWithAWait()
    {
        using var harness = new McpToolHarness();

        JsonElement result = McpToolHarness.Structured(await harness.CallAsync("outcome",
            new JsonObject { ["handle"] = 999, ["waitSeconds"] = 30 }));

        Assert.Equal("unknown", result.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(result.GetProperty("reason").GetString()));
    }

    [Fact]
    public async Task OutcomeIgnoresRecordsThatOnlyNestTheHandleNumber()
    {
        using var harness = new McpToolHarness();
        harness.Host.FakeAutomation.FakeSpells.KnownSpells = [FakeSpells.Spell(2u, "Strength Self VI")];
        await harness.CallAsync("spells", new JsonObject());
        JsonElement say = McpToolHarness.Structured(await harness.CallAsync("act", new JsonObject { ["line"] = "say hi" }));
        Assert.Equal("2", say.GetProperty("handle").GetString());

        JsonElement outcome = McpToolHarness.Structured(await harness.CallAsync("outcome", new JsonObject { ["handle"] = "2" }));

        JsonElement only = Assert.Single(outcome.GetProperty("records").EnumerateArray());
        Assert.Equal(2, only.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task EventsFromNowWaitsForTheNextRecord()
    {
        using var harness = new McpToolHarness();
        harness.StartTracking();

        Task<JsonObject> call = harness.Begin("events",
            new JsonObject { ["kinds"] = new JsonArray("chat"), ["waitSeconds"] = 5 });
        harness.Tools.Tick();
        Assert.False(call.IsCompleted);

        harness.Host.FakeAutomation.FakeChat.Receive("hello");
        harness.Tick();

        Assert.True(call.IsCompleted);
        JsonElement result = McpToolHarness.Structured(await call);
        Assert.Equal("hello", result.GetProperty("records")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task EventsUntilWakesOnlyOnTheMatchingRecordAndStopsTheCursorThere()
    {
        using var harness = new McpToolHarness();
        harness.Host.FakeAutomation.FakeCharacter.MaxHealth = 300u;
        harness.Host.FakeAutomation.FakeCharacter.CurrentHealth = 300u;
        harness.StartTracking();

        Task<JsonObject> call = harness.Begin("events", new JsonObject
        {
            ["kinds"] = new JsonArray("vital-changed"),
            ["until"] = new JsonArray(LowHealth()),
            ["waitSeconds"] = 10,
        });
        harness.Tools.Tick();

        harness.Host.FakeAutomation.FakeCharacter.CurrentHealth = 250u;
        harness.Tick();
        Assert.False(call.IsCompleted);

        harness.Host.FakeAutomation.FakeCharacter.CurrentHealth = 50u;
        harness.Tick();

        Assert.True(call.IsCompleted);
        JsonElement result = McpToolHarness.Structured(await call);
        JsonElement[] records = result.GetProperty("records").EnumerateArray().ToArray();
        Assert.Equal([250, 50], records.Select(record => record.GetProperty("value").GetInt32()));
        Assert.Equal(0, result.GetProperty("woke").GetProperty("clause").GetInt32());
        Assert.Equal(records[1].GetProperty("seq").GetInt64(), result.GetProperty("nextSeq").GetInt64());
    }

    [Fact]
    public async Task EventsUntilWakesOnAKindItDoesNotReturnAndTheCursorResumesAfterIt()
    {
        using var harness = new McpToolHarness();
        harness.Host.FakeAutomation.FakeCharacter.MaxHealth = 300u;
        harness.Host.FakeAutomation.FakeCharacter.CurrentHealth = 300u;
        harness.StartTracking();

        Task<JsonObject> call = harness.Begin("events", new JsonObject
        {
            ["kinds"] = new JsonArray("chat"),
            ["until"] = new JsonArray(LowHealth()),
            ["waitSeconds"] = 10,
        });
        harness.Tools.Tick();
        harness.Host.FakeAutomation.FakeCharacter.CurrentHealth = 50u;
        harness.Host.FakeAutomation.FakeChat.Receive("help");
        harness.Tick();

        Assert.True(call.IsCompleted);
        JsonElement woke = McpToolHarness.Structured(await call);
        Assert.Equal("vital-changed", woke.GetProperty("woke").GetProperty("record").GetProperty("kind").GetString());
        Assert.True(woke.GetProperty("more").GetBoolean());

        JsonElement next = McpToolHarness.Structured(await harness.CallAsync("events", new JsonObject
        {
            ["kinds"] = new JsonArray("chat"),
            ["sinceSeq"] = woke.GetProperty("nextSeq").GetInt64(),
        }));
        Assert.Equal("help", next.GetProperty("records")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task EventsFromACursorAheadOfTheStreamStartsFromNow()
    {
        using var harness = new McpToolHarness();

        JsonElement result = McpToolHarness.Structured(await harness.CallAsync("events",
            new JsonObject { ["sinceSeq"] = 1_000_000 }));

        Assert.True(result.GetProperty("cursorReset").GetBoolean());
        Assert.Equal(harness.Service.Ring.LastSeq, result.GetProperty("nextSeq").GetInt64());
    }

    [Theory]
    [InlineData("""{"until":[{"kind":"k","compare":["n","~",1]}]}""")]
    [InlineData("""{"kinds":"chat"}""")]
    [InlineData("""{"sinceSeq":1.5}""")]
    [InlineData("""{"limit":0}""")]
    public async Task EventsWithBadArgumentsIsAnError(string arguments)
    {
        using var harness = new McpToolHarness();

        JsonObject result = await harness.CallAsync("events", JsonNode.Parse(arguments)!.AsObject());

        Assert.True(McpToolHarness.IsError(result));
    }

    [Fact]
    public async Task ACanceledCallNeverRuns()
    {
        using var harness = new McpToolHarness();
        using var cancel = new CancellationTokenSource();

        Task<JsonObject> call = harness.Tools.CallAsync("act", new JsonObject { ["line"] = "say hi" },
            McpToolHarness.Session, cancel.Token);
        await cancel.CancelAsync();
        harness.Tools.Tick();

        Assert.True(call.IsCompleted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Empty(harness.Host.FakeAutomation.FakeChat.Submitted);
        Assert.Equal(0, harness.Tools.RunningCount);
    }

    [Fact]
    public async Task EndAllAnswersWaitingCallsWithAnError()
    {
        using var harness = new McpToolHarness();

        Task<JsonObject> call = harness.Begin("act", new JsonObject { ["line"] = "turn to 90", ["waitSeconds"] = 30 });
        harness.Tools.Tick();
        harness.Tools.EndAll("the agent stopped listening");

        Assert.True(call.IsCompleted);
        Assert.True(McpToolHarness.IsError(await call));
        Assert.Equal(0, harness.Tools.RunningCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AToolThatThrowsAnswersWithAnError(bool whileStarting)
    {
        var tools = new McpToolHost([new ThrowingTool(whileStarting)]);

        Task<JsonObject> call = tools.CallAsync("throws", new JsonObject(), McpToolHarness.Session, CancellationToken.None);
        tools.Tick();

        Assert.True(call.IsCompleted);
        Assert.True(McpToolHarness.IsError(await call));
        Assert.Equal(0, tools.RunningCount);
    }

    [Fact]
    public async Task AnUnknownToolIsAnError()
    {
        using var harness = new McpToolHarness();

        Assert.True(McpToolHarness.IsError(await harness.CallAsync("unknown-tool", new JsonObject())));
    }

    private sealed class ThrowingTool(bool whileStarting) : IMcpTool
    {
        public string Name => "throws";

        public JsonObject Definition() => new();

        public IMcpToolRun Start(JsonObject arguments, McpSession session) =>
            whileStarting
                ? throw new InvalidOperationException("the tool broke while starting")
                : new PollingRun(() => throw new InvalidOperationException("the tool broke while running"));
    }

    private static JsonNode LowHealth() =>
        JsonNode.Parse("""{"kind":"vital-changed","match":{"vital":"health"},"compare":["value","<",100]}""")!;
}
