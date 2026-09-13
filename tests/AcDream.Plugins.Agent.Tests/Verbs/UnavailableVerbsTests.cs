using System.Text.Json;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;
using AcDream.Plugins.Agent.Verbs;

namespace AcDream.Plugins.Agent.Tests.Verbs;

public sealed class UnavailableVerbsTests
{
    [Fact]
    public void GoingToAPlaceIsRefusedNamingTheMissingRung()
    {
        var ring = new RecordRing(8);
        var verbs = new UnavailableVerbs(new Publisher(new AgentClock(), ring));

        VerbResult result = verbs.Handle(CommandLine.Parse(3, "go to Mite Maze", "mcp"));

        Assert.Equal("refused", result.Outcome);
        using JsonDocument record = JsonDocument.Parse(Assert.Single(ring.Read(-1).Records).Json);
        Assert.Equal(RecordKinds.GoalRefused, record.RootElement.GetProperty("kind").GetString());
        Assert.Equal(3, record.RootElement.GetProperty("id").GetInt64());
        Assert.Equal("navigation", record.RootElement.GetProperty("rung").GetString());
    }

    [Theory]
    [InlineData("logout")]
    [InlineData("stop")]
    public void SessionAndAmbiguousWordsAreRefusedWithoutAGoalRecord(string text)
    {
        var ring = new RecordRing(8);
        var verbs = new UnavailableVerbs(new Publisher(new AgentClock(), ring));

        VerbResult result = verbs.Handle(CommandLine.Parse(1, text, "mcp"));

        Assert.Equal("refused", result.Outcome);
        Assert.Equal(0, ring.Count);
    }
}
