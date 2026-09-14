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

    [Fact]
    public void ALeapIsFacedChargedAndFlownFromItsTakeoffToItsLanding()
    {
        var body = new SimulatedBody { Floor = at => at.Y < 5.5f ? 3f : 0f };
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 3f), new Vector3(0f, 5f, 3f), new Vector3(0f, 6.7f, 0f), new Vector3(0f, 10f, 0f)],
            [new RuntimeRouteLeap(2, 0.1f, Run: false)]);

        List<RuntimeRouteDriveStep> steps = Drive(driver, body, seconds: 30f);

        Assert.Equal(RuntimeRouteDriveState.Arrived, driver.State);
        Assert.Equal(1, body.Jumps);
        RuntimeRouteDriveStep jump = Assert.Single(steps, step => step.Jump is not null);
        Assert.Equal(0.1f, jump.Jump!.Value, 3);
        Assert.Null(jump.Travel);
        Assert.InRange(Vector2.Distance(body.Position, new Vector2(0f, 10f)), 0f, 0.6f);
        Assert.Equal(0f, body.Height, 3);
    }

    [Fact]
    public void ALeapThatLandsFarFromItsLandingEndsTheDriveBlocked()
    {
        var body = new SimulatedBody { Floor = at => at.Y < 5.5f ? 3f : 0f };
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 3f), new Vector3(0f, 5f, 3f), new Vector3(0f, 12f, 0f), new Vector3(0f, 15f, 0f)],
            [new RuntimeRouteLeap(2, 0.1f, Run: false)]);

        Drive(driver, body, seconds: 30f);

        Assert.Equal(RuntimeRouteDriveState.Blocked, driver.State);
        Assert.Equal(1, body.Jumps);
    }

    [Fact]
    public void ALeapThatNeverLeavesTheGroundEndsTheDriveBlocked()
    {
        var body = new SimulatedBody { Floor = at => at.Y < 5.5f ? 3f : 0f, CannotJump = true };
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 3f), new Vector3(0f, 5f, 3f), new Vector3(0f, 6.7f, 0f), new Vector3(0f, 10f, 0f)],
            [new RuntimeRouteLeap(2, 0.1f, Run: false)]);

        Drive(driver, body, seconds: 30f);

        Assert.Equal(RuntimeRouteDriveState.Blocked, driver.State);
        Assert.False(driver.IsLeaping);
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

    /// <summary>
    /// A body that carries out scripted moves the way the client does, at fixed
    /// speeds. It stands on <see cref="Floor"/>, and a jump it charges while standing
    /// still leaves the ground at the pace of the move pressed during the charge.
    /// </summary>
    private sealed class SimulatedBody
    {
        private const float RunSpeed = 4f;
        private const float WalkSpeed = 1.5f;
        private const float TurnSpeed = 90f;
        private const float Gravity = 9.8f;
        private const float FullJumpHeight = 4.2f;

        private long _sequence;
        private RuntimeMoveChannelSnapshot _travel;
        private RuntimeMoveChannelSnapshot _turn;
        private float _turnRemaining;
        private float _chargeLeft = -1f;
        private float _chargePower;
        private Vector3 _velocity;
        private float _airHeight;

        public Vector2 Position { get; private set; }

        /// <summary>The height of the ground under a point; level ground at zero when unset.</summary>
        public Func<Vector2, float>? Floor { get; init; }

        public bool CannotJump { get; init; }

        public bool Airborne { get; private set; }

        public int Jumps { get; private set; }

        public float Height => Airborne ? _airHeight : Floor?.Invoke(Position) ?? 0f;

        public float Heading { get; set; }

        public bool Stuck { get; init; }

        public bool InPortalSpace { get; set; }

        public float LongestTravelSeconds { get; private set; }

        public bool Travelling => _travel.State == RuntimeScriptedMoveState.Moving;

        public RuntimeRouteDriveSample Sample() =>
            new(
                new Vector3(Position, Height),
                Heading,
                new RuntimeScriptedMoveSnapshot(_travel, default, _turn, 0, false),
                InPortalSpace,
                Airborne);

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
            if (step.Jump is { } power && !CannotJump && !Airborne)
            {
                _chargeLeft = power;
                _chargePower = power;
                Jumps++;
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
            if (Airborne)
            {
                Position += new Vector2(_velocity.X, _velocity.Y) * seconds;
                _velocity.Z -= Gravity * seconds;
                _airHeight += _velocity.Z * seconds;
                float ground = Floor?.Invoke(Position) ?? 0f;
                if (_velocity.Z < 0f && _airHeight <= ground)
                    Airborne = false;
                if (_travel.State == RuntimeScriptedMoveState.Moving)
                    _travel = _travel with { ElapsedSeconds = _travel.ElapsedSeconds + seconds };
                return;
            }
            if (_chargeLeft >= 0f)
            {
                if (_travel.State == RuntimeScriptedMoveState.Moving)
                    _travel = _travel with { ElapsedSeconds = _travel.ElapsedSeconds + seconds };
                _chargeLeft -= seconds;
                if (_chargeLeft > 0f)
                    return;
                _chargeLeft = -1f;
                float pace = _travel.State != RuntimeScriptedMoveState.Moving ? 0f
                    : _travel.Request.Pace == RuntimeMovePace.Run ? RunSpeed
                    : WalkSpeed;
                float facing = Heading * MathF.PI / 180f;
                float rise = MathF.Sqrt(2f * Gravity * MathF.Max(0.35f, FullJumpHeight * _chargePower));
                _velocity = new Vector3(MathF.Sin(facing) * pace, MathF.Cos(facing) * pace, rise);
                _airHeight = Height;
                Airborne = true;
                return;
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
