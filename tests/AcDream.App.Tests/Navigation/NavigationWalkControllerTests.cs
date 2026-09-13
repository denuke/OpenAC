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
        Assert.InRange(
            Vector2.Distance(body.Flat, new Vector2(60f, 75f)),
            0f,
            NavigationWalkController.DefaultArrivalMeters + 0.75f);
        Assert.False(body.Travelling);
        Assert.InRange(MathF.Abs(body.HeadingErrorTo(new Vector2(60f, 75f))), 0f, 10.5f);
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
        walk.Tick();

        Assert.Equal(NavigationWalkState.Stopped, walk.Report.State);
        Assert.False(body.Travelling);
        Assert.False(walk.IsBusy);
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
        walk.Tick();

        Assert.Equal(NavigationWalkState.Interrupted, walk.Report.State);
        Assert.False(walk.IsBusy);
    }

    [Fact]
    public void ACharacterThatCannotMovePlansAgainAndThenGivesUp()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f)) { Stuck = true };
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(40f, 150f, 0f) });

        walk.WalkTo(Target);
        NavigationWalkReport report = RunUntilSettled(walk, body);

        Assert.Equal(NavigationWalkState.Blocked, report.State);
        Assert.Equal(NavigationWalkController.MaximumReplans, report.Replans);
        Assert.False(body.Travelling);
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
    public void AnObjectTheClientCannotPlaceHasNoRoute()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals());

        walk.WalkTo(Other);
        walk.Tick();

        Assert.Equal(NavigationWalkState.NoRoute, walk.Report.State);
        Assert.Contains("0x50000002", walk.Report.Reason);
    }

    [Fact]
    public void AGoalTooFarAwayToPlanForHasNoRoute()
    {
        var body = new SimulatedBody(new Vector3(40f, 40f, 0f));
        var walk = new NavigationWalkController(FlatWorld(), body, new Goals { [Target] = new Vector3(40f, 500f, 0f) });

        walk.WalkTo(Target);
        walk.Tick();

        Assert.Equal(NavigationWalkState.NoRoute, walk.Report.State);
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
            walk.Tick();
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
            walk.Tick();
            body.Integrate(Frame);
        }
    }

    private sealed class Goals : Dictionary<uint, Vector3>, INavigationGoalSource
    {
        public bool TryLocate(uint objectId, out Vector3 position) => TryGetValue(objectId, out position);
    }

    /// <summary>A body that carries out scripted moves the way the client does, at fixed speeds.</summary>
    private sealed class SimulatedBody : INavigationWalkBody
    {
        private const float RunSpeed = 4f;
        private const float WalkSpeed = 1.5f;
        private const float TurnSpeed = 180f;

        private long _sequence;
        private RuntimeMoveChannelSnapshot _travel;
        private RuntimeMoveChannelSnapshot _turn;
        private float _turnRemaining;

        public SimulatedBody(Vector3 position) => Position = position;

        public Vector3 Position { get; private set; }

        public float Heading { get; private set; }

        public bool Stuck { get; init; }

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
            if (!Stuck)
                Position += new Vector3(MathF.Sin(radians), MathF.Cos(radians), 0f) * speed * seconds;
            _travel = _travel with { ElapsedSeconds = _travel.ElapsedSeconds + seconds };
            if (Stuck && _travel.ElapsedSeconds > 1.5f)
                _travel = _travel with { State = RuntimeScriptedMoveState.Blocked };
        }
    }
}
