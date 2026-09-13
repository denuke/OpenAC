using System.Text.Json.Nodes;
using AcDream.Plugins.Agent.State;
using AcDream.Plugins.Agent.Tests.Fakes;

namespace AcDream.Plugins.Agent.Tests.State;

public sealed class VitalsProjectionTests
{
    [Fact]
    public void AVitalWithNoMaximumIsUnknownNotZero()
    {
        var host = new FakePluginHost();

        JsonObject vitals = new VitalsProjection(host).Capture();

        Assert.Equal("unknown", vitals["health"]!["current"]!["presence"]!.GetValue<string>());
        Assert.Equal("unknown", vitals["health"]!["max"]!["presence"]!.GetValue<string>());
    }

    [Fact]
    public void ADeadBodyHasAnObservedZeroHealth()
    {
        var host = new FakePluginHost();
        host.FakeAutomation.FakeCharacter.CurrentHealth = 0u;
        host.FakeAutomation.FakeCharacter.MaxHealth = 300u;

        JsonObject vitals = new VitalsProjection(host).Capture();

        Assert.Equal("observed", vitals["health"]!["current"]!["presence"]!.GetValue<string>());
        Assert.Equal(0u, vitals["health"]!["current"]!["value"]!.GetValue<uint>());
        Assert.Equal(0d, vitals["health"]!["fraction"]!["value"]!.GetValue<double>());
    }

    [Fact]
    public void TheFractionIsDerivedAndSaysFromWhat()
    {
        var host = new FakePluginHost();
        host.FakeAutomation.FakeCharacter.CurrentMana = 150u;
        host.FakeAutomation.FakeCharacter.MaxMana = 300u;

        JsonObject vitals = new VitalsProjection(host).Capture();

        JsonNode fraction = vitals["mana"]!["fraction"]!;
        Assert.Equal(0.5, fraction["value"]!.GetValue<double>());
        Assert.Equal("current and max", fraction["from"]!.GetValue<string>());
    }

    [Fact]
    public void OutOfTheWorldEveryVitalIsUnknown()
    {
        var host = new FakePluginHost();
        host.FakeAutomation.FakeCharacter.MaxHealth = 300u;
        host.FakeAutomation.FakeCharacter.CurrentHealth = 300u;
        host.FakeAutomation.IsAvailable = false;

        JsonObject vitals = new VitalsProjection(host).Capture();

        Assert.Equal("unknown", vitals["health"]!["current"]!["presence"]!.GetValue<string>());
        Assert.Equal("unknown", vitals["guid"]!["presence"]!.GetValue<string>());
    }
}
