using System.Text.Json.Nodes;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.State;
using AcDream.Plugins.Agent.Tests.Fakes;

namespace AcDream.Plugins.Agent.Tests.State;

public sealed class SessionProjectionTests
{
    [Fact]
    public void InTheWorldTheCharacterAndWorldAreObserved()
    {
        var host = new FakePluginHost();
        var projection = new SessionProjection(host);

        JsonObject state = projection.Capture();

        Assert.Equal(
            """{"state":"in-world","from":null,"character":{"presence":"observed","value":"Tester","because":null},"guid":{"presence":"observed","value":"0x50000001","because":null},"world":{"presence":"observed","value":"Testworld","because":null}}""",
            state.ToJsonString(AgentJson.Options));
    }

    [Fact]
    public void OutOfTheWorldNothingIsClaimedAndTheTransitionIsNamed()
    {
        var host = new FakePluginHost();
        var projection = new SessionProjection(host);
        projection.Capture();

        host.FakeAutomation.IsAvailable = false;
        JsonObject state = projection.Capture();

        Assert.Equal("out-of-world", state["state"]!.GetValue<string>());
        Assert.Equal("in-world", state["from"]!.GetValue<string>());
        Assert.Equal("unknown", state["character"]!["presence"]!.GetValue<string>());
        Assert.Equal("unknown", state["guid"]!["presence"]!.GetValue<string>());
    }

    [Fact]
    public void AnUnnamedCharacterInTheWorldIsUnknownNotBlank()
    {
        var host = new FakePluginHost();
        host.FakeAutomation.FakeCharacter.Name = string.Empty;

        JsonObject state = new SessionProjection(host).Capture();

        Assert.Equal("unknown", state["character"]!["presence"]!.GetValue<string>());
    }
}
