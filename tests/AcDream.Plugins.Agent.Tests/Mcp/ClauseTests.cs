using System.Text.Json.Nodes;
using AcDream.Plugins.Agent.Mcp;

namespace AcDream.Plugins.Agent.Tests.Mcp;

public sealed class ClauseTests
{
    [Fact]
    public void AKindMatchAndComparisonMustAllHold()
    {
        Clause clause = Parse("""{"kind":"vital-changed","match":{"vital":"health"},"compare":["value","<",100]}""");

        Assert.True(clause.Matches(Record("""{"kind":"vital-changed","vital":"health","value":50}""")));
        Assert.False(clause.Matches(Record("""{"kind":"vital-changed","vital":"health","value":150}""")));
        Assert.False(clause.Matches(Record("""{"kind":"vital-changed","vital":"mana","value":50}""")));
        Assert.False(clause.Matches(Record("""{"kind":"chat","vital":"health","value":50}""")));
    }

    [Fact]
    public void AFieldPathReachesIntoNestedObjects()
    {
        Clause clause = Parse("""{"kind":"vitals","compare":["health.current.value","<=",40]}""");

        Assert.True(clause.Matches(Record("""{"kind":"vitals","health":{"current":{"value":40}}}""")));
        Assert.False(clause.Matches(Record("""{"kind":"vitals","health":{"current":{"presence":"unknown","value":null}}}""")));
    }

    [Theory]
    [InlineData("<", 11, true)]
    [InlineData("<", 10, false)]
    [InlineData("<=", 10, true)]
    [InlineData(">", 10, false)]
    [InlineData(">", 9, true)]
    [InlineData(">=", 10, true)]
    [InlineData("==", 10, true)]
    [InlineData("!=", 10, false)]
    [InlineData("!=", 9, true)]
    public void EveryOperatorCompares(string op, int value, bool expected)
    {
        Clause clause = Parse($$"""{"kind":"k","compare":["n","{{op}}",{{value}}]}""");

        Assert.Equal(expected, clause.Matches(Record("""{"kind":"k","n":10}""")));
    }

    [Theory]
    [InlineData("""{"match":{}}""")]
    [InlineData("""{"kind":"k","match":[1]}""")]
    [InlineData("""{"kind":"k","compare":["n","~",1]}""")]
    [InlineData("""{"kind":"k","compare":["n","<"]}""")]
    [InlineData("""[1]""")]
    public void AMalformedClauseIsRefused(string json)
    {
        Assert.False(Clause.TryParse(JsonNode.Parse(json), out _, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    private static Clause Parse(string json)
    {
        Assert.True(Clause.TryParse(JsonNode.Parse(json), out Clause? clause, out string? error), error);
        return clause!;
    }

    private static JsonObject Record(string json) => JsonNode.Parse(json)!.AsObject();
}
