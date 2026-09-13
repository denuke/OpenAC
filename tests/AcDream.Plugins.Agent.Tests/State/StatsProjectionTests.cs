using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.State;
using AcDream.Plugins.Agent.Tests.Fakes;

namespace AcDream.Plugins.Agent.Tests.State;

public sealed class StatsProjectionTests
{
    [Fact]
    public void LevelZeroMeansTheLevelWasNotReceived()
    {
        var host = new FakePluginHost();

        JsonObject stats = new StatsProjection(host).Capture();

        Assert.Equal("unknown", stats["level"]!["presence"]!.GetValue<string>());
    }

    [Fact]
    public void AttributesAreKeyedByNameAndMissingOnesAreUnknown()
    {
        var host = new FakePluginHost();
        host.FakeAutomation.FakeCharacter.Level = 42;
        host.FakeAutomation.FakeCharacter.Attributes =
        [
            new PluginAttributeInfo(0, "Strength", 100u) { Base = 90u },
            new PluginAttributeInfo(5, "Self", 200u) { Base = 180u },
        ];

        JsonObject stats = new StatsProjection(host).Capture();

        Assert.Equal(42, stats["level"]!["value"]!.GetValue<int>());
        JsonNode attributes = stats["attributes"]!;
        Assert.Equal(100u, attributes["strength"]!["current"]!["value"]!.GetValue<uint>());
        Assert.Equal(90u, attributes["strength"]!["base"]!["value"]!.GetValue<uint>());
        Assert.Equal(200u, attributes["self"]!["current"]!["value"]!.GetValue<uint>());
        Assert.Equal("unknown", attributes["endurance"]!["current"]!["presence"]!.GetValue<string>());
    }
}
