using System.Numerics;

namespace AcDream.Runtime.Gameplay;

public enum RuntimeRouteDriveState
{
    Driving,

    Arrived,

    /// <summary>The body stopped making progress along a leg; plan again from where it stands.</summary>
    Blocked,

    /// <summary>The player moved the character.</summary>
    Interrupted,

    /// <summary>The character entered portal space or left the world.</summary>
    Lost,
}

/// <summary>The body as a route driver sees it on one frame.</summary>
public readonly record struct RuntimeRouteDriveSample(
    Vector3 Position,
    float HeadingDegrees,
    RuntimeScriptedMoveSnapshot Moves,
    bool InPortalSpace);

/// <summary>The scripted moves a route driver wants begun or stopped on one frame.</summary>
public readonly record struct RuntimeRouteDriveStep(
    RuntimeMoveRequest? Travel = null,
    RuntimeMoveRequest? Turn = null,
    bool StopTravel = false,
    bool StopTurn = false)
{
    public bool IsEmpty => Travel is null && Turn is null && !StopTravel && !StopTurn;
}

/// <summary>
/// Walks a body along the legs of a planned route with scripted moves. It turns
/// in place onto a leg that points well away from its heading, runs along the
/// leg while correcting its heading with small exact turns, walks the last
/// stretch before a sharp corner, and counts a leg's end as reached once the
/// body is near it or past it. A leg the body stops making progress on ends the
/// drive as blocked, so the route can be planned again from where it stands.
/// </summary>
public sealed class RuntimeRouteDriver
{
    public const float ArrivalRadius = 0.5f;
    public const float SteerToleranceDegrees = 3f;
    public const float TurnInPlaceDegrees = 30f;
    public const float CornerWalkMeters = 1.5f;

    /// <summary>A run without an amount is renewed this often, well inside the client's thirty second limit.</summary>
    public const float RenewTravelSeconds = 20f;

    private readonly Vector3[] _legs;
    private bool _started;
    private long _travelBaseline;
    private long _turnBaseline;

    public RuntimeRouteDriver(IReadOnlyList<Vector3> legs)
    {
        ArgumentNullException.ThrowIfNull(legs);
        if (legs.Count < 2)
            throw new ArgumentException("A route needs a start and at least one leg end.", nameof(legs));
        _legs = [.. legs];
        LegIndex = 1;
    }

    public RuntimeRouteDriveState State { get; private set; } = RuntimeRouteDriveState.Driving;

    /// <summary>The index in <see cref="Legs"/> of the leg end the body is heading for.</summary>
    public int LegIndex { get; private set; }

    public IReadOnlyList<Vector3> Legs => _legs;

    public RuntimeRouteDriveStep Advance(in RuntimeRouteDriveSample sample)
    {
        if (State != RuntimeRouteDriveState.Driving)
            return default;
        if (!_started)
        {
            _started = true;
            _travelBaseline = sample.Moves.Travel.Sequence;
            _turnBaseline = sample.Moves.Turn.Sequence;
        }

        RuntimeMoveChannelSnapshot travel = sample.Moves.Travel;
        RuntimeMoveChannelSnapshot turn = sample.Moves.Turn;
        bool travelIsOurs = travel.Sequence > _travelBaseline;
        bool turnIsOurs = turn.Sequence > _turnBaseline;
        bool travelling = travelIsOurs && travel.State == RuntimeScriptedMoveState.Moving;
        bool turning = turnIsOurs && turn.State == RuntimeScriptedMoveState.Moving;

        if (sample.InPortalSpace)
            return Finish(RuntimeRouteDriveState.Lost, travelling, turning);
        if ((travelIsOurs && travel.State == RuntimeScriptedMoveState.Interrupted)
            || (turnIsOurs && turn.State == RuntimeScriptedMoveState.Interrupted))
        {
            State = RuntimeRouteDriveState.Interrupted;
            return default;
        }
        if (travelIsOurs && travel.State == RuntimeScriptedMoveState.Blocked)
            return Finish(RuntimeRouteDriveState.Blocked, travelling, turning);

        var position = new Vector2(sample.Position.X, sample.Position.Y);
        while (LegIndex < _legs.Length && Reached(position, LegIndex))
            LegIndex++;
        if (LegIndex >= _legs.Length)
            return Finish(RuntimeRouteDriveState.Arrived, travelling, turning);

        Vector2 toEnd = Flat(_legs[LegIndex]) - position;
        float error = SignedDegrees(CompassHeading(toEnd) - sample.HeadingDegrees);
        if (MathF.Abs(error) > TurnInPlaceDegrees)
        {
            return new RuntimeRouteDriveStep(
                Turn: turning ? null : TurnBy(error),
                StopTravel: travelling);
        }

        RuntimeMovePace pace = SharpCornerWithin(toEnd.Length())
            ? RuntimeMovePace.Walk
            : RuntimeMovePace.Run;
        bool renew = !travelling
            || travel.Request.Direction != RuntimeMoveDirection.Forward
            || travel.Request.Pace != pace
            || travel.ElapsedSeconds >= RenewTravelSeconds;
        return new RuntimeRouteDriveStep(
            Travel: renew ? new RuntimeMoveRequest(RuntimeMoveDirection.Forward, pace, 0f) : null,
            Turn: !turning && MathF.Abs(error) > SteerToleranceDegrees ? TurnBy(error) : null);
    }

    /// <summary>Ends the drive at once, stopping whatever it had begun.</summary>
    public RuntimeRouteDriveStep Cancel()
    {
        if (State != RuntimeRouteDriveState.Driving)
            return default;
        State = RuntimeRouteDriveState.Interrupted;
        return new RuntimeRouteDriveStep(StopTravel: _started, StopTurn: _started);
    }

    private RuntimeRouteDriveStep Finish(RuntimeRouteDriveState state, bool travelling, bool turning)
    {
        State = state;
        return new RuntimeRouteDriveStep(StopTravel: travelling, StopTurn: turning);
    }

    private bool Reached(Vector2 position, int index)
    {
        Vector2 end = Flat(_legs[index]);
        if (Vector2.Distance(position, end) <= ArrivalRadius)
            return true;
        Vector2 along = end - Flat(_legs[index - 1]);
        return along.LengthSquared() > 1e-6f && Vector2.Dot(position - end, along) >= 0f;
    }

    private bool SharpCornerWithin(float distance)
    {
        if (LegIndex + 1 >= _legs.Length || distance > CornerWalkMeters)
            return false;
        Vector2 current = Flat(_legs[LegIndex]) - Flat(_legs[LegIndex - 1]);
        Vector2 next = Flat(_legs[LegIndex + 1]) - Flat(_legs[LegIndex]);
        return MathF.Abs(SignedDegrees(CompassHeading(next) - CompassHeading(current))) > TurnInPlaceDegrees;
    }

    private static RuntimeMoveRequest TurnBy(float degrees) =>
        new(
            degrees > 0f ? RuntimeMoveDirection.TurnRight : RuntimeMoveDirection.TurnLeft,
            RuntimeMovePace.Run,
            MathF.Abs(degrees));

    private static Vector2 Flat(Vector3 point) => new(point.X, point.Y);

    private static float CompassHeading(Vector2 direction) =>
        NormalizedDegrees(MathF.Atan2(direction.X, direction.Y) * (180f / MathF.PI));

    private static float SignedDegrees(float degrees) => (((degrees % 360f) + 540f) % 360f) - 180f;

    private static float NormalizedDegrees(float degrees) => ((degrees % 360f) + 360f) % 360f;
}
