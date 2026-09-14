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
    bool InPortalSpace,
    bool Airborne = false);

/// <summary>The scripted moves a route driver wants begun or stopped on one frame, and the power of a jump to charge.</summary>
public readonly record struct RuntimeRouteDriveStep(
    RuntimeMoveRequest? Travel = null,
    RuntimeMoveRequest? Turn = null,
    bool StopTravel = false,
    bool StopTurn = false,
    float? Jump = null)
{
    public bool IsEmpty => Travel is null && Turn is null && !StopTravel && !StopTurn && Jump is null;
}

/// <summary>
/// A leg of a route flown rather than walked: the leg that ends at
/// <c>Legs[LegIndex]</c> is a standing long jump from <c>Legs[LegIndex - 1]</c>,
/// charged to <paramref name="Power"/> and left at running or walking pace.
/// </summary>
public readonly record struct RuntimeRouteLeap(int LegIndex, float Power, bool Run);

/// <summary>
/// Walks a body along the legs of a planned route with scripted moves. It turns
/// in place onto a leg that points well away from its heading, runs along the
/// leg while correcting its heading with small exact turns, walks the last
/// stretch before a sharp corner, and counts a leg's end as reached once the
/// body is near it or past it. A leg the body stops making progress on ends the
/// drive as blocked, so the route can be planned again from where it stands.
/// A leap is taken as a standing long jump: the body walks up to the takeoff,
/// stops, faces the landing, charges the jump, and presses forward at the leap's
/// pace while the jump charges, so it leaves the ground at that pace.
/// </summary>
public sealed class RuntimeRouteDriver
{
    public const float ArrivalRadius = 0.5f;
    public const float SteerToleranceDegrees = 3f;
    public const float TurnInPlaceDegrees = 30f;
    public const float CornerWalkMeters = 1.5f;

    /// <summary>A run without an amount is renewed this often, well inside the client's thirty second limit.</summary>
    public const float RenewTravelSeconds = 20f;

    /// <summary>
    /// A leap starts once the body stands this near its takeoff, facing its
    /// landing to within <see cref="LeapFacingDegrees"/>. It has landed where it
    /// should within <see cref="LandingRadius"/> of the landing, measured flat, and
    /// <see cref="LandingHeight"/> of its height. A jump still on the ground this
    /// long after its charge, or still in the air this long after it, ends the drive
    /// blocked, as does a landing anywhere else.
    /// </summary>
    public const float TakeoffRadius = 0.35f;
    public const float LeapFacingDegrees = 2f;
    public const float LandingRadius = 1.5f;
    public const float LandingHeight = 1f;
    public const float TakeoffGraceSeconds = 1f;
    public const float LongestFlightSeconds = 5f;

    private enum LeapPhase
    {
        None,
        Facing,
        Charging,
        Flying,
    }

    private readonly Vector3[] _legs;
    private readonly Dictionary<int, RuntimeRouteLeap> _leaps = [];
    private bool _started;
    private long _travelBaseline;
    private long _turnBaseline;
    private LeapPhase _phase;
    private bool _leapTravelBegun;

    public RuntimeRouteDriver(IReadOnlyList<Vector3> legs, IReadOnlyList<RuntimeRouteLeap>? leaps = null)
    {
        ArgumentNullException.ThrowIfNull(legs);
        if (legs.Count < 2)
            throw new ArgumentException("A route needs a start and at least one leg end.", nameof(legs));
        _legs = [.. legs];
        foreach (RuntimeRouteLeap leap in leaps ?? [])
        {
            if (leap.LegIndex < 1 || leap.LegIndex >= _legs.Length)
                throw new ArgumentException("A leap must end at one of the route's leg ends.", nameof(leaps));
            _leaps[leap.LegIndex] = leap;
        }
        LegIndex = 1;
    }

    public RuntimeRouteDriveState State { get; private set; } = RuntimeRouteDriveState.Driving;

    /// <summary>The index in <see cref="Legs"/> of the leg end the body is heading for.</summary>
    public int LegIndex { get; private set; }

    public IReadOnlyList<Vector3> Legs => _legs;

    /// <summary>Whether the body is facing, charging or flying a leap.</summary>
    public bool IsLeaping => _phase != LeapPhase.None;

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
            _phase = LeapPhase.None;
            State = RuntimeRouteDriveState.Interrupted;
            return default;
        }
        if (travelIsOurs && travel.State == RuntimeScriptedMoveState.Blocked)
            return Finish(RuntimeRouteDriveState.Blocked, travelling, turning);

        var position = new Vector2(sample.Position.X, sample.Position.Y);
        if (_phase != LeapPhase.None)
            return AdvanceLeap(sample, position, travel, travelling, turning);

        while (LegIndex < _legs.Length && !_leaps.ContainsKey(LegIndex) && Reached(position, LegIndex))
            LegIndex++;
        if (LegIndex >= _legs.Length)
            return Finish(RuntimeRouteDriveState.Arrived, travelling, turning);
        if (_leaps.ContainsKey(LegIndex))
        {
            _phase = LeapPhase.Facing;
            return AdvanceLeap(sample, position, travel, travelling, turning);
        }

        Vector2 toEnd = Flat(_legs[LegIndex]) - position;
        float error = SignedDegrees(CompassHeading(toEnd) - sample.HeadingDegrees);
        if (MathF.Abs(error) > TurnInPlaceDegrees)
        {
            return new RuntimeRouteDriveStep(
                Turn: turning ? null : TurnBy(error),
                StopTravel: travelling);
        }

        RuntimeMovePace pace = SlowForWithin(toEnd.Length())
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
        _phase = LeapPhase.None;
        State = RuntimeRouteDriveState.Interrupted;
        return new RuntimeRouteDriveStep(StopTravel: _started, StopTurn: _started);
    }

    /// <summary>
    /// Takes the leap whose leg the body is on: faces its landing from where the
    /// body stopped at the takeoff, charges the jump, presses forward at its pace,
    /// and once the body comes down, goes on along the route from a landing near
    /// the leg's end or ends the drive blocked from anywhere else.
    /// </summary>
    private RuntimeRouteDriveStep AdvanceLeap(
        in RuntimeRouteDriveSample sample,
        Vector2 position,
        RuntimeMoveChannelSnapshot travel,
        bool travelling,
        bool turning)
    {
        RuntimeRouteLeap leap = _leaps[LegIndex];
        Vector3 landing = _legs[LegIndex];
        float charge = leap.Power * (float)RuntimeScriptedMovement.FullJumpChargeSeconds;
        switch (_phase)
        {
            case LeapPhase.Facing:
            {
                if (turning)
                    return travelling ? new RuntimeRouteDriveStep(StopTravel: true) : default;
                float error = SignedDegrees(CompassHeading(Flat(landing) - position) - sample.HeadingDegrees);
                if (MathF.Abs(error) > LeapFacingDegrees)
                    return new RuntimeRouteDriveStep(Turn: TurnBy(error), StopTravel: travelling);
                if (travelling)
                    return new RuntimeRouteDriveStep(StopTravel: true);
                _phase = LeapPhase.Charging;
                _leapTravelBegun = false;
                return new RuntimeRouteDriveStep(Jump: leap.Power);
            }
            case LeapPhase.Charging:
                if (!_leapTravelBegun)
                {
                    _leapTravelBegun = true;
                    RuntimeMovePace pace = leap.Run ? RuntimeMovePace.Run : RuntimeMovePace.Walk;
                    return new RuntimeRouteDriveStep(Travel: new RuntimeMoveRequest(RuntimeMoveDirection.Forward, pace, 0f));
                }
                if (sample.Airborne)
                {
                    _phase = LeapPhase.Flying;
                    return default;
                }
                return travel.ElapsedSeconds > charge + TakeoffGraceSeconds
                    ? Finish(RuntimeRouteDriveState.Blocked, travelling, turning)
                    : default;
            default:
                if (sample.Airborne)
                {
                    return travel.ElapsedSeconds > charge + LongestFlightSeconds
                        ? Finish(RuntimeRouteDriveState.Blocked, travelling, turning)
                        : default;
                }
                _phase = LeapPhase.None;
                if (Vector2.Distance(position, Flat(landing)) > LandingRadius
                    || MathF.Abs(sample.Position.Z - landing.Z) > LandingHeight)
                {
                    return Finish(RuntimeRouteDriveState.Blocked, travelling, turning);
                }
                LegIndex++;
                return default;
        }
    }

    private RuntimeRouteDriveStep Finish(RuntimeRouteDriveState state, bool travelling, bool turning)
    {
        _phase = LeapPhase.None;
        State = state;
        return new RuntimeRouteDriveStep(StopTravel: travelling, StopTurn: turning);
    }

    /// <summary>
    /// Whether the body has reached a leg's end: near it, or past it. The takeoff
    /// of a leap counts only once the body stands within <see cref="TakeoffRadius"/>.
    /// </summary>
    private bool Reached(Vector2 position, int index)
    {
        Vector2 end = Flat(_legs[index]);
        if (_leaps.ContainsKey(index + 1))
            return Vector2.Distance(position, end) <= TakeoffRadius;
        if (Vector2.Distance(position, end) <= ArrivalRadius)
            return true;
        Vector2 along = end - Flat(_legs[index - 1]);
        return along.LengthSquared() > 1e-6f && Vector2.Dot(position - end, along) >= 0f;
    }

    /// <summary>Whether to walk the rest of a leg: the last stretch before a sharp corner or a leap's takeoff.</summary>
    private bool SlowForWithin(float distance)
    {
        if (LegIndex + 1 >= _legs.Length || distance > CornerWalkMeters)
            return false;
        if (_leaps.ContainsKey(LegIndex + 1))
            return true;
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
