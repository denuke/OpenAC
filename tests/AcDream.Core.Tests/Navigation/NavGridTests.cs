using System.Numerics;
using AcDream.Core.Navigation;
using AcDream.Core.Physics;

namespace AcDream.Core.Tests.Navigation;

public sealed class NavGridTests
{
    private static readonly NavBody Body = new(0.48f, 1.835f, 0.4f, 0.4f);

    [Fact]
    public void AFlatFloorIsStoodOnAndItsEdgeIsTooTightToWalk()
    {
        NavGrid grid = Build(Floor(0f, 0f, 10f, 10f, 0f));

        int middle = grid.FindNode(new Vector3(5.125f, 5.125f, 0f), 0.1f, 0.5f);

        Assert.True(middle >= 0);
        Assert.Equal(0f, grid.Position(middle).Z, 3);
        Assert.Equal(-1, grid.FindNode(new Vector3(0.125f, 5.125f, 0f), 0.1f, 0.5f));
    }

    [Fact]
    public void ARouteAcrossOpenFloorIsOneStraightLeg()
    {
        NavGrid grid = Build(Floor(0f, 0f, 20f, 20f, 0f));

        NavRoute route = NavRouter.Find(grid, new Vector3(3f, 3f, 0f), new Vector3(15f, 12f, 0f), arrivalRadius: 1f);

        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        Assert.Equal(2, route.Legs.Count);
    }

    [Fact]
    public void AWallIsPassedThroughItsDoorway()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 20f, 0f),
            .. Wall(0f, 10f, 9f, 10f, 0f, 3f),
            .. Wall(11f, 10f, 20f, 10f, 0f, 3f)]);

        NavRoute route = NavRouter.Find(grid, new Vector3(3f, 5f, 0f), new Vector3(17f, 15f, 0f), arrivalRadius: 1f);

        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        Assert.Contains(route.Path, point => point.Y > 9.9f && point.Y < 10.4f && point.X > 9f && point.X < 11f);
    }

    [Fact]
    public void ADoorwayJustWiderThanTheBodyIsPassed()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 20f, 0f),
            .. Wall(0f, 10f, 9.4f, 10f, 0f, 3f),
            .. Wall(10.6f, 10f, 20f, 10f, 0f, 3f)]);

        NavRoute route = NavRouter.Find(grid, new Vector3(3f, 5f, 0f), new Vector3(17f, 15f, 0f), arrivalRadius: 1f);

        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
    }

    [Fact]
    public void ADoorwayNarrowerThanTheBodyIsNotPassedAndTheRouteEndsWhereTheGoalCanBeSeenThroughIt()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 20f, 0f),
            .. Wall(0f, 10f, 9.75f, 10f, 0f, 3f),
            .. Wall(10.25f, 10f, 20f, 10f, 0f, 3f)]);

        NavRoute route = NavRouter.Find(grid, new Vector3(3f, 5f, 0f), new Vector3(17f, 15f, 0f), arrivalRadius: 1f);

        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        Assert.All(route.Path, point => Assert.True(point.Y < 10f));
        Assert.Contains("nearest spot that can", route.Reason);
    }

    [Fact]
    public void ARampClimbsFromTheGroundOntoADeck()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 10f, 0f),
            .. Ramp(6f, 10f, 10f, 16f, 0f, 2.5f),
            .. Floor(0f, 16f, 20f, 20f, 2.5f)]);

        NavRoute route = NavRouter.Find(grid, new Vector3(3f, 3f, 0f), new Vector3(16f, 18f, 2.5f), arrivalRadius: 1f);

        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        Assert.Equal(2.5f, route.Path[^1].Z, 1);
        Assert.Contains(route.Path, point => point.Y > 11f && point.Y < 15f && point.Z > 0.3f && point.Z < 2.2f);
    }

    [Fact]
    public void ARampTooSteepToStandOnIsNotClimbed()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 10f, 0f),
            .. Ramp(6f, 10f, 10f, 12f, 0f, 4f),
            .. Floor(0f, 12f, 20f, 20f, 4f)]);

        NavRoute route = NavRouter.Find(grid, new Vector3(3f, 3f, 0f), new Vector3(16f, 16f, 4f), arrivalRadius: 1f);

        Assert.Equal(NavRouteOutcome.NoPath, route.Outcome);
    }

    [Fact]
    public void StairsWithLowRisersAreClimbed()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 10f, 0f),
            .. Stairs(6f, 10f, yStart: 10f, depth: 0.5f, rise: 0.2f, steps: 10),
            .. Floor(0f, 15f, 20f, 20f, 2f)]);

        NavRoute route = NavRouter.Find(grid, new Vector3(3f, 3f, 0f), new Vector3(16f, 18f, 2f), arrivalRadius: 1f);

        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        Assert.Equal(2f, route.Path[^1].Z, 1);
    }

    [Fact]
    public void StairsWithRisersTallerThanAStepAreNotClimbed()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 10f, 0f),
            .. Stairs(6f, 10f, yStart: 10f, depth: 0.5f, rise: 0.8f, steps: 3),
            .. Floor(0f, 11.5f, 20f, 20f, 2.4f)]);

        NavRoute route = NavRouter.Find(grid, new Vector3(3f, 3f, 0f), new Vector3(16f, 16f, 2.4f), arrivalRadius: 1f);

        Assert.Equal(NavRouteOutcome.NoPath, route.Outcome);
    }

    [Fact]
    public void GroundUnderADeckAndTheDeckAreSeparateStandingPoints()
    {
        NavGrid grid = Build([.. Floor(0f, 0f, 10f, 10f, 0f), .. Floor(3f, 3f, 7f, 7f, 3f)]);

        int ground = grid.FindNode(new Vector3(5.125f, 5.125f, 0f), 0.1f, 0.5f);
        int deck = grid.FindNode(new Vector3(5.125f, 5.125f, 3f), 0.1f, 0.5f);

        Assert.True(ground >= 0 && deck >= 0);
        Assert.Equal(0f, grid.Position(ground).Z, 3);
        Assert.Equal(3f, grid.Position(deck).Z, 3);
        Assert.Equal(grid.ColumnOf(ground), grid.ColumnOf(deck));
    }

    [Fact]
    public void ALowCeilingLeavesNoRoomToStandUnderIt()
    {
        NavGrid grid = Build([.. Floor(0f, 0f, 10f, 10f, 0f), .. Floor(2f, 2f, 8f, 8f, 1.2f)]);

        Assert.Equal(-1, grid.FindNode(new Vector3(5.125f, 5.125f, 0f), 0.1f, 0.5f));
        Assert.True(grid.FindNode(new Vector3(5.125f, 5.125f, 1.2f), 0.1f, 0.5f) >= 0);
    }

    [Fact]
    public void ACylinderIsWalkedAround()
    {
        NavGrid grid = Build(
            Floor(0f, 0f, 20f, 8f, 0f),
            cylinders: [new NavCylinder(new Vector3(10f, 4f, 0f), 1f, 2f)]);

        NavRoute route = NavRouter.Find(grid, new Vector3(2f, 4f, 0f), new Vector3(18f, 4f, 0f), arrivalRadius: 0.5f);

        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        Assert.True(route.Legs.Count > 2);
        Assert.All(route.Path, point =>
            Assert.True(Vector2.Distance(new Vector2(point.X, point.Y), new Vector2(10f, 4f)) >= 1f));
    }

    [Fact]
    public void TerrainIsStoodOnExceptWhereACellCoversIt()
    {
        var terrain = new TerrainSurface(new byte[81], new float[256]);
        NavGrid grid = NavGrid.Build(
            new NavGeometry(
                0f,
                0f,
                NavGeometry.LandblockSize,
                [new NavTerrain(terrain, 0f, 0f)],
                Floor(100f, 100f, 110f, 110f, 1f),
                [],
                []),
            Body);

        Assert.True(grid.FindNode(new Vector3(50.125f, 50.125f, 0f), 0.1f, 0.5f) >= 0);
        Assert.Equal(-1, grid.FindNode(new Vector3(105.125f, 105.125f, 0f), 0.1f, 0.5f));
        Assert.True(grid.FindNode(new Vector3(105.125f, 105.125f, 1f), 0.1f, 0.5f) >= 0);
        Assert.Equal(
            NavRouteOutcome.Routed,
            NavRouter.Find(grid, new Vector3(40f, 40f, 0f), new Vector3(60f, 70f, 0f), 1f).Outcome);
    }

    [Fact]
    public void ARegionSpanningTwoLandblocksIsWalkedAcrossTheirSeam()
    {
        NavGrid grid = NavGrid.Build(
            new NavGeometry(
                96f,
                0f,
                NavGeometry.LandblockSize,
                [
                    new NavTerrain(new TerrainSurface(new byte[81], new float[256]), 0f, 0f),
                    new NavTerrain(new TerrainSurface(new byte[81], new float[256]), 192f, 0f),
                ],
                [],
                [],
                []),
            Body);

        Assert.True(grid.FindNode(new Vector3(191.875f, 50.125f, 0f), 0.1f, 0.5f) >= 0);
        Assert.True(grid.FindNode(new Vector3(192.125f, 50.125f, 0f), 0.1f, 0.5f) >= 0);
        NavRoute route = NavRouter.Find(grid, new Vector3(150f, 50f, 0f), new Vector3(250f, 60f, 0f), 1f);
        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        Assert.Equal(2, route.Legs.Count);
    }

    [Fact]
    public void ARegionIsCapturedFromEveryResidentLandblockItOverlaps()
    {
        var engine = new PhysicsEngine();
        engine.AddLandblock(0xA9B4FFFFu, new TerrainSurface(new byte[81], new float[256]), [], [], 0f, 0f);
        engine.AddLandblock(0xAAB4FFFFu, new TerrainSurface(new byte[81], new float[256]), [], [], 192f, 0f);

        NavGeometry? both = NavGeometry.Capture(engine, 150f, 10f, 96f);
        NavGeometry? west = NavGeometry.Capture(engine, 10f, 10f, 96f);
        NavGeometry? east = NavGeometry.CaptureLandblock(engine, 0xAAB4FFFFu);

        Assert.Equal([0xA9B4FFFFu, 0xAAB4FFFFu], both!.LandblockIds.Order());
        Assert.Equal([0xA9B4FFFFu], west!.LandblockIds);
        Assert.Equal(192f, east!.OriginX);
        Assert.Equal([0xAAB4FFFFu], east.LandblockIds);
        Assert.Null(NavGeometry.Capture(engine, 1000f, 1000f, 96f));
    }

    [Fact]
    public void AGoalBehindAThinWallIsReachedFromTheSideThatCanSeeIt()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 20f, 0f),
            .. Wall(10f, 0f, 10f, 16f, 0f, 3f)]);

        NavRoute route = NavRouter.Find(grid, new Vector3(6f, 4f, 0f), new Vector3(11.5f, 4f, 0f), arrivalRadius: 2.5f);

        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        Assert.True(route.Path[^1].X > 10f);
        Assert.Contains(route.Path, point => point.Y > 16f);
    }

    [Fact]
    public void AGoalBehindACounterWindowIsReachedAtTheNearestSpotThatCanSeeIt()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 20f, 0f),
            .. Wall(12f, 8f, 16f, 8f, 0f, 3f),
            .. Wall(16f, 8f, 16f, 12f, 0f, 3f),
            .. Wall(16f, 12f, 12f, 12f, 0f, 3f),
            .. Wall(12f, 12f, 12f, 8f, 0f, 1f),
            .. Wall(12f, 12f, 12f, 8f, 2f, 3f)]);
        var goal = new Vector3(14.5f, 10f, 0f);

        NavRoute route = NavRouter.Find(grid, new Vector3(4f, 10f, 0f), goal, arrivalRadius: 2.5f);

        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        Assert.True(route.Path[^1].X < 12f);
        Assert.InRange(Vector2.Distance(new Vector2(route.Path[^1].X, route.Path[^1].Y), new Vector2(goal.X, goal.Y)), 2.5f, 4f);
        Assert.Contains("nearest spot that can", route.Reason);
    }

    [Fact]
    public void AGoalShutInACupboardIsReachedAtTheNearestSpotWithoutALineOfSight()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 20f, 0f),
            .. Wall(9.5f, 9.5f, 10.5f, 9.5f, 0f, 3f),
            .. Wall(10.5f, 9.5f, 10.5f, 10.5f, 0f, 3f),
            .. Wall(10.5f, 10.5f, 9.5f, 10.5f, 0f, 3f),
            .. Wall(9.5f, 10.5f, 9.5f, 9.5f, 0f, 3f)]);

        NavRoute route = NavRouter.Find(grid, new Vector3(3f, 3f, 0f), new Vector3(10f, 10f, 0f), arrivalRadius: 2.5f);

        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        Assert.Contains("without a line of sight", route.Reason);
        Vector3 end = route.Path[^1];
        Assert.False(end.X > 9.5f && end.X < 10.5f && end.Y > 9.5f && end.Y < 10.5f);
        Assert.InRange(Vector2.Distance(new Vector2(end.X, end.Y), new Vector2(10f, 10f)), 0.5f, 2.5f);
    }

    [Fact]
    public void AStartInsideAPassageTooNarrowToStandInIsNotTakenFromBeyondItsWalls()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 20f, 0f),
            .. Wall(0f, 10f, 12f, 10f, 0f, 3f),
            .. Wall(0f, 10.7f, 12f, 10.7f, 0f, 3f)]);
        var start = new Vector3(6f, 10.35f, 0f);

        Assert.True(grid.FindNode(start, NavRouter.StartRadius, NavRouter.StartHeightTolerance) >= 0);
        Assert.Equal(-1, grid.FindWalkableNode(start, NavRouter.StartRadius, NavRouter.StartHeightTolerance));
        Assert.Equal(NavRouteOutcome.NoStart, NavRouter.Find(grid, start, new Vector3(16f, 16f, 0f), 1f).Outcome);
    }

    [Fact]
    public void LegsThroughADoorwayKeepTheBodyClearOfItsFrame()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 20f, 0f),
            .. Wall(0f, 10f, 9.4f, 10f, 0f, 3f),
            .. Wall(10.6f, 10f, 20f, 10f, 0f, 3f)]);

        NavRoute route = NavRouter.Find(grid, new Vector3(2f, 4f, 0f), new Vector3(15f, 17f, 0f), arrivalRadius: 1f);

        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        for (int leg = 1; leg < route.Legs.Count; leg++)
        {
            foreach (Vector2 frame in new[] { new Vector2(9.4f, 10f), new Vector2(10.6f, 10f) })
            {
                Assert.True(
                    DistanceToSegment(frame, route.Legs[leg - 1], route.Legs[leg]) >= grid.NearestWall - 0.05f,
                    $"leg {leg} passes within {DistanceToSegment(frame, route.Legs[leg - 1], route.Legs[leg]):0.00} m of a door frame");
            }
        }
    }

    [Fact]
    public void ARouteKeepsOutOfAvoidedSpots()
    {
        NavGrid grid = Build([
            .. Floor(0f, 0f, 20f, 20f, 0f),
            .. Wall(0f, 10f, 4f, 10f, 0f, 3f),
            .. Wall(6f, 10f, 14f, 10f, 0f, 3f),
            .. Wall(16f, 10f, 20f, 10f, 0f, 3f)]);
        var from = new Vector3(5f, 5f, 0f);
        var to = new Vector3(5f, 15f, 0f);
        var leftDoor = new NavAvoidance(new Vector3(5f, 10f, 0f), 1.2f);
        var rightDoor = new NavAvoidance(new Vector3(15f, 10f, 0f), 1.2f);

        NavRoute direct = NavRouter.Find(grid, from, to, 1f);
        NavRoute around = NavRouter.Find(grid, from, to, 1f, [leftDoor]);
        NavRoute nowhere = NavRouter.Find(grid, from, to, 1f, [leftDoor, rightDoor]);

        Assert.Equal(NavRouteOutcome.Routed, direct.Outcome);
        Assert.DoesNotContain(direct.Path, point => point.X > 13f);
        Assert.Equal(NavRouteOutcome.Routed, around.Outcome);
        Assert.Contains(around.Path, point => point.X > 13f && MathF.Abs(point.Y - 10f) < 0.5f);
        Assert.All(around.Path, point =>
            Assert.True(Vector2.Distance(new Vector2(point.X, point.Y), new Vector2(5f, 10f)) > 1.2f));
        Assert.Equal(NavRouteOutcome.NoPath, nowhere.Outcome);
    }

    private static float DistanceToSegment(Vector2 point, Vector3 from, Vector3 to)
    {
        var start = new Vector2(from.X, from.Y);
        Vector2 along = new Vector2(to.X, to.Y) - start;
        float lengthSquared = along.LengthSquared();
        float t = lengthSquared > 0f ? Math.Clamp(Vector2.Dot(point - start, along) / lengthSquared, 0f, 1f) : 0f;
        return Vector2.Distance(point, start + (along * t));
    }

    private static NavGrid Build(NavTriangle[] triangles, NavCylinder[]? cylinders = null) =>
        NavGrid.Build(new NavGeometry(0f, 0f, 32f, [], triangles, [], cylinders ?? []), Body);

    private static NavTriangle[] Floor(float x0, float y0, float x1, float y1, float z) =>
        Quad(new Vector3(x0, y0, z), new Vector3(x1, y0, z), new Vector3(x1, y1, z), new Vector3(x0, y1, z));

    private static NavTriangle[] Ramp(float x0, float y0, float x1, float y1, float zAtY0, float zAtY1) =>
        Quad(new Vector3(x0, y0, zAtY0), new Vector3(x1, y0, zAtY0), new Vector3(x1, y1, zAtY1), new Vector3(x0, y1, zAtY1));

    private static NavTriangle[] Wall(float x0, float y0, float x1, float y1, float bottom, float top) =>
        Quad(new Vector3(x0, y0, bottom), new Vector3(x1, y1, bottom), new Vector3(x1, y1, top), new Vector3(x0, y0, top));

    private static NavTriangle[] Stairs(float x0, float x1, float yStart, float depth, float rise, int steps)
    {
        var triangles = new List<NavTriangle>();
        for (int step = 0; step < steps; step++)
        {
            float y = yStart + (step * depth);
            triangles.AddRange(Wall(x0, y, x1, y, step * rise, (step + 1) * rise));
            triangles.AddRange(Floor(x0, y, x1, y + depth, (step + 1) * rise));
        }
        return [.. triangles];
    }

    private static NavTriangle[] Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) =>
        [new NavTriangle(a, b, c), new NavTriangle(a, c, d)];
}
