using System.Diagnostics;
using System.Numerics;
using AcDream.App.Navigation;
using AcDream.Core.Navigation;
using AcDream.Core.Physics;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Tests.Navigation;

public sealed class NavigationWalkControllerTests
{
    private const float Frame = 1f / 30f;
    private const uint Target = 0x50000001u;
    private const uint Other = 0x50000002u;
    private const uint Door = 0x7A000001u;

    /// <summary>How near a creature's middle the simulated body's middle can come, for a creature 0.8 m across its footprint's radius.</summary>
    private static readonly float Reach = 0.8f + NavBody.Player(0.6f, 1.5f).Radius;

    [Fact]
    public void AWalkIsPlannedWalkedToItsGoalAndEndsFacingIt()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(60f, 75f, 0f) });

        long sequence = walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);
        body.Integrate(3f);

        Assert.Equal(sequence, report.Sequence);
        Assert.Equal(NavigationWalkState.Arrived, report.State);
        float left = Vector2.Distance(body.Flat, new Vector2(60f, 75f));
        Assert.InRange(left, 0f, NavigationWalkController.DefaultArrivalMeters + 0.05f);
        Assert.Equal(left, report.RemainingMeters, 1);
        Assert.False(body.Travelling);
        Assert.InRange(MathF.Abs(body.HeadingErrorTo(new Vector2(60f, 75f))), 0f, 10.5f);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(4f)]
    public void AWalkEndsWithinTheDistanceItWasAskedToArriveWithin(float arrivalMeters)
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(60f, 75f, 0f) });

        walk.WalkTo(Target, arrivalMeters);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.InRange(report.RemainingMeters, 0f, arrivalMeters + 0.05f);
    }

    [Fact]
    public void AWalkWaitsWhileSomethingElseNeedsTheCharacterAndGoesOnFromWhereItWasLeft()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        string? need = null;
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(40f, 150f, 0f) })
        {
            PausedBy = () => need,
        };

        walk.WalkTo(Target);
        RunUntil(walk, body, _ => body.Position.Y > 60f);
        need = "MossTank is running Attack";
        walk.Tick(Frame);

        Assert.Equal(NavigationWalkState.Waiting, walk.Report.State);
        Assert.Equal("waiting: MossTank is running Attack", walk.Report.Reason);
        Assert.False(body.Travelling);
        body.Place(new Vector3(55f, 70f, 0f));
        Run(walk, body, 10f);
        Assert.Equal(NavigationWalkState.Waiting, walk.Report.State);
        Assert.Equal(new Vector2(55f, 70f), body.Flat);

        need = null;
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.Equal(0, report.Replans);
        Assert.InRange(
            Vector2.Distance(body.Flat, new Vector2(40f, 150f)),
            0f,
            NavigationWalkController.DefaultArrivalMeters + 0.05f);
    }

    [Fact]
    public void AWalkSetsOffAgainOnlyOnceNothingHasNeededTheCharacterForAMoment()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        string? need = null;
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(40f, 150f, 0f) })
        {
            PausedBy = () => need,
        };
        walk.WalkTo(Target);
        RunUntil(walk, body, report => report.State == NavigationWalkState.Walking);
        need = "MossTank is running Attack";
        Run(walk, body, 1f);
        int moves = body.MovesBegun;

        for (int gap = 0; gap < 5; gap++)
        {
            need = null;
            Run(walk, body, (float)NavigationWalkController.PauseSettleSeconds * 0.5f);
            need = "MossTank is running LootCorpseIdle";
            Run(walk, body, 0.2f);
        }

        Assert.Equal(NavigationWalkState.Waiting, walk.Report.State);
        Assert.Equal("waiting: MossTank is running LootCorpseIdle", walk.Report.Reason);
        Assert.Equal(moves, body.MovesBegun);
        need = null;
        Assert.Equal(NavigationWalkState.Arrived, RunUntilSettled(walk, body).State);
    }

    [Fact]
    public void ARouteAskedForAloneIsFoundWhileSomethingElseNeedsTheCharacter()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(60f, 75f, 0f) })
        {
            PausedBy = () => "MossTank is running Attack",
        };

        walk.RouteTo(Target);

        Assert.Equal(NavigationWalkState.Planned, RunUntilSettled(walk, body).State);
    }

    [Fact]
    public void APlaceKnownOnlyByItsMapCoordinatesLiesWhereItsCellPlacesIt()
    {
        var here = new Vector3(15.259f, -39.938f, 0.005f);
        Vector3 byCell = RuntimeNavigationGoalSource.PlaceOffset(0x019E0114u, new Vector3(10f, -40f, 0.005f), 0x019E0123u, here);
        Vector3 byMap = RuntimeNavigationGoalSource.PlaceOffset(0u, new Vector3(202f, 30296f, 0.005f), 0x019E0123u, here);

        Assert.Equal(-5.259d, byCell.X, 3);
        Assert.Equal(-0.062d, byCell.Y, 3);
        Assert.Equal(byCell.X, byMap.X, 2);
        Assert.Equal(byCell.Y, byMap.Y, 2);
        Assert.Equal(byCell.Z, byMap.Z, 3);
    }

    [Fact]
    public void AWalkToAPlaceArrivesThereWithoutTurningToFaceIt()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals());

        walk.WalkToPlace(0xA9B40001u, new Vector3(60f, 75f, 0f), 2f);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.Equal(0u, report.ObjectId);
        Assert.InRange(Vector2.Distance(body.Flat, new Vector2(60f, 75f)), 0f, 2.05f);
        Assert.False(body.Travelling);
    }

    [Fact]
    public void PlacesAreFoundOnceTheGroundAroundTheCharacterIsMapped()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals());

        walk.WantPlaces();
        Assert.Equal(NavigationPlacesState.Mapping, walk.Places.State);
        NavigationPlacesReport report = RunUntilPlaces(walk);

        Assert.Equal(NavigationPlacesState.Ready, report.State);
        Assert.False(report.InDungeon);
        Assert.Contains(report.Places, place => place.Kind == NavigationPlaceKind.Open);
        Assert.Equal(report.Places.OrderBy(place => place.WalkMeters), report.Places);
        Assert.Equal(new Vector3(40f, 40f, 0f), report.FromGlobal);
        Assert.Equal(0, body.MovesBegun);
    }

    [Fact]
    public void PlacesOutdoorsNameTheLoadedLandblocksBesideTheCharacters()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(FlatWorld(landblocksNorth: 2), body, new Goals());

        NavigationPlacesReport report = RunUntilPlaces(walk);

        NavigationPlace north = Assert.Single(report.Places, place => place.Kind == NavigationPlaceKind.Landblock);
        Assert.Equal(0xA9B5FFFFu, north.LandblockId);
        Assert.Equal(new Vector3(96f, 288f, 0f), north.Global);
        Assert.True(float.IsNaN(north.WalkMeters));
        Assert.False(north.IsWater);
    }

    [Fact]
    public void ARouteAskedForAloneIsFoundWithoutMovingTheCharacter()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(60f, 75f, 0f) });

        walk.RouteTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Planned, report.State);
        Assert.Equal(NavRouteOutcome.Routed, walk.Route!.Outcome);
        Assert.Equal(0, body.MovesBegun);
    }

    [Fact]
    public void StoppingAWalkEndsItAndStopsItsMoves()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(40f, 150f, 0f) });
        walk.WalkTo(Target);
        RunUntil(walk, body, report => report.State == NavigationWalkState.Walking);
        Run(walk, body, seconds: 1f);

        walk.Stop();
        walk.Tick(Frame);

        Assert.Equal(NavigationWalkState.Stopped, walk.Report.State);
        Assert.Equal(150f - body.Position.Y, walk.Report.RemainingMeters, 1);
        Assert.False(body.Travelling);
        Assert.False(walk.IsBusy);
    }

    [Fact]
    public void WhatIsLeftIsMeasuredToWhereTheObjectStandsWhenTheWalkEnds()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var goals = new Goals { [Target] = new Vector3(40f, 150f, 0f) };
        var walk = new NavigationWalkController(FlatWorld(), body, goals);
        walk.WalkTo(Target);
        RunUntil(walk, body, report => report.State == NavigationWalkState.Walking);

        goals[Target] = new Vector3(40f, 120f, 0f);
        walk.Stop();
        walk.Tick(Frame);

        Assert.Equal(NavigationWalkState.Stopped, walk.Report.State);
        Assert.Equal(120f - body.Position.Y, walk.Report.RemainingMeters, 1);
    }

    [Fact]
    public void ThePlayerMovingTheCharacterEndsTheWalk()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(40f, 150f, 0f) });
        walk.WalkTo(Target);
        RunUntil(walk, body, report => report.State == NavigationWalkState.Walking);
        Run(walk, body, seconds: 1f);

        body.Interrupt();
        walk.Tick(Frame);

        Assert.Equal(NavigationWalkState.Interrupted, walk.Report.State);
        Assert.False(walk.IsBusy);
    }

    [Fact]
    public void ACharacterThatCannotMovePlansAgainThenGivesUpNamingWhatBlockedIt()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f)) { Stuck = true };
        var goals = new Goals
        {
            [Target] = new Vector3(40f, 150f, 0f),
            Blocker = new NavigationBlocker(Door, "Door", IsClosedDoor: true),
        };
        var walk = new NavigationWalkController(FlatWorld(), body, goals);

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Blocked, report.State);
        Assert.Equal(NavigationWalkController.MaximumReplans, report.Replans);
        Assert.Equal(Door, report.BlockedByObjectId);
        Assert.Contains("a closed door, Door (0x7A000001)", report.Reason);
        Assert.Equal(110f, report.RemainingMeters, 1);
        Assert.False(body.Travelling);
    }

    [Fact]
    public void AWalkRunsThroughADoorwayWithoutStoppingWhereItsArcsKeepToTheFloor()
    {
        var turning = new RuntimeRouteTurning(RunSpeed: 4f, RunTurnDegreesPerSecond: 180f);
        var standing = new SimulatedBody(new Vector3(4f, 4f, -30f));
        var cutting = new SimulatedBody(new Vector3(4f, 4f, -30f)) { Turning = turning };
        var said = new List<string>();

        foreach (SimulatedBody body in new[] { standing, cutting })
        {
            said.Clear();
            var walk = new NavigationWalkController(RoomWithDoorways(10f), body, new Goals { [Target] = new Vector3(4f, 16f, -30f) }, said.Add);
            walk.WalkTo(Target);
            Assert.Equal(NavigationWalkState.Arrived, RunUntilSettled(walk, body).State);
        }

        Assert.True(standing.TravelStops > 1, $"turning in place, the walk stopped {standing.TravelStops} times");
        Assert.Equal(1, cutting.TravelStops);
        Assert.Contains(said, line => line.Contains("turned in place at 0", StringComparison.Ordinal) && !line.Contains("ran around 0 corners", StringComparison.Ordinal));
        for (int step = 1; step < cutting.Path.Count; step++)
        {
            Vector2 from = cutting.Path[step - 1];
            Vector2 to = cutting.Path[step];
            if ((from.Y < 10f) == (to.Y < 10f))
                continue;
            float crossing = from.X + ((to.X - from.X) * ((10f - from.Y) / (to.Y - from.Y)));
            Assert.InRange(crossing, 9f + NavGrid.BrushMargin, 11f - NavGrid.BrushMargin);
        }
    }

    [Fact]
    public void AWalkPlansAroundTheSpotWhereItWasBlocked()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f)) { Obstacle = (new Vector2(40f, 60f), 0.3f) };
        var goals = new Goals
        {
            [Target] = new Vector3(40f, 80f, 0f),
            Blocker = new NavigationBlocker(Other, "Barrel", IsClosedDoor: false),
        };
        var walk = new NavigationWalkController(FlatWorld(), body, goals);

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.InRange(report.Replans, 1, NavigationWalkController.MaximumReplans);
        Assert.Equal(0u, report.BlockedByObjectId);
        Assert.InRange(Vector2.Distance(body.Flat, new Vector2(40f, 80f)), 0f, NavigationWalkController.DefaultArrivalMeters + 0.75f);
    }

    [Fact]
    public void AWalkBlockedBesideAnObjectPlansAroundAllOfItAfterward()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f)) { Obstacle = (new Vector2(40f, 60f), 1.5f) };
        var goals = new Goals
        {
            [Target] = new Vector3(40f, 80f, 0f),
            Blocker = new NavigationBlocker(Other, "Ore Deposit", IsClosedDoor: false, new Vector3(40f, 60f, 0f), 1.5f),
        };
        var walk = new NavigationWalkController(FlatWorld(), body, goals);

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.Equal(1, report.Replans);
        NavRoute route = walk.Route!;
        Assert.All(route.Legs.Skip(1), end =>
            Assert.True(Vector2.Distance(new Vector2(end.X, end.Y), new Vector2(40f, 60f)) >= 1.9f, "a leg ends inside the deposit"));
        for (int leg = 2; leg < route.Legs.Count; leg++)
        {
            var start = new Vector2(route.Legs[leg - 1].X, route.Legs[leg - 1].Y);
            Vector2 along = new Vector2(route.Legs[leg].X, route.Legs[leg].Y) - start;
            float t = along.LengthSquared() > 0f
                ? Math.Clamp(Vector2.Dot(new Vector2(40f, 60f) - start, along) / along.LengthSquared(), 0f, 1f)
                : 0f;
            Assert.True(Vector2.Distance(new Vector2(40f, 60f), start + (along * t)) >= 1.5f, $"leg {leg} passes through the deposit");
        }
    }

    [Fact]
    public void AWalkBlockedBesideAnObjectWithNoOtherWayEndsBlockedNamingItAtOnce()
    {
        var body = new SimulatedBody(new Vector3(5f, 5f, -30f)) { Obstacle = (new Vector2(5f, 10f), 0.8f) };
        var goals = new Goals
        {
            [Target] = new Vector3(5f, 15f, -30f),
            Blocker = new NavigationBlocker(Other, "Ore Deposit", IsClosedDoor: false, new Vector3(5f, 10f, -30f), 0.8f),
        };
        var walk = new NavigationWalkController(RoomWithDoorways(5f), body, goals);

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Blocked, report.State);
        Assert.Equal(1, report.Replans);
        Assert.Equal(Other, report.BlockedByObjectId);
        Assert.Contains("Ore Deposit", report.Reason);
        Assert.Contains("stands in the way, and no other way around it was found", report.Reason);
    }

    [Fact]
    public void AWalkPlansAroundACreatureInItsWayAndKeepsClearOfIt()
    {
        var creature = new Vector2(40f, 70f);
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f)) { Obstacle = (creature, Reach) };
        var goals = new Goals
        {
            [Target] = new Vector3(40f, 100f, 0f),
            CrowdNow = () => [new NavAvoidance(new Vector3(creature, 0f), 0.8f)],
        };
        var walk = new NavigationWalkController(FlatWorld(), body, goals);
        float nearest = float.PositiveInfinity;

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntil(walk, body, report =>
        {
            nearest = MathF.Min(nearest, Vector2.Distance(body.Flat, creature));
            return report.State is not (NavigationWalkState.Planning or NavigationWalkState.Walking or NavigationWalkState.Waiting);
        });

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.Equal(0, report.Replans);
        Assert.True(nearest >= Reach - 0.05f, $"the walk came within {nearest:0.00} m of the creature");
    }

    [Fact]
    public void AWalkStepsAroundACreatureThatWalksOntoItsRouteWithoutStopping()
    {
        var creature = new Vector2(40f, 80f);
        bool there = false;
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f)) { Obstacle = (creature, Reach), ObstacleActive = () => there };
        var goals = new Goals
        {
            [Target] = new Vector3(40f, 120f, 0f),
            CrowdNow = () => there ? [new NavAvoidance(new Vector3(creature, 0f), 0.8f)] : [],
        };
        var walk = new NavigationWalkController(FlatWorld(), body, goals);
        float nearest = float.PositiveInfinity;
        int longestStill = 0;
        bool planning = false;

        walk.WalkTo(Target);
        RunUntil(walk, body, report => report.State == NavigationWalkState.Walking && body.Position.Y > 60f);
        there = true;
        NavigationWalkReport report = RunUntil(walk, body, report =>
        {
            nearest = MathF.Min(nearest, Vector2.Distance(body.Flat, creature));
            longestStill = Math.Max(longestStill, body.StillFrames);
            planning |= report.State == NavigationWalkState.Planning;
            return report.State is not (NavigationWalkState.Planning or NavigationWalkState.Walking or NavigationWalkState.Waiting);
        });

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.Equal(0, report.Replans);
        Assert.False(planning, "the walk stopped to plan again");
        Assert.True(nearest >= Reach - 0.05f, $"the walk came within {nearest:0.00} m of the creature");
        Assert.True(longestStill <= 10, $"the walk stood still for {longestStill} frames");
    }

    [Fact]
    public void AWalkStoppedByACreatureWaitsForItToMoveAsideAndGoesOn()
    {
        bool movedAside = false;
        var body = new SimulatedBody(new Vector3(5f, 5f, -30f))
        {
            Obstacle = (new Vector2(5f, 10f), Reach),
            ObstacleActive = () => !movedAside,
        };
        var goals = new Goals
        {
            [Target] = new Vector3(5f, 15f, -30f),
            Blocker = new NavigationBlocker(Other, "Drudge Skulker", IsClosedDoor: false, new Vector3(5f, 10f, -30f), 0.8f, Moves: true),
            CrowdNow = () => movedAside ? [] : [new NavAvoidance(new Vector3(5f, 10f, -30f), 0.8f)],
        };
        var walk = new NavigationWalkController(RoomWithDoorways(5f), body, goals);

        walk.WalkTo(Target);
        NavigationWalkReport waiting = RunUntil(walk, body, report => report.Reason.Contains("waiting for it to move"));
        movedAside = true;
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Contains("Drudge Skulker", waiting.Reason);
        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.Equal(0, report.Replans);
    }

    [Fact]
    public void AWalkThatACreatureNeverLetsByEndsBlockedNamingIt()
    {
        var body = new SimulatedBody(new Vector3(5f, 8f, -30f)) { Stuck = true };
        var goals = new Goals
        {
            [Target] = new Vector3(5f, 15f, -30f),
            Blocker = new NavigationBlocker(Other, "Drudge Skulker", IsClosedDoor: false, new Vector3(5f, 10f, -30f), 0.8f, Moves: true),
            CrowdNow = () => [new NavAvoidance(new Vector3(5f, 10f, -30f), 0.8f)],
        };
        var walk = new NavigationWalkController(RoomWithDoorways(5f), body, goals);

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Blocked, report.State);
        Assert.Equal(Other, report.BlockedByObjectId);
        Assert.Contains("Drudge Skulker", report.Reason);
    }

    [Fact]
    public void AWalkStoppedByAHostileMonsterPlansAroundItAtOnceInsteadOfWaitingForItToMove()
    {
        var body = new SimulatedBody(new Vector3(5f, 8f, -30f)) { Stuck = true };
        var goals = new Goals
        {
            [Target] = new Vector3(5f, 15f, -30f),
            Blocker = new NavigationBlocker(Other, "Drudge Skulker", IsClosedDoor: false, new Vector3(5f, 10f, -30f), 0.8f, Moves: true, Hostile: true),
            CrowdNow = () => [new NavAvoidance(new Vector3(5f, 10f, -30f), 0.8f)],
        };
        var walk = new NavigationWalkController(RoomWithDoorways(5f), body, goals);

        walk.WalkTo(Target);
        NavigationWalkReport first = RunUntil(
            walk,
            body,
            report => report.Reason.Contains("waiting for it to move") || report.Reason.Contains("planning again"));
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Contains("planning again", first.Reason);
        Assert.Equal(NavigationWalkState.Blocked, report.State);
        Assert.Equal(Other, report.BlockedByObjectId);
        Assert.Contains("a hostile monster, Drudge Skulker", report.Reason);
    }

    [Fact]
    public void ANewerRequestReplacesTheWalkUnderWay()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var goals = new Goals { [Target] = new Vector3(40f, 150f, 0f), [Other] = new Vector3(70f, 40f, 0f) };
        var walk = new NavigationWalkController(FlatWorld(), body, goals);
        walk.WalkTo(Target);
        RunUntil(walk, body, report => report.State == NavigationWalkState.Walking);

        long second = walk.WalkTo(Other);
        NavigationWalkReport settled = RunUntilSettled(walk, body);

        Assert.Equal(second, settled.Sequence);
        Assert.Equal(NavigationWalkState.Arrived, settled.State);
        Assert.InRange(Vector2.Distance(body.Flat, new Vector2(70f, 40f)), 0f, NavigationWalkController.DefaultArrivalMeters + 0.75f);
    }

    [Fact]
    public void AnObjectTheClientCannotPlaceHasNoRouteAndNoDistance()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals());

        walk.WalkTo(Other);
        walk.Tick(Frame);

        Assert.Equal(NavigationWalkState.NoRoute, walk.Report.State);
        Assert.Contains("0x50000002", walk.Report.Reason);
        Assert.True(float.IsNaN(walk.Report.RemainingMeters));
    }

    [Fact]
    public void AGoalTooFarAwayForOneRegionIsWalkedToInStages()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(
            FlatWorld(landblocksNorth: 3),
            body,
            new Goals { [Target] = new Vector3(60f, 520f, 0f) });

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body, seconds: 300f);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.InRange(
            Vector2.Distance(body.Flat, new Vector2(60f, 520f)),
            0f,
            NavigationWalkController.DefaultArrivalMeters + 0.05f);
        Assert.Equal(0, report.Replans);
    }

    [Fact]
    public void AWalkTowardAGoalBeyondTheLoadedWorldEndsWhereItCanGetNoNearer()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(40f, 900f, 0f) });

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body, seconds: 200f);

        Assert.Equal(NavigationWalkState.NoRoute, report.State);
        Assert.Contains("no way on toward the goal", report.Reason);
        Assert.True(body.Position.Y > 150f);
    }

    [Fact]
    public void AStageRegionHoldsTheCharacterAndReachesTowardTheGoal()
    {
        var from = new Vector3(500f, 500f, 0f);
        float margin = NavigationWalkController.RegionMargin - NavGrid.DefaultCellSize;
        foreach (Vector3 goal in new[] { new Vector3(1500f, 500f, 0f), new Vector3(-400f, -600f, 0f), new Vector3(700f, 1400f, 0f) })
        {
            NavigationWalkController.ChooseStageRegion(from, goal, out float originX, out float originY, out float size);

            Assert.Equal(NavigationWalkController.MaximumRegion, size);
            Assert.InRange(from.X - originX, margin, size - margin);
            Assert.InRange(from.Y - originY, margin, size - margin);
            var centre = new Vector2(originX + (size * 0.5f), originY + (size * 0.5f));
            var flatGoal = new Vector2(goal.X, goal.Y);
            Assert.True(Vector2.Distance(centre, flatGoal) < Vector2.Distance(new Vector2(from.X, from.Y), flatGoal) - 100f);
        }
    }

    [Fact]
    public void InsideASealedDungeonOneGridCoversEveryCellAndServesEveryWalkThere()
    {
        const uint room = 0xA9B40100u;
        var body = new SimulatedBody(new Vector3(20f, 20f, -30f)) { CellId = room };
        var goals = new Goals
        {
            [Target] = new Vector3(60f, 32f, -30f),
            [Other] = new Vector3(245f, 32f, -30f),
        };
        var walk = new NavigationWalkController(DungeonWorld(room), body, goals, isSealedDungeon: cell => cell == room);

        walk.WalkTo(Target);
        Assert.Equal(NavigationWalkState.Arrived, RunUntilSettled(walk, body).State);
        NavGrid? first = walk.Grid;
        walk.WalkTo(Other);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.Same(first, walk.Grid);
        Assert.True(first!.Contains(new Vector3(10f, 10f, -30f)) && first.Contains(new Vector3(250f, 50f, -30f)));
    }

    [Fact]
    public void ADungeonWiderThanAnyOutdoorRegionGetsOneGridWithoutTerrain()
    {
        const uint room = 0xA9B40100u;
        var body = new SimulatedBody(new Vector3(20f, 20f, -30f)) { CellId = room };
        var goals = new Goals { [Target] = new Vector3(645f, 32f, -30f) };
        var walk = new NavigationWalkController(
            DungeonWorld(room, corridorEnd: 630f),
            body,
            goals,
            isSealedDungeon: cell => cell == room);

        walk.RouteTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Planned, report.State);
        Assert.Equal("a route was found", report.Reason);
        NavGrid grid = walk.Grid!;
        Assert.True(grid.Size > 2f * NavigationWalkController.MaximumRegion);
        Assert.Equal([0xA9B4FFFFu], grid.LandblockIds);
        Assert.True(grid.FindNode(new Vector3(100f, 150f, 0f), 2f, 2f) < 0);
    }

    [Fact]
    public void AGridShownInsideASealedDungeonCoversTheWholeDungeonAndServesItsWalks()
    {
        const uint room = 0xA9B40100u;
        var body = new SimulatedBody(new Vector3(20f, 20f, -30f)) { CellId = room };
        var goals = new Goals { [Target] = new Vector3(245f, 32f, -30f) };
        var walk = new NavigationWalkController(DungeonWorld(room), body, goals, isSealedDungeon: cell => cell == room)
        {
            ShowGrid = true,
        };

        var wall = Stopwatch.StartNew();
        while (walk.Grid is null && wall.Elapsed < TimeSpan.FromSeconds(30))
        {
            walk.Tick(Frame);
            Thread.Sleep(1);
        }
        NavGrid? shown = walk.Grid;
        walk.RouteTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.NotNull(shown);
        Assert.True(shown.Contains(new Vector3(10f, 10f, -30f)) && shown.Contains(new Vector3(250f, 50f, -30f)));
        Assert.Equal(NavigationWalkState.Planned, report.State);
        Assert.Same(shown, walk.Grid);
    }

    [Fact]
    public void AGoalFarOutsideTheSealedDungeonIsReportedWithoutBuildingAGrid()
    {
        const uint room = 0xA9B40100u;
        var body = new SimulatedBody(new Vector3(20f, 20f, -30f)) { CellId = room };
        var goals = new Goals { [Target] = new Vector3(5000f, 32f, -30f) };
        var walk = new NavigationWalkController(DungeonWorld(room), body, goals, isSealedDungeon: cell => cell == room);

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.NoRoute, report.State);
        Assert.Contains("outside this dungeon", report.Reason);
        Assert.Null(walk.Grid);
    }

    [Fact]
    public void AWalkPlansAroundAnObjectTheServerPlacedWhenAnotherWayArrives()
    {
        var body = new SimulatedBody(new Vector3(5f, 5f, -30f));
        var goals = new Goals { [Target] = new Vector3(5f, 15f, -30f) };
        goals.Obstacles.Add(new NavAvoidance(new Vector3(5f, 10f, -30f), 0.8f));
        var walk = new NavigationWalkController(RoomWithDoorways(5f, 15f), body, goals);

        walk.RouteTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Planned, report.State);
        Assert.Contains(walk.Route!.Path, point => point.X > 13f && MathF.Abs(point.Y - 10f) < 0.5f);
    }

    [Fact]
    public void AWalkPlansThroughAnObjectTheServerPlacedWhenNoOtherWayArrives()
    {
        var body = new SimulatedBody(new Vector3(5f, 5f, -30f));
        var goals = new Goals { [Target] = new Vector3(5f, 15f, -30f) };
        goals.Obstacles.Add(new NavAvoidance(new Vector3(5f, 10f, -30f), 0.8f));
        var walk = new NavigationWalkController(RoomWithDoorways(5f), body, goals);

        walk.RouteTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Planned, report.State);
        Assert.Contains(walk.Route!.Path, point => MathF.Abs(point.X - 5f) < 1f && MathF.Abs(point.Y - 10f) < 0.5f);
    }

    [Fact]
    public void APlanningRegionHoldsBothEndsWithRoomAroundThem()
    {
        var from = new Vector3(10f, 10f, 0f);
        var to = new Vector3(130f, 40f, 0f);

        Assert.True(NavigationWalkController.TryChooseRegion(from, to, out float originX, out float originY, out float size));

        float margin = NavigationWalkController.RegionMargin - NavGrid.DefaultCellSize;
        foreach (Vector3 end in new[] { from, to })
        {
            Assert.InRange(end.X - originX, margin, size - margin);
            Assert.InRange(end.Y - originY, margin, size - margin);
        }
        Assert.False(NavigationWalkController.TryChooseRegion(from, new Vector3(400f, 10f, 0f), out _, out _, out _));
    }

    [Fact]
    public void AClosedDoorOnTheRouteIsOpenedAndWalkedThrough()
    {
        var doors = new FakeDoors(new NavigationDoor(Door, "Door"), new Vector2(40f, 60f), opens: true);
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f))
        {
            Obstacle = (new Vector2(40f, 60f), 0.3f),
            ObstacleActive = () => !doors.Open,
        };
        var walk = new NavigationWalkController(
            FlatWorld(),
            body,
            new Goals { [Target] = new Vector3(40f, 80f, 0f) },
            doors: doors);
        doors.AcceptsUse = () => body.StillFrames >= 1;

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.Equal(1, doors.Uses);
        Assert.Equal(0, doors.DroppedUses);
        Assert.Equal(0, report.Replans);
        Assert.True(body.Position.Y > 60f);
    }

    [Fact]
    public void ADoorThatWillNotOpenEndsTheWalkBlockedNamingIt()
    {
        var doors = new FakeDoors(new NavigationDoor(Door, "Door"), new Vector2(40f, 60f), opens: false);
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f)) { Obstacle = (new Vector2(40f, 60f), 0.3f) };
        var walk = new NavigationWalkController(
            FlatWorld(),
            body,
            new Goals { [Target] = new Vector3(40f, 80f, 0f) },
            doors: doors);

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Blocked, report.State);
        Assert.Equal(Door, report.BlockedByObjectId);
        Assert.Contains("would not open: Door (0x7A000001)", report.Reason);
        Assert.Equal(1, doors.Uses);
        Assert.False(body.Travelling);
    }

    [Fact]
    public void ALockedDoorIsWalkedAroundWithoutBeingTriedWhenAnotherWayArrives()
    {
        var doors = new FakeDoors(new NavigationDoor(Door, "Door", new Vector3(5f, 10f, -30f), 0.5f), new Vector2(5f, 10f), opens: false)
        {
            LockedWhenAppraised = true,
        };
        var body = new SimulatedBody(new Vector3(5f, 5f, -30f)) { Obstacle = (new Vector2(5f, 10f), 0.5f) };
        var walk = new NavigationWalkController(
            RoomWithDoorways(5f, 15f),
            body,
            new Goals { [Target] = new Vector3(5f, 15f, -30f) },
            doors: doors);

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.Equal(1, doors.Appraisals);
        Assert.Equal(0, doors.Uses);
        Assert.True(body.Position.Y > 12f);
    }

    [Fact]
    public void ALockedDoorOnTheOnlyWayEndsTheWalkBlockedNamingItWithoutTryingIt()
    {
        var doors = new FakeDoors(new NavigationDoor(Door, "Door", new Vector3(5f, 10f, -30f), 0.5f), new Vector2(5f, 10f), opens: false)
        {
            LockedWhenAppraised = true,
        };
        var body = new SimulatedBody(new Vector3(5f, 5f, -30f)) { Obstacle = (new Vector2(5f, 10f), 0.5f) };
        var walk = new NavigationWalkController(
            RoomWithDoorways(5f),
            body,
            new Goals { [Target] = new Vector3(5f, 15f, -30f) },
            doors: doors);

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Blocked, report.State);
        Assert.Equal(Door, report.BlockedByObjectId);
        Assert.Contains("a locked door: Door (0x7A000001), and no other way around it was found", report.Reason);
        Assert.Equal(0, doors.Uses);
    }

    [Fact]
    public void ADoorThatWillNotOpenIsWalkedAroundWhenAnotherWayArrives()
    {
        var doors = new FakeDoors(new NavigationDoor(Door, "Door", new Vector3(5f, 10f, -30f), 0.5f), new Vector2(5f, 10f), opens: false);
        var body = new SimulatedBody(new Vector3(5f, 5f, -30f)) { Obstacle = (new Vector2(5f, 10f), 0.5f) };
        var walk = new NavigationWalkController(
            RoomWithDoorways(5f, 15f),
            body,
            new Goals { [Target] = new Vector3(5f, 15f, -30f) },
            doors: doors);
        doors.AcceptsUse = () => body.StillFrames >= 1;

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.Equal(1, doors.Uses);
        Assert.True(body.Position.Y > 12f);
    }

    [Fact]
    public void AWalkThatStopsBesideAClosedDoorItDidNotSeeAheadOpensIt()
    {
        var doors = new FakeDoors(new NavigationDoor(Door, "Door"), new Vector2(40f, 60f), opens: true) { SeenAhead = false };
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f))
        {
            Obstacle = (new Vector2(40f, 60f), 0.3f),
            ObstacleActive = () => !doors.Open,
        };
        var goals = new Goals
        {
            [Target] = new Vector3(40f, 80f, 0f),
            Blocker = new NavigationBlocker(Door, "Door", IsClosedDoor: true),
        };
        var walk = new NavigationWalkController(FlatWorld(), body, goals, doors: doors);
        doors.AcceptsUse = () => body.StillFrames >= 1;

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.Equal(1, doors.Uses);
        Assert.Equal(0, report.Replans);
        Assert.True(body.Position.Y > 60f);
    }

    private static PhysicsEngine FlatWorld(int landblocksNorth = 1)
    {
        var physics = new PhysicsEngine();
        for (int index = 0; index < landblocksNorth; index++)
        {
            physics.AddLandblock(
                0xA9B4FFFFu + ((uint)index << 16),
                new TerrainSurface(new byte[81], new float[256]),
                [],
                [],
                0f,
                index * 192f);
        }
        return physics;
    }

    /// <summary>
    /// A room 20 m square, 30 m below its landblock's terrain, split along y = 10 by
    /// a wall 3 m high with a doorway 2 m wide centred at each x in <paramref name="doorways"/>.
    /// </summary>
    private static PhysicsEngine RoomWithDoorways(params float[] doorways)
    {
        const float floor = -30f;
        const float top = -27f;
        var vertices = new Dictionary<ushort, Vector3>
        {
            [0] = new(0f, 0f, floor),
            [1] = new(20f, 0f, floor),
            [2] = new(20f, 20f, floor),
            [3] = new(0f, 20f, floor),
        };
        var polygons = new List<List<short>> { new() { 0, 1, 2, 3 } };
        float start = 0f;
        foreach (float end in doorways.Order().Select(centre => centre - 1f).Append(20f))
        {
            if (end > start)
            {
                short first = (short)vertices.Count;
                vertices[(ushort)first] = new Vector3(start, 10f, floor);
                vertices[(ushort)(first + 1)] = new Vector3(end, 10f, floor);
                vertices[(ushort)(first + 2)] = new Vector3(end, 10f, top);
                vertices[(ushort)(first + 3)] = new Vector3(start, 10f, top);
                polygons.Add([first, (short)(first + 1), (short)(first + 2), (short)(first + 3)]);
            }
            start = end + 2f;
        }

        var physics = new PhysicsEngine();
        physics.AddLandblock(
            0xA9B4FFFFu,
            new TerrainSurface(new byte[81], new float[256]),
            [new CellSurface(0xA9B40100u, vertices, polygons)],
            [],
            0f,
            0f);
        return physics;
    }

    /// <summary>
    /// A landblock holding a dungeon 30 m below its terrain: two rooms joined by a
    /// corridor, 190 m long unless <paramref name="corridorEnd"/> moves the far room.
    /// </summary>
    private static PhysicsEngine DungeonWorld(uint firstCell, float corridorEnd = 230f)
    {
        static CellSurface Floor(uint cellId, float x0, float y0, float x1, float y1) => new(
            cellId,
            new Dictionary<ushort, Vector3>
            {
                [0] = new(x0, y0, -30f),
                [1] = new(x1, y0, -30f),
                [2] = new(x1, y1, -30f),
                [3] = new(x0, y1, -30f),
            },
            [[0, 1, 2, 3]]);

        var physics = new PhysicsEngine();
        physics.AddLandblock(
            0xA9B4FFFFu,
            new TerrainSurface(new byte[81], new float[256]),
            [
                Floor(firstCell, 10f, 10f, 40f, 50f),
                Floor(firstCell + 1u, 40f, 28f, corridorEnd, 36f),
                Floor(firstCell + 2u, corridorEnd, 20f, corridorEnd + 20f, 50f),
            ],
            [],
            0f,
            0f);
        return physics;
    }

    private static NavigationWalkReport RunUntilSettled(NavigationWalkController walk, SimulatedBody body, float seconds = 120f) =>
        RunUntil(
            walk,
            body,
            report => report.State is not (NavigationWalkState.Planning or NavigationWalkState.Walking or NavigationWalkState.Waiting),
            seconds);

    private static NavigationWalkReport RunUntil(
        NavigationWalkController walk,
        SimulatedBody body,
        Func<NavigationWalkReport, bool> done,
        float seconds = 120f)
    {
        var wall = Stopwatch.StartNew();
        float simulated = 0f;
        while (simulated < seconds && wall.Elapsed < TimeSpan.FromSeconds(120))
        {
            walk.Tick(Frame);
            NavigationWalkReport report = walk.Report;
            if (done(report))
                return report;
            if (report.State == NavigationWalkState.Planning || walk.IsSearching)
            {
                Thread.Sleep(1);
                continue;
            }
            body.Integrate(Frame);
            simulated += Frame;
        }
        return walk.Report;
    }

    private static NavigationPlacesReport RunUntilPlaces(NavigationWalkController walk)
    {
        walk.WantPlaces();
        NavigationPlacesReport report = walk.Places;
        var wall = Stopwatch.StartNew();
        while (report.State != NavigationPlacesState.Ready && wall.Elapsed < TimeSpan.FromSeconds(60))
        {
            walk.WantPlaces();
            walk.Tick(Frame);
            report = walk.Places;
            Thread.Sleep(1);
        }
        return report;
    }

    private static void Run(NavigationWalkController walk, SimulatedBody body, float seconds)
    {
        for (float simulated = 0f; simulated < seconds; simulated += Frame)
        {
            walk.Tick(Frame);
            body.Integrate(Frame);
        }
    }

    internal sealed class Goals : Dictionary<uint, Vector3>, INavigationGoalSource
    {
        public bool TryGlobalOf(Vector3 world, out Vector3 global)
        {
            global = world;
            return true;
        }

        public NavigationBlocker? Blocker { get; init; }

        /// <summary>The objects the server placed, as a walk asks for them.</summary>
        public List<NavAvoidance> Obstacles { get; } = [];

        public bool TryLocate(uint objectId, out Vector3 position) => TryGetValue(objectId, out position);

        public IReadOnlyList<NavAvoidance> FindObstacles(Vector3 around, float radius, uint goalObjectId) => Obstacles;

        /// <summary>The creatures and players standing around, as a walk asks for them now.</summary>
        public Func<IReadOnlyList<NavAvoidance>>? CrowdNow { get; init; }

        public IReadOnlyList<NavAvoidance> FindCrowd(Vector3 around, float radius, uint goalObjectId) => CrowdNow?.Invoke() ?? [];

        /// <summary>Places stand where their landblock-local point says, the test world having one landblock at its origin.</summary>
        public bool TryLocatePlace(uint cellId, Vector3 local, out Vector3 position)
        {
            position = local;
            return true;
        }

        public bool TryFindBlocker(Vector3 position, float radius, out NavigationBlocker blocker)
        {
            blocker = Blocker ?? default;
            return Blocker is not null;
        }
    }

    /// <summary>A single door that opens a few frames after it is used, or never.</summary>
    internal sealed class FakeDoors : INavigationDoors
    {
        private int _pollsUntilOpen = -1;

        public FakeDoors(NavigationDoor door, Vector2 at, bool opens)
        {
            Door = door;
            At = at;
            Opens = opens;
        }

        public NavigationDoor Door { get; }

        public Vector2 At { get; }

        public bool Opens { get; }

        /// <summary>Whether a walk looking ahead along its leg finds the door.</summary>
        public bool SeenAhead { get; init; } = true;

        public bool Open { get; private set; }

        public int Uses { get; private set; }

        public bool TryFindClosedDoor(Vector3 from, Vector3 to, float corridor, out NavigationDoor door)
        {
            door = Door;
            if (Open || !SeenAhead)
                return false;
            var start = new Vector2(from.X, from.Y);
            Vector2 along = new Vector2(to.X, to.Y) - start;
            float lengthSquared = along.LengthSquared();
            float t = lengthSquared > 1e-6f ? Vector2.Dot(At - start, along) / lengthSquared : 0f;
            return t >= 0f && Vector2.Distance(At, start + (along * MathF.Min(t, 1f))) <= corridor;
        }

        public bool IsOpen(uint doorId)
        {
            if (_pollsUntilOpen > 0 && --_pollsUntilOpen == 0)
                Open = true;
            return Open;
        }

        /// <summary>
        /// Whether a use reaches the door. The client drops a use when a stop that
        /// lands after it cancels the walk into the door's use range.
        /// </summary>
        public Func<bool>? AcceptsUse { get; set; }

        public int DroppedUses { get; private set; }

        /// <summary>Whether an appraisal finds the door locked; null for a door the client cannot appraise.</summary>
        public bool? LockedWhenAppraised { get; init; }

        public int Appraisals { get; private set; }

        private bool? _locked;

        public bool? IsLocked(uint doorId) => _locked;

        public bool Appraise(uint doorId)
        {
            Appraisals++;
            _locked = LockedWhenAppraised;
            return LockedWhenAppraised is not null;
        }

        public void Use(uint doorId)
        {
            Uses++;
            if (AcceptsUse?.Invoke() == false)
            {
                DroppedUses++;
                return;
            }
            if (Opens)
                _pollsUntilOpen = 10;
        }
    }

    /// <summary>
    /// A body that carries out scripted moves the way the client does, at fixed
    /// speeds, sliding off a round obstacle and reporting a move blocked once it
    /// stops making progress.
    /// </summary>
    internal sealed class SimulatedBody : INavigationWalkBody
    {
        private const float StallSeconds = 1.5f;

        private long _sequence;
        private RuntimeMoveChannelSnapshot _travel;
        private RuntimeMoveChannelSnapshot _turn;
        private float _turnRemaining;
        private float _stalledSeconds;

        public SimulatedBody(Vector3 position) => Position = position;

        public float RunSpeed { get; init; } = 4f;

        public float WalkSpeed { get; init; } = 1.5f;

        /// <summary>Degrees a second the body turns standing or running.</summary>
        public float TurnSpeed { get; init; } = 180f;

        /// <summary>Degrees a second the body turns while it walks.</summary>
        public float WalkTurnSpeed { get; init; } = 180f;

        /// <summary>The frame time integrated, and how much of it the body spent walking.</summary>
        public double Seconds { get; private set; }

        public double WalkingSeconds { get; private set; }

        public Vector3 Position { get; private set; }

        public float Heading { get; private set; }

        public bool Stuck { get; init; }

        public uint CellId { get; init; }

        public (Vector2 Centre, float Radius)? Obstacle { get; init; }

        public RuntimeRouteTurning? Turning { get; init; }

        /// <summary>How many times a travel under way was stopped.</summary>
        public int TravelStops { get; private set; }

        /// <summary>Where the body stood after each frame it travelled.</summary>
        public List<Vector2> Path { get; } = [];

        /// <summary>Whether the obstacle is there, for one that can go away, such as a door that opens.</summary>
        public Func<bool>? ObstacleActive { get; init; }

        public int MovesBegun { get; private set; }

        /// <summary>Frames integrated since the body last travelled.</summary>
        public int StillFrames { get; private set; }

        public Vector2 Flat => new(Position.X, Position.Y);

        public bool Travelling => _travel.State == RuntimeScriptedMoveState.Moving;

        public bool TrySample(out NavigationWalkBodySample sample)
        {
            sample = new NavigationWalkBodySample(
                Position,
                Heading,
                NavBody.Player(0.6f, 1.5f),
                new RuntimeScriptedMoveSnapshot(_travel, default, _turn, 0, false),
                InPortalSpace: false,
                CellId: CellId,
                Turning: Turning);
            return true;
        }

        public bool BeginMove(in RuntimeMoveRequest request)
        {
            MovesBegun++;
            var begun = new RuntimeMoveChannelSnapshot(++_sequence, RuntimeScriptedMoveState.Moving, request, 0f, 0f);
            if (request.Direction is RuntimeMoveDirection.TurnLeft or RuntimeMoveDirection.TurnRight)
            {
                _turn = begun;
                _turnRemaining = request.Amount;
            }
            else
            {
                _travel = begun;
                _stalledSeconds = 0f;
            }
            return true;
        }

        public bool StopMove(RuntimeMoveChannel channel)
        {
            if (channel == RuntimeMoveChannel.Turn && _turn.State == RuntimeScriptedMoveState.Moving)
            {
                _turn = _turn with { State = RuntimeScriptedMoveState.Stopped };
                return true;
            }
            if (channel == RuntimeMoveChannel.Travel && _travel.State == RuntimeScriptedMoveState.Moving)
            {
                _travel = _travel with { State = RuntimeScriptedMoveState.Stopped };
                TravelStops++;
                return true;
            }
            return false;
        }

        public void Interrupt()
        {
            if (_travel.State == RuntimeScriptedMoveState.Moving)
                _travel = _travel with { State = RuntimeScriptedMoveState.Interrupted };
            if (_turn.State == RuntimeScriptedMoveState.Moving)
                _turn = _turn with { State = RuntimeScriptedMoveState.Interrupted };
        }

        /// <summary>Puts the body somewhere else, as something other than the walk moving it would.</summary>
        public void Place(Vector3 position) => Position = position;

        public float HeadingErrorTo(Vector2 target)
        {
            Vector2 toward = target - Flat;
            float heading = MathF.Atan2(toward.X, toward.Y) * (180f / MathF.PI);
            return ((((heading - Heading) % 360f) + 540f) % 360f) - 180f;
        }

        public void Integrate(float seconds)
        {
            Seconds += seconds;
            bool walking = _travel.State == RuntimeScriptedMoveState.Moving && _travel.Request.Pace == RuntimeMovePace.Walk;
            if (_turn.State == RuntimeScriptedMoveState.Moving)
            {
                float step = MathF.Min(_turnRemaining, (walking ? WalkTurnSpeed : TurnSpeed) * seconds);
                _turnRemaining -= step;
                float signed = _turn.Request.Direction == RuntimeMoveDirection.TurnRight ? step : -step;
                Heading = (((Heading + signed) % 360f) + 360f) % 360f;
                if (_turnRemaining <= 0f)
                    _turn = _turn with { State = RuntimeScriptedMoveState.Completed, Covered = _turn.Request.Amount };
            }
            if (_travel.State != RuntimeScriptedMoveState.Moving)
            {
                StillFrames++;
                return;
            }
            StillFrames = 0;
            if (walking)
                WalkingSeconds += seconds;

            float speed = _travel.Request.Pace == RuntimeMovePace.Run ? RunSpeed : WalkSpeed;
            float radians = Heading * MathF.PI / 180f;
            Vector3 intended = Position + (new Vector3(MathF.Sin(radians), MathF.Cos(radians), 0f) * speed * seconds);
            Vector3 next = Stuck ? Position : SlideOffObstacle(intended);
            float moved = Vector2.Distance(new Vector2(next.X, next.Y), Flat);
            Position = next;
            Path.Add(Flat);
            _stalledSeconds = moved < speed * seconds * 0.25f ? _stalledSeconds + seconds : 0f;
            _travel = _travel with { ElapsedSeconds = _travel.ElapsedSeconds + seconds };
            if (_stalledSeconds > StallSeconds)
                _travel = _travel with { State = RuntimeScriptedMoveState.Blocked };
        }

        private Vector3 SlideOffObstacle(Vector3 intended)
        {
            if (Obstacle is not { } obstacle || ObstacleActive?.Invoke() == false)
                return intended;
            Vector2 offset = new Vector2(intended.X, intended.Y) - obstacle.Centre;
            float distance = offset.Length();
            if (distance >= obstacle.Radius)
                return intended;
            Vector2 pushed = distance > 1e-4f
                ? obstacle.Centre + (offset / distance * obstacle.Radius)
                : Flat;
            return new Vector3(pushed, intended.Z);
        }
    }
}
