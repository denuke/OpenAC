using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.State;

namespace AcDream.Plugins.Agent.Tests.State;

public sealed class PlaceTourTests
{
    /// <summary>
    /// A start, then a junction with a short dead end, a longer dead end two places deep,
    /// and a way on to the farthest place.
    /// </summary>
    private static readonly PluginNavigationPlace[] Branching =
    [
        Place(0, 0, 0f, 1),
        Place(10, 0, 10f, 0, 2, 3, 5),
        Place(10, 5, 15f, 1),
        Place(20, 0, 20f, 1, 4),
        Place(45, 0, 45f, 3),
        Place(10, -10, 20f, 1, 6),
        Place(10, -25, 35f, 5),
    ];

    [Fact]
    public void SideBranchesAreClearedShallowestFirstBeforeTheWayOnToTheFarthestPlace()
    {
        List<int> order = PlaceTour.Order(Branching, endAt: null, _ => false, out int end);

        Assert.Equal(4, end);
        Assert.Equal([0, 1, 2, 5, 6, 3, 4], order);
    }

    [Fact]
    public void WaysThatMeetAgainAreEachWalkedOnceWithoutGoingBackAndForth()
    {
        PluginNavigationPlace[] places =
        [
            Place(0, 0, 0f, 1),
            Place(10, 0, 10f, 0, 2, 3),
            Place(20, 4, 20f, 1, 4),
            Place(20, -5, 21f, 1, 4),
            Place(30, 0, 30f, 2, 3, 5),
            Place(45, 0, 45f, 4),
        ];

        List<int> order = PlaceTour.Order(places, endAt: null, _ => false, out int end);

        Assert.Equal(5, end);
        Assert.Equal([0, 1, 3, 2, 4, 5], order);
    }

    [Fact]
    public void PlacesLeftOutAreNotStopsButTheTourStillGoesOnPastThem()
    {
        List<int> order = PlaceTour.Order(Branching, endAt: null, index => index is 1 or 5, out _);

        Assert.Equal([0, 2, 6, 3, 4], order);
    }

    [Fact]
    public void ATourAskedToEndSomewhereWalksThePlacesBeyondItBeforeEndingThere()
    {
        List<int> order = PlaceTour.Order(Branching, endAt: 3, _ => false, out int end);

        Assert.Equal(3, end);
        Assert.Equal([0, 1, 2, 5, 6, 4, 3], order);
    }

    [Fact]
    public void ATourAskedToStartSomewhereStartsThereAndEndsFarthestFromThere()
    {
        List<int> order = PlaceTour.Order(Branching, endAt: null, _ => false, out int end, startAt: 4);

        Assert.Equal(6, end);
        Assert.Equal([4, 3, 1, 2, 0, 5, 6], order);
    }

    [Fact]
    public void PlacesNoWayReachesComeAfterTheTourNearestWalkFirstAndBuildingsAreNoStops()
    {
        PluginNavigationPlace[] places =
        [
            Place(0, 0, 0f, 1),
            Place(10, 0, 10f, 0),
            Place(100, 0, 90f),
            Place(80, 0, 60f),
            Place(5, 5, float.NaN) with { Kind = PluginPlaceKind.Building },
        ];

        List<int> order = PlaceTour.Order(places, endAt: null, _ => false, out int end);

        Assert.Equal(1, end);
        Assert.Equal([0, 1, 3, 2], order);
    }

    [Fact]
    public void ATourStartsAtThePlaceNearestTheCharacterByWalkWhereverItIsListed()
    {
        PluginNavigationPlace[] places =
        [
            Place(20, 0, 20f, 1),
            Place(10, 0, 10f, 0, 2),
            Place(0, 0, 0f, 1),
        ];

        List<int> order = PlaceTour.Order(places, endAt: null, _ => false, out int end);

        Assert.Equal(2, PlaceTour.Start(places, startAt: null));
        Assert.Equal(0, end);
        Assert.Equal([2, 1, 0], order);
    }

    [Fact]
    public void ATourAskedToStartAtAPlaceNoWalkReachesStartsNearestTheCharacterInstead()
    {
        PluginNavigationPlace[] places =
        [
            Place(0, 0, 0f, 1),
            Place(10, 0, 10f, 0),
            Place(5, 5, float.NaN) with { Kind = PluginPlaceKind.Building },
        ];

        Assert.Equal(1, PlaceTour.Start(places, startAt: 1));
        Assert.Equal(0, PlaceTour.Start(places, startAt: 2));
        Assert.Equal(0, PlaceTour.Start(places, startAt: 7));
    }

    private static PluginNavigationPlace Place(double eastMeters, double northMeters, float walkMeters, params int[] neighbours) =>
        new(
            new PluginNavigationPosition(0u, eastMeters / 240d, northMeters / 240d, 0d, 0f, false),
            walkMeters,
            PluginPlaceKind.Room,
            60f,
            6f,
            0f,
            neighbours.Length)
        {
            Neighbours = neighbours,
        };
}
