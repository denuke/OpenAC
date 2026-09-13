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
    public void AGoalTooFarAwayToPlanForHasNoRoute()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(40f, 500f, 0f) });

        walk.WalkTo(Target);
        walk.Tick(Frame);

        Assert.Equal(NavigationWalkState.NoRoute, walk.Report.State);
        Assert.Equal(460f, walk.Report.RemainingMeters, 1);
        Assert.Equal(0, body.MovesBegun);
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

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.Equal(1, doors.Uses);
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

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Arrived, report.State);
        Assert.Equal(1, doors.Uses);
        Assert.Equal(0, report.Replans);
        Assert.True(body.Position.Y > 60f);
    }

    private static PhysicsEngine FlatWorld()
    {
        var physics = new PhysicsEngine();
        physics.AddLandblock(0xA9B4FFFFu, new TerrainSurface(new byte[81], new float[256]), [], [], 0f, 0f);
        return physics;
    }

    private static NavigationWalkReport RunUntilSettled(NavigationWalkController walk, SimulatedBody body) =>
        RunUntil(walk, body, report => report.State is not (NavigationWalkState.Planning or NavigationWalkState.Walking));

    private static NavigationWalkReport RunUntil(
        NavigationWalkController walk,
        SimulatedBody body,
        Func<NavigationWalkReport, bool> done)
    {
        var wall = Stopwatch.StartNew();
        float simulated = 0f;
        while (simulated < 120f && wall.Elapsed < TimeSpan.FromSeconds(60))
        {
            walk.Tick(Frame);
            NavigationWalkReport report = walk.Report;
            if (done(report))
                return report;
            if (report.State == NavigationWalkState.Planning)
            {
                Thread.Sleep(1);
                continue;
            }
            body.Integrate(Frame);
            simulated += Frame;
        }
        return walk.Report;
    }

    private static void Run(NavigationWalkController walk, SimulatedBody body, float seconds)
    {
        for (float simulated = 0f; simulated < seconds; simulated += Frame)
        {
            walk.Tick(Frame);
            body.Integrate(Frame);
        }
    }

    private sealed class Goals : Dictionary<uint, Vector3>, INavigationGoalSource
    {
        public NavigationBlocker? Blocker { get; init; }

        public bool TryLocate(uint objectId, out Vector3 position) => TryGetValue(objectId, out position);

        public bool TryFindBlocker(Vector3 position, float radius, out NavigationBlocker blocker)
        {
            blocker = Blocker ?? default;
            return Blocker is not null;
        }
    }

    /// <summary>A single door that opens a few frames after it is used, or never.</summary>
    private sealed class FakeDoors : INavigationDoors
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

        public void Use(uint doorId)
        {
            Uses++;
            if (Opens)
                _pollsUntilOpen = 10;
        }
    }

    /// <summary>
    /// A body that carries out scripted moves the way the client does, at fixed
    /// speeds, sliding off a round obstacle and reporting a move blocked once it
    /// stops making progress.
    /// </summary>
    private sealed class SimulatedBody : INavigationWalkBody
    {
        private const float RunSpeed = 4f;
        private const float WalkSpeed = 1.5f;
        private const float TurnSpeed = 180f;
        private const float StallSeconds = 1.5f;

        private long _sequence;
        private RuntimeMoveChannelSnapshot _travel;
        private RuntimeMoveChannelSnapshot _turn;
        private float _turnRemaining;
        private float _stalledSeconds;

        public SimulatedBody(Vector3 position) => Position = position;

        public Vector3 Position { get; private set; }

        public float Heading { get; private set; }

        public bool Stuck { get; init; }

        public (Vector2 Centre, float Radius)? Obstacle { get; init; }

        /// <summary>Whether the obstacle is there, for one that can go away, such as a door that opens.</summary>
        public Func<bool>? ObstacleActive { get; init; }

        public int MovesBegun { get; private set; }

        public Vector2 Flat => new(Position.X, Position.Y);

        public bool Travelling => _travel.State == RuntimeScriptedMoveState.Moving;

        public bool TrySample(out NavigationWalkBodySample sample)
        {
            sample = new NavigationWalkBodySample(
                Position,
                Heading,
                NavBody.Player(0.6f, 1.5f),
                new RuntimeScriptedMoveSnapshot(_travel, default, _turn, 0, false),
                InPortalSpace: false);
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

        public float HeadingErrorTo(Vector2 target)
        {
            Vector2 toward = target - Flat;
            float heading = MathF.Atan2(toward.X, toward.Y) * (180f / MathF.PI);
            return ((((heading - Heading) % 360f) + 540f) % 360f) - 180f;
        }

        public void Integrate(float seconds)
        {
            if (_turn.State == RuntimeScriptedMoveState.Moving)
            {
                float step = MathF.Min(_turnRemaining, TurnSpeed * seconds);
                _turnRemaining -= step;
                float signed = _turn.Request.Direction == RuntimeMoveDirection.TurnRight ? step : -step;
                Heading = (((Heading + signed) % 360f) + 360f) % 360f;
                if (_turnRemaining <= 0f)
                    _turn = _turn with { State = RuntimeScriptedMoveState.Completed, Covered = _turn.Request.Amount };
            }
            if (_travel.State != RuntimeScriptedMoveState.Moving)
                return;

            float speed = _travel.Request.Pace == RuntimeMovePace.Run ? RunSpeed : WalkSpeed;
            float radians = Heading * MathF.PI / 180f;
            Vector3 intended = Position + (new Vector3(MathF.Sin(radians), MathF.Cos(radians), 0f) * speed * seconds);
            Vector3 next = Stuck ? Position : SlideOffObstacle(intended);
            float moved = Vector2.Distance(new Vector2(next.X, next.Y), Flat);
            Position = next;
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
