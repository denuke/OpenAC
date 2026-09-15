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

    /// <summary>A leap came down on its landing's level but away from where it was planned; plan on from where the body stands.</summary>
    LandedElsewhere,
}

/// <summary>
/// How fast a body runs and turns at a run, in meters and degrees a second, which
/// sets the arc it follows when it turns as it runs.
/// </summary>
public readonly record struct RuntimeRouteTurning(
    float RunSpeed,
    float RunTurnDegreesPerSecond);

/// <summary>The body as a route driver sees it on one frame, with how fast it goes and turns when that is known.</summary>
public readonly record struct RuntimeRouteDriveSample(
    Vector3 Position,
    float HeadingDegrees,
    RuntimeScriptedMoveSnapshot Moves,
    bool InPortalSpace,
    bool Airborne = false,
    RuntimeRouteTurning? Turning = null);

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
/// leg while steering back onto it with small exact turns, and counts a leg's
/// end as reached once the body is near it or past it. Where it knows how fast
/// the body goes and turns, it cuts a corner without stopping: it starts turning
/// as far before the corner as the arc of a running turn needs and keeps running
/// around it, wherever the check it was given lets a body pass along that arc. A
/// corner it cannot run around, or one sharper than <see cref="SharpestCutDegrees"/>,
/// it runs up to, stops at and turns in place, never slowing to a walk. A leg the body stops making progress on ends the
/// drive as blocked, so the route can be planned again from where it stands.
/// A leap is taken as a standing long jump: the body walks up to the takeoff,
/// stops, faces the landing, charges the jump, and presses forward at the leap's
/// pace while the jump charges, so it leaves the ground at that pace. A leap that
/// comes down on its landing's level goes on along the route, or asks for a new
/// plan from where it came down when that is far from the landing; one that comes
/// down on another level ends the drive blocked.
/// </summary>
public sealed class RuntimeRouteDriver
{
    public const float ArrivalRadius = 0.5f;
    public const float SteerToleranceDegrees = 3f;
    public const float TurnInPlaceDegrees = 30f;
    /// <summary>A body walks the last this many meters up to a leap's takeoff.</summary>
    public const float TakeoffWalkMeters = 1.5f;

    /// <summary>A corner turning more than this is never cut; the body turns in place at it.</summary>
    public const float SharpestCutDegrees = 120f;

    /// <summary>
    /// A cut starts this many seconds of travel before its arc does: half a frame, since
    /// the body is sampled once a frame and is on average that far past where it
    /// crossed the arc's start.
    /// </summary>
    public const float CutEarlySeconds = 1f / 120f;

    /// <summary>
    /// While cutting a corner, and after a cut until it faces within
    /// <see cref="TurnInPlaceDegrees"/> of the leg ahead, the body stops to turn in place
    /// only when it faces farther than this from where it is headed.
    /// </summary>
    public const float CutTurnInPlaceDegrees = 90f;

    /// <summary>
    /// The body steers for a point this far along its leg past the point on the leg
    /// nearest it, or farther when it is off the leg, <see cref="LookAheadPerMeterAside"/>
    /// for each meter aside, so it turns back onto the leg at no more than about 20°.
    /// </summary>
    public const float LookAheadMeters = 2.5f;

    public const float LookAheadPerMeterAside = 2.75f;

    /// <summary>The points of an arc a cut is checked along lie about this far apart.</summary>
    private const float ArcStepMeters = 0.25f;

    /// <summary>A run without an amount is renewed this often, well inside the client's thirty second limit.</summary>
    public const float RenewTravelSeconds = 20f;

    /// <summary>
    /// A leap starts once the body stands this near its takeoff, facing its
    /// landing to within <see cref="LeapFacingDegrees"/>. It has come down on its
    /// landing's level within <see cref="LandingHeight"/> of the landing's height,
    /// and where it was planned within <see cref="LandingRadius"/> of the landing,
    /// measured flat. A jump still on the ground this long after its charge, or still
    /// in the air this long after it, ends the drive blocked.
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
    private readonly bool _takeOverMoves;
    private readonly Func<IReadOnlyList<Vector3>, bool>? _canCutAlong;
    private int _cutPlannedFor;
    private Cut _cut;
    private Cut _cutting;
    private RuntimeMovePace? _settling;

    /// <summary>
    /// How a corner is cut: at what pace, how far before the corner the turn starts,
    /// the point on the next leg where the cut ends, and the way that leg runs. A cut
    /// with no pace is a corner taken standing.
    /// </summary>
    private readonly record struct Cut(RuntimeMovePace? Pace, float Lead, Vector2 Exit, Vector2 Onward);

    /// <summary>
    /// A drive along <paramref name="legs"/>. With <paramref name="takeOverMoves"/>, moves
    /// already under way when it starts, such as those of a drive it replaces, count as
    /// its own, so a new plan goes on without stopping the character to start again.
    /// With <paramref name="canCutAlong"/>, which answers whether a body can pass along
    /// a path of points, the drive cuts the corners whose arcs it lets through.
    /// </summary>
    public RuntimeRouteDriver(
        IReadOnlyList<Vector3> legs,
        IReadOnlyList<RuntimeRouteLeap>? leaps = null,
        bool takeOverMoves = false,
        Func<IReadOnlyList<Vector3>, bool>? canCutAlong = null)
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
        _takeOverMoves = takeOverMoves;
        _canCutAlong = canCutAlong;
    }

    public RuntimeRouteDriveState State { get; private set; } = RuntimeRouteDriveState.Driving;

    /// <summary>The index in <see cref="Legs"/> of the leg end the body is heading for.</summary>
    public int LegIndex { get; private set; }

    public IReadOnlyList<Vector3> Legs => _legs;

    /// <summary>Whether the body is facing, charging or flying a leap.</summary>
    public bool IsLeaping => _phase != LeapPhase.None;

    /// <summary>How far from its planned landing, measured flat, the last leap came down.</summary>
    public float LandingError { get; private set; }

    /// <summary>How many corners the drive has come to and planned to run around, or turn in place at.</summary>
    public int CornersRunAround { get; private set; }

    public int CornersTurnedInPlace { get; private set; }

    public RuntimeRouteDriveStep Advance(in RuntimeRouteDriveSample sample)
    {
        if (State != RuntimeRouteDriveState.Driving)
            return default;
        if (!_started)
        {
            _started = true;
            _travelBaseline = Baseline(sample.Moves.Travel);
            _turnBaseline = Baseline(sample.Moves.Turn);
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

        if (_cutting.Pace is not null
            && (Vector2.Distance(position, _cutting.Exit) <= ArrivalRadius
                || Vector2.Dot(position - _cutting.Exit, _cutting.Onward) >= 0f))
        {
            _settling = _cutting.Pace;
            _cutting = default;
        }
        if (_cutting.Pace is null)
        {
            while (LegIndex < _legs.Length && !_leaps.ContainsKey(LegIndex))
            {
                if (Reached(position, LegIndex))
                {
                    LegIndex++;
                    continue;
                }
                Cut cut = CutFor(LegIndex, sample.Turning);
                if (cut.Pace is not null && AlongTo(position, LegIndex) <= cut.Lead)
                {
                    _cutting = cut;
                    _settling = null;
                    LegIndex++;
                }
                break;
            }
        }
        if (LegIndex >= _legs.Length)
            return Finish(RuntimeRouteDriveState.Arrived, travelling, turning);
        if (_leaps.ContainsKey(LegIndex))
        {
            _phase = LeapPhase.Facing;
            return AdvanceLeap(sample, position, travel, travelling, turning);
        }

        Vector2 aim = _cutting.Pace is null ? AimAlong(position, LegIndex) : _cutting.Exit;
        float error = SignedDegrees(CompassHeading(aim - position) - sample.HeadingDegrees);
        if (_settling is not null && MathF.Abs(error) <= TurnInPlaceDegrees)
            _settling = null;
        bool lenient = _cutting.Pace is not null || _settling is not null;
        if (MathF.Abs(error) > (lenient ? CutTurnInPlaceDegrees : TurnInPlaceDegrees))
        {
            return new RuntimeRouteDriveStep(
                Turn: turning ? null : TurnBy(error),
                StopTravel: travelling);
        }

        RuntimeMovePace pace = PaceFor(Vector2.Distance(position, Flat(_legs[LegIndex])));
        bool renew = !travelling
            || travel.Request.Direction != RuntimeMoveDirection.Forward
            || travel.Request.Pace != pace
            || travel.ElapsedSeconds >= RenewTravelSeconds;
        return new RuntimeRouteDriveStep(
            Travel: renew ? new RuntimeMoveRequest(RuntimeMoveDirection.Forward, pace, 0f) : null,
            Turn: !turning && MathF.Abs(error) > SteerToleranceDegrees ? TurnBy(error) : null);
    }

    private long Baseline(in RuntimeMoveChannelSnapshot channel) =>
        _takeOverMoves && channel.State == RuntimeScriptedMoveState.Moving
            ? channel.Sequence - 1
            : channel.Sequence;

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
                if (MathF.Abs(sample.Position.Z - landing.Z) > LandingHeight)
                    return Finish(RuntimeRouteDriveState.Blocked, travelling, turning);
                LandingError = Vector2.Distance(position, Flat(landing));
                if (LandingError > LandingRadius)
                    return Finish(RuntimeRouteDriveState.LandedElsewhere, travelling, turning);
                LegIndex++;
                return default;
        }
    }

    private RuntimeRouteDriveStep Finish(RuntimeRouteDriveState state, bool travelling, bool turning)
    {
        _phase = LeapPhase.None;
        _cutting = default;
        _settling = null;
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

    /// <summary>Whether to walk the rest of a leg: the last stretch before a leap's takeoff.</summary>
    private bool SlowForWithin(float distance) =>
        LegIndex + 1 < _legs.Length
        && distance <= TakeoffWalkMeters
        && _leaps.ContainsKey(LegIndex + 1);

    /// <summary>Whether the route turns more than <see cref="TurnInPlaceDegrees"/> at a leg's end between two walked legs.</summary>
    private bool TurnsSharplyAt(int corner)
    {
        if (corner + 1 >= _legs.Length || _leaps.ContainsKey(corner) || _leaps.ContainsKey(corner + 1))
            return false;
        Vector2 current = Flat(_legs[corner]) - Flat(_legs[corner - 1]);
        Vector2 next = Flat(_legs[corner + 1]) - Flat(_legs[corner]);
        return MathF.Abs(SignedDegrees(CompassHeading(next) - CompassHeading(current))) > TurnInPlaceDegrees;
    }

    /// <summary>
    /// The pace to go at: a cut's own while cutting and until the body faces the leg
    /// ahead, and otherwise a run, but for the last stretch before a leap's takeoff.
    /// </summary>
    private RuntimeMovePace PaceFor(float distance)
    {
        if ((_cutting.Pace ?? _settling) is { } cutting)
            return cutting;
        return SlowForWithin(distance) ? RuntimeMovePace.Walk : RuntimeMovePace.Run;
    }

    /// <summary>
    /// The point the body steers for on the leg to a leg end: past the point on the leg
    /// nearest the body by <see cref="LookAheadMeters"/>, or more when the body is off the
    /// leg, and never past the leg's end.
    /// </summary>
    private Vector2 AimAlong(Vector2 position, int index)
    {
        Vector2 start = Flat(_legs[index - 1]);
        Vector2 end = Flat(_legs[index]);
        Vector2 along = end - start;
        float length = along.Length();
        if (length < 1e-3f)
            return end;
        Vector2 direction = along / length;
        Vector2 offset = position - start;
        float aside = MathF.Abs((offset.X * direction.Y) - (offset.Y * direction.X));
        float reach = MathF.Max(Vector2.Dot(offset, direction), 0f) + MathF.Max(LookAheadMeters, aside * LookAheadPerMeterAside);
        return reach >= length ? end : start + (direction * reach);
    }

    /// <summary>How far the body is from a leg's end, measured along the leg.</summary>
    private float AlongTo(Vector2 position, int index)
    {
        Vector2 end = Flat(_legs[index]);
        Vector2 along = end - Flat(_legs[index - 1]);
        float length = along.Length();
        return length > 1e-3f ? Vector2.Dot(end - position, along / length) : Vector2.Distance(position, end);
    }

    /// <summary>How the corner at a leg's end is taken, worked out once, the first time the body heads for it.</summary>
    private Cut CutFor(int corner, RuntimeRouteTurning? turning)
    {
        if (_cutPlannedFor != corner)
        {
            _cutPlannedFor = corner;
            _cut = PlanCut(corner, turning);
            if (_cut.Pace == RuntimeMovePace.Run)
                CornersRunAround++;
            else if (TurnsSharplyAt(corner))
                CornersTurnedInPlace++;
        }
        return _cut;
    }

    /// <summary>
    /// A cut around the corner at a leg's end, at a run, where the arc of a running turn
    /// passes; none where it does not, so the body stops and turns there. An arc must start and
    /// end within the legs beside the corner, no nearer the middle of a leg the body
    /// cuts into from another corner. Leaps' takeoffs and landings are not cut.
    /// </summary>
    private Cut PlanCut(int corner, RuntimeRouteTurning? turning)
    {
        if (_canCutAlong is null
            || turning is not { } speeds
            || corner + 1 >= _legs.Length
            || _leaps.ContainsKey(corner)
            || _leaps.ContainsKey(corner + 1))
        {
            return default;
        }
        Vector2 before = Flat(_legs[corner - 1]);
        Vector2 at = Flat(_legs[corner]);
        Vector2 after = Flat(_legs[corner + 1]);
        float inbound = Vector2.Distance(before, at);
        float outbound = Vector2.Distance(at, after);
        if (inbound < 1e-3f || outbound < 1e-3f)
            return default;
        float turn = SignedDegrees(CompassHeading(after - at) - CompassHeading(at - before));
        if (MathF.Abs(turn) <= SteerToleranceDegrees || MathF.Abs(turn) > SharpestCutDegrees)
            return default;
        float room = MathF.Min(corner == 1 ? inbound : inbound * 0.5f, outbound * 0.5f);
        return TryCut(corner, turn, room, RuntimeMovePace.Run, speeds.RunSpeed, speeds.RunTurnDegreesPerSecond);
    }

    private Cut TryCut(int corner, float turn, float room, RuntimeMovePace pace, float speed, float degreesPerSecond)
    {
        if (speed <= 0f || degreesPerSecond <= 0f)
            return default;
        float radius = speed / (degreesPerSecond * (MathF.PI / 180f));
        float tangent = radius * MathF.Tan(MathF.Abs(turn) * 0.5f * (MathF.PI / 180f));
        if (tangent > room)
            return default;
        Vector3[] arc = Arc(corner, turn, radius, tangent);
        if (!_canCutAlong!(arc))
            return default;
        Vector2 onward = Vector2.Normalize(Flat(_legs[corner + 1]) - Flat(_legs[corner]));
        return new Cut(pace, tangent + (speed * CutEarlySeconds), Flat(arc[^1]), onward);
    }

    /// <summary>
    /// The arc a body turning at a steady rate, around a circle of <paramref name="radius"/>,
    /// follows around a corner turning <paramref name="turn"/> degrees, right for more than
    /// zero: from <paramref name="tangent"/> before the corner on the leg in to that far
    /// along the leg out, as points about <see cref="ArcStepMeters"/> apart at the legs' heights.
    /// </summary>
    private Vector3[] Arc(int corner, float turn, float radius, float tangent)
    {
        Vector3 before = _legs[corner - 1];
        Vector3 at = _legs[corner];
        Vector3 after = _legs[corner + 1];
        Vector3 start = Vector3.Lerp(at, before, tangent / Vector2.Distance(Flat(before), Flat(at)));
        Vector3 end = Vector3.Lerp(at, after, tangent / Vector2.Distance(Flat(at), Flat(after)));
        Vector2 inbound = Vector2.Normalize(Flat(at) - Flat(before));
        Vector2 inside = turn > 0f ? new Vector2(inbound.Y, -inbound.X) : new Vector2(-inbound.Y, inbound.X);
        Vector2 centre = Flat(start) + (inside * radius);
        Vector2 spoke = Flat(start) - centre;
        float sweep = -turn * (MathF.PI / 180f);
        int steps = Math.Max(1, (int)MathF.Ceiling(radius * MathF.Abs(sweep) / ArcStepMeters));
        var points = new Vector3[steps + 1];
        for (int step = 0; step <= steps; step++)
        {
            float share = (float)step / steps;
            float cos = MathF.Cos(sweep * share);
            float sin = MathF.Sin(sweep * share);
            Vector2 point = centre + new Vector2((spoke.X * cos) - (spoke.Y * sin), (spoke.X * sin) + (spoke.Y * cos));
            points[step] = new Vector3(point, start.Z + ((end.Z - start.Z) * share));
        }
        return points;
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
