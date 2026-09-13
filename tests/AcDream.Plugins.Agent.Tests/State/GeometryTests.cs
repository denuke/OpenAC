using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.State;

namespace AcDream.Plugins.Agent.Tests.State;

public sealed class GeometryTests
{
    [Theory]
    [InlineData(0.01, 0d, 0d)]
    [InlineData(0d, 0.01, 90d)]
    [InlineData(-0.01, 0d, 180d)]
    [InlineData(0d, -0.01, 270d)]
    public void BearingIsACompassBearing(double northSouth, double eastWest, double expected)
    {
        Assert.Equal(expected, Geometry.BearingDegrees(At(0d, 0d), At(northSouth, eastWest)), 6);
    }

    [Theory]
    [InlineData(90d, 0d, 90d)]
    [InlineData(10d, 350d, 20d)]
    [InlineData(350d, 10d, -20d)]
    [InlineData(180d, 0d, 180d)]
    public void RelativeBearingTakesTheShortWayRound(double bearing, double heading, double expected)
    {
        Assert.Equal(expected, Geometry.RelativeDegrees(bearing, heading), 6);
    }

    [Fact]
    public void DistanceCombinesTheMapAndElevation()
    {
        PluginNavigationPosition from = At(0d, 0d);
        var to = new PluginNavigationPosition(0u, 0d, 0.0125, 4d, 0f, true);

        Assert.Equal(5d, Geometry.DistanceMeters(from, to), 6);
    }

    private static PluginNavigationPosition At(double northSouth, double eastWest) =>
        new(0u, eastWest, northSouth, 0d, 0f, true);
}
