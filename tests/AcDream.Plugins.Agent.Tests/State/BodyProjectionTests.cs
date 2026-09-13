using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.State;
using AcDream.Plugins.Agent.Tests.Fakes;

namespace AcDream.Plugins.Agent.Tests.State;

public sealed class BodyProjectionTests
{
    [Fact]
    public void WithNoPositionEveryBodyFactIsUnknown()
    {
        var host = new FakePluginHost();

        JsonObject body = new BodyProjection(host).Capture();

        foreach (KeyValuePair<string, JsonNode?> field in body)
            Assert.Equal("unknown", field.Value!["presence"]!.GetValue<string>());
    }

    [Fact]
    public void TheSimulatedPositionIsStatedInMapCoordinates()
    {
        var host = new FakePluginHost();
        host.FakeAutomation.FakeNavigation.Snapshot = Snapshot(
            new PluginNavigationPosition(0xA9B40021u, 33.61, 42.12, 55.123, 354.4f, true),
            revision: 0UL);

        JsonObject body = new BodyProjection(host).Capture();

        JsonNode position = body["position"]!["value"]!;
        Assert.Equal("0xA9B40021", position["cell"]!.GetValue<string>());
        Assert.Equal(42.12, position["northSouth"]!.GetValue<double>());
        Assert.Equal(33.61, position["eastWest"]!.GetValue<double>());
        Assert.False(position["indoor"]!.GetValue<bool>());
        Assert.Equal("42.1N, 33.6E", position["text"]!.GetValue<string>());
        Assert.Equal(354d, body["heading"]!["value"]!.GetValue<double>());
        Assert.True(body["moving"]!["value"]!.GetValue<bool>());
    }

    [Fact]
    public void SouthAndWestAreNamedBySign()
    {
        Assert.Equal(
            "12.5S, 3.3W",
            Coordinates.Text(new PluginNavigationPosition(0u, -3.26, -12.51, 0d, 0f, true)));
    }

    [Fact]
    public void AnUnconfirmedPositionIsUnknownRatherThanTheSimulatedOne()
    {
        var host = new FakePluginHost();
        host.FakeAutomation.FakeNavigation.Snapshot = Snapshot(
            new PluginNavigationPosition(0xA9B40021u, 33.61, 42.12, 0d, 0f, true),
            revision: 0UL);

        JsonObject body = new BodyProjection(host).Capture();

        Assert.Equal("unknown", body["confirmed"]!["presence"]!.GetValue<string>());
    }

    [Fact]
    public void AConfirmedPositionCarriesItsRevision()
    {
        var host = new FakePluginHost();
        host.FakeAutomation.FakeNavigation.Snapshot = Snapshot(
            new PluginNavigationPosition(0xA9B40021u, 33.61, 42.12, 0d, 0f, true),
            revision: 7UL);

        JsonObject body = new BodyProjection(host).Capture();

        Assert.Equal("observed", body["confirmed"]!["presence"]!.GetValue<string>());
        Assert.Equal(7UL, body["confirmed"]!["value"]!["revision"]!.GetValue<ulong>());
    }

    private static PluginNavigationSnapshot Snapshot(
        PluginNavigationPosition position,
        ulong revision) =>
        new(
            IsAvailable: true,
            IsPortalSpace: false,
            LocalObjectId: 0x50000001u,
            Position: position,
            IsMoving: true,
            IsAirborne: false)
        {
            ConfirmedPosition = position,
            ConfirmedPositionRevision = revision,
        };
}
