using System.Text.Json;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;
using AcDream.Plugins.Agent.Verbs;

namespace AcDream.Plugins.Agent.Tests.Verbs;

public sealed class UnavailableVerbsTests
{
    [Theory]
    [InlineData("go to Mite Maze")]
    [InlineData("goto Mite Maze")]
    public void GoingToAPlaceIsRefusedNamingTheMissingRungAndTheMovesThatExist(string text)
    {
        var ring = new RecordRing(8);
        var verbs = new UnavailableVerbs(new Publisher(new AgentClock(), ring));

        VerbResult result = verbs.Handle(CommandLine.Parse(3, text, "mcp"));

        Assert.Equal("refused", result.Outcome);
        using JsonDocument record = JsonDocument.Parse(Assert.Single(ring.Read(-1).Records).Json);
        Assert.Equal(RecordKinds.GoalRefused, record.RootElement.GetProperty("kind").GetString());
        Assert.Equal(3, record.RootElement.GetProperty("id").GetInt64());
        Assert.Equal("navigation", record.RootElement.GetProperty("rung").GetString());
        Assert.Contains("'walk'", record.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void LogoutIsLeftToTheLoginVerbs()
    {
        var verbs = new UnavailableVerbs(new Publisher(new AgentClock(), new RecordRing(8)));

        Assert.DoesNotContain("logout", verbs.ReservedWords);
    }

    [Fact]
    public void MovementWordsAreNoLongerRefusedHere()
    {
        var verbs = new UnavailableVerbs(new Publisher(new AgentClock(), new RecordRing(8)));

        Assert.DoesNotContain("walk", verbs.ReservedWords);
        Assert.DoesNotContain("run", verbs.ReservedWords);
        Assert.DoesNotContain("strafe", verbs.ReservedWords);
        Assert.DoesNotContain("stop", verbs.ReservedWords);
    }
}
