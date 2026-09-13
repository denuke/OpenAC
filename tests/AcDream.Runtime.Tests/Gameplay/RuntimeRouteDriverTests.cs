using System.Numerics;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeRouteDriverTests
{
    private const float Frame = 1f / 30f;

    [Fact]
    public void AStraightRouteIsRunToItsEnd()
    {
        var body = new SimulatedBody();
        var driver = new RuntimeRouteDriver([new Vector3(0f, 0f, 0f), new Vector3(0f, 20f, 0f)]);

        Drive(driver, body, seconds: 10f);

        Assert.Equal(RuntimeRouteDriveState.Arrived, driver.State);
        Assert.InRange(Vector2.Distance(body.Position, new Vector2(0f, 20f)), 0f, 0.6f);
        Assert.False(body.Travelling);
    }

    [Fact]
    public void ALegPointingBehindIsFacedBeforeRunning()
    {
        var body = new SimulatedBody { Heading = 180f };
        var driver = new RuntimeRouteDriver([new Vector3(0f, 0f, 0f), new Vector3(0f, 10f, 0f)]);

        RuntimeRouteDriveStep first = driver.Advance(body.Sample());

        Assert.Null(first.Travel);
        Assert.Equal(180f, first.Turn!.Value.Amount, 3);
        body.Apply(first);
        Drive(driver, body, seconds: 10f);
        Assert.Equal(RuntimeRouteDriveState.Arrived, driver.State);
    }

    [Fact]
    public void ACornerIsTurnedInPlaceAfterWalkingUpToIt()
    {
        var body = new SimulatedBody();
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 0f), new Vector3(0f, 10f, 0f), new Vector3(10f, 10f, 0f)]);

        List<RuntimeRouteDriveStep> steps = Drive(driver, body, seconds: 20f);

        Assert.Equal(RuntimeRouteDriveState.Arrived, driver.State);
        Assert.Contains(steps, step => step.Travel is { Pace: RuntimeMovePace.Walk });
        Assert.Contains(steps, step => step.StopTravel && step.Turn is { Direction: RuntimeMoveDirection.TurnRight });
        Assert.InRange(Vector2.Distance(body.Position, new Vector2(10f, 10f)), 0f, 0.6f);
    }

    [Fact]
    public void SmallDriftIsCorrectedWithoutStopping()
    {
        var body = new SimulatedBody { Heading = 10f };
        var driver = new RuntimeRouteDriver([new Vector3(0f, 0f, 0f), new Vector3(0f, 20f, 0f)]);

        RuntimeRouteDriveStep first = driver.Advance(body.Sample());

        Assert.Equal(new RuntimeMoveRequest(RuntimeMoveDirection.Forward, RuntimeMovePace.Run, 0f), first.Travel);
        Assert.Equal(RuntimeMoveDirection.TurnLeft, first.Turn!.Value.Direction);
        Assert.Equal(10f, first.Turn!.Value.Amount, 3);
        Assert.False(first.StopTravel);
    }

    [Fact]
    public void ALegTheBodyCannotMoveAlongEndsTheDriveAsBlocked()
    {
        var body = new SimulatedBody { Stuck = true };
        var driver = new RuntimeRouteDriver([new Vector3(0f, 0f, 0f), new Vector3(0f, 20f, 0f)]);

        List<RuntimeRouteDriveStep> steps = Drive(driver, body, seconds: 5f);

        Assert.Equal(RuntimeRouteDriveState.Blocked, driver.State);
        Assert.True(steps[^1].StopTravel || !body.Travelling);
    }

    [Fact]
    public void ThePlayerTakingOverEndsTheDrive()
    {
        var body = new SimulatedBody();
        var driver = new RuntimeRouteDriver([new Vector3(0f, 0f, 0f), new Vector3(0f, 20f, 0f)]);
        Drive(driver, body, seconds: 1f);

        body.Interrupt();
        RuntimeRouteDriveStep step = driver.Advance(body.Sample());

        Assert.Equal(RuntimeRouteDriveState.Interrupted, driver.State);
        Assert.True(step.IsEmpty);
    }

    [Fact]
    public void PortalSpaceLosesTheDrive()
    {
        var body = new SimulatedBody();
        var driver = new RuntimeRouteDriver([new Vector3(0f, 0f, 0f), new Vector3(0f, 20f, 0f)]);
        Drive(driver, body, seconds: 1f);

        body.InPortalSpace = true;
        RuntimeRouteDriveStep step = driver.Advance(body.Sample());

        Assert.Equal(RuntimeRouteDriveState.Lost, driver.State);
        Assert.True(step.StopTravel);
    }

    [Fact]
    public void ALongLegRenewsItsRunBeforeTheClientsLimit()
    {
        var body = new SimulatedBody();
        var driver = new RuntimeRouteDriver([new Vector3(0f, 0f, 0f), new Vector3(0f, 200f, 0f)]);

        List<RuntimeRouteDriveStep> steps = Drive(driver, body, seconds: 60f);

        Assert.Equal(RuntimeRouteDriveState.Arrived, driver.State);
        Assert.True(steps.Count(step => step.Travel is not null) >= 3);
        Assert.True(body.LongestTravelSeconds < 30f);
    }

    [Fact]
    public void AMoveEndedBeforeTheDriveBeganIsNotItsOwn()
    {
        var body = new SimulatedBody();
        body.EndTravelBeforeDrive(RuntimeScriptedMoveState.Blocked);
        var driver = new RuntimeRouteDriver([new Vector3(0f, 0f, 0f), new Vector3(0f, 20f, 0f)]);

        RuntimeRouteDriveStep first = driver.Advance(body.Sample());

        Assert.Equal(RuntimeRouteDriveState.Driving, driver.State);
        Assert.NotNull(first.Travel);
    }

    [Fact]
    public void CancelStopsWhatTheDriveBegan()
    {
        var body = new SimulatedBody();
        var driver = new RuntimeRouteDriver([new Vector3(0f, 0f, 0f), new Vector3(0f, 20f, 0f)]);
        Drive(driver, body, seconds: 1f);

        RuntimeRouteDriveStep step = driver.Cancel();

        Assert.True(step.StopTravel && step.StopTurn);
        Assert.Equal(RuntimeRouteDriveState.Interrupted, driver.State);
        Assert.True(driver.Advance(body.Sample()).IsEmpty);
    }

    private static List<RuntimeRouteDriveStep> Drive(RuntimeRouteDriver driver, SimulatedBody body, float seconds)
    {
        var steps = new List<RuntimeRouteDriveStep>();
        for (float time = 0f; time < seconds && driver.State == RuntimeRouteDriveState.Driving; time += Frame)
        {
            RuntimeRouteDriveStep step = driver.Advance(body.Sample());
            steps.Add(step);
            body.Apply(step);
            body.Integrate(Frame);
        }
        return steps;
    }

    /// <summary>A body that carries out scripted moves the way the client does, at fixed speeds.</summary>
    private sealed class SimulatedBody
    {
        private const float RunSpeed = 4f;
        private const float WalkSpeed = 1.5f;
        private const float TurnSpeed = 90f;

        private long _sequence;
        private RuntimeMoveChannelSnapshot _travel;
        private RuntimeMoveChannelSnapshot _turn;
        private float _turnRemaining;

        public Vector2 Position { get; private set; }

        public float Heading { get; set; }

        public bool Stuck { get; init; }

        public bool InPortalSpace { get; set; }

        public float LongestTravelSeconds { get; private set; }

        public bool Travelling => _travel.State == RuntimeScriptedMoveState.Moving;

        public RuntimeRouteDriveSample Sample() =>
            new(new Vector3(Position, 0f), Heading, new RuntimeScriptedMoveSnapshot(_travel, default, _turn, 0, false), InPortalSpace);

        public void EndTravelBeforeDrive(RuntimeScriptedMoveState state) =>
            _travel = new RuntimeMoveChannelSnapshot(
                ++_sequence,
                state,
                new RuntimeMoveRequest(RuntimeMoveDirection.Forward, RuntimeMovePace.Run, 0f),
                0f,
                0f);

        public void Interrupt()
        {
            if (_travel.State == RuntimeScriptedMoveState.Moving)
                _travel = _travel with { State = RuntimeScriptedMoveState.Interrupted };
            if (_turn.State == RuntimeScriptedMoveState.Moving)
                _turn = _turn with { State = RuntimeScriptedMoveState.Interrupted };
        }

        public void Apply(RuntimeRouteDriveStep step)
        {
            if (step.StopTravel && _travel.State == RuntimeScriptedMoveState.Moving)
                _travel = _travel with { State = RuntimeScriptedMoveState.Stopped };
            if (step.StopTurn && _turn.State == RuntimeScriptedMoveState.Moving)
                _turn = _turn with { State = RuntimeScriptedMoveState.Stopped };
            if (step.Travel is { } travel)
                _travel = new RuntimeMoveChannelSnapshot(++_sequence, RuntimeScriptedMoveState.Moving, travel, 0f, 0f);
            if (step.Turn is { } turn)
            {
                _turn = new RuntimeMoveChannelSnapshot(++_sequence, RuntimeScriptedMoveState.Moving, turn, 0f, 0f);
                _turnRemaining = turn.Amount;
            }
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
                Position += new Vector2(MathF.Sin(radians), MathF.Cos(radians)) * speed * seconds;
            _travel = _travel with { ElapsedSeconds = _travel.ElapsedSeconds + seconds };
            LongestTravelSeconds = MathF.Max(LongestTravelSeconds, _travel.ElapsedSeconds);
            if (Stuck && _travel.ElapsedSeconds > 1.5f)
                _travel = _travel with { State = RuntimeScriptedMoveState.Blocked };
        }
    }
}
