using System.Text.Json;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Tests.Fakes;

namespace AcDream.Plugins.Agent.Tests.Verbs;

public sealed class TrendVerbsTests
{
    [Fact]
    public void TrendsAreRefusedUntilCountingStartsThenAnsweredCarryingTheLineId()
    {
        var host = new FakePluginHost();
        using var service = new AgentService(host, _ => new RecordingSink());

        long early = service.Commands.Deliver("trends", "mcp").Id;
        Assert.Equal("refused", Outcome(service, early).GetProperty("outcome").GetString());

        for (int second = 0; second < 30; second++)
            service.OnTick(1d);
        long later = service.Commands.Deliver("trends", "mcp").Id;

        JsonElement trends = service.Ring.Read(-1, new HashSet<string> { RecordKinds.Trends }).Records
            .Select(record => JsonDocument.Parse(record.Json).RootElement.Clone())
            .Last();
        Assert.Equal(later, trends.GetProperty("id").GetInt64());
        Assert.Equal(JsonValueKind.Object, trends.GetProperty("xpPerHour").ValueKind);
        Assert.NotEqual("refused", Outcome(service, later).GetProperty("outcome").GetString());
    }

    private static JsonElement Outcome(AgentService service, long id) =>
        service.Ring.Read(-1, new HashSet<string> { RecordKinds.CommandOutcome }).Records
            .Select(record => JsonDocument.Parse(record.Json).RootElement.Clone())
            .Single(record => record.GetProperty("id").GetInt64() == id);
}
