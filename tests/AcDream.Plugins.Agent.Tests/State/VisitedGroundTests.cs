using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.State;

namespace AcDream.Plugins.Agent.Tests.State;

public sealed class VisitedGroundTests
{
    [Fact]
    public void GroundStoodOnCountsForSpotsBesideItOnTheSameLevelOnly()
    {
        var visited = new VisitedGround();
        visited.Note(At(0d, 0d, 0d));

        Assert.True(visited.IsNear(At(6d, 0d, 0d)));
        Assert.False(visited.IsNear(At(12d, 0d, 0d)));
        Assert.False(visited.IsNear(At(0d, 0d, 6d)));
    }

    private static PluginNavigationPosition At(double eastMeters, double northMeters, double upMeters) =>
        new(0u, eastMeters / 240d, northMeters / 240d, upMeters / 240d, 0f, true);
}
