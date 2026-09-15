using System.Numerics;
using AcDream.Core.Navigation;
using AcDream.Core.Physics;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Navigation;

internal enum NavigationWalkState
{
    None,

    /// <summary>Building a grid, or searching one for a route.</summary>
    Planning,

    Walking,

    /// <summary>
    /// Stopped where the character stands while something else needs the character,
    /// such as a plugin fighting a monster; the walk plans on once nothing has for a moment.
    /// </summary>
    Waiting,

    /// <summary>A route was asked for without walking it, and found.</summary>
    Planned,

    Arrived,

    /// <summary>No route joins the character to the goal.</summary>
    NoRoute,

    /// <summary>The character stopped making progress, even after planning again from where it stood.</summary>
    Blocked,

    /// <summary>A stop, or a newer request, ended it.</summary>
    Stopped,

    /// <summary>The player moved the character.</summary>
    Interrupted,

    /// <summary>The character entered portal space or left the world.</summary>
    Lost,
}

/// <summary>
/// Where the most recent walk or route request stands. <paramref name="Sequence"/>
/// grows by one for every request, so a caller can tell its own from a later one.
/// <paramref name="RemainingMeters"/> is the length of route left while the walk
/// goes on; once it has ended, the straight-line distance from the character to
/// where the goal stands then, or NaN when that is unknown. On a walk that ended
/// blocked, <paramref name="BlockedByObjectId"/> is the server object beside the
/// spot where it last stopped making progress; otherwise it is zero.
/// </summary>
internal readonly record struct NavigationWalkReport(
    long Sequence,
    NavigationWalkState State,
    uint ObjectId,
    float RemainingMeters,
    int Replans,
    string Reason,
    uint BlockedByObjectId = 0u);

/// <summary>
/// The local player's body on one frame, as a walk sees it: the cell it stands in,
/// what it can leap, when it can jump at all, and whether it is in the air.
/// </summary>
internal readonly record struct NavigationWalkBodySample(
    Vector3 Position,
    float HeadingDegrees,
    NavBody Body,
    RuntimeScriptedMoveSnapshot Moves,
    bool InPortalSpace,
    uint CellId = 0u,
    NavLeapAbility? Leaps = null,
    bool Airborne = false,
    RuntimeRouteTurning? Turning = null);

/// <summary>
/// A server object standing where a walk stopped making progress, the point it
/// stands on and radius it fills, whether it is a creature or player, which
/// moves and moves aside when bumped, and whether it is hostile, which stays to fight.
/// </summary>
internal readonly record struct NavigationBlocker(
    uint ObjectId,
    string Name,
    bool IsClosedDoor,
    Vector3 Position = default,
    float Radius = 0f,
    bool Moves = false,
    bool Hostile = false);

/// <summary>A door a walk can open, and the point it stands on and radius it fills, when known.</summary>
internal readonly record struct NavigationDoor(uint ObjectId, string Name, Vector3 Position = default, float Radius = 0f);

/// <summary>The doors a walk meets, as the client sees and opens them.</summary>
internal interface INavigationDoors
{
    /// <summary>
    /// The closed door nearest <paramref name="from"/> whose position lies ahead of it,
    /// within <paramref name="corridor"/> of the straight line from it to <paramref name="to"/>.
    /// </summary>
    bool TryFindClosedDoor(Vector3 from, Vector3 to, float corridor, out NavigationDoor door);

    bool IsOpen(uint doorId);

    /// <summary>Asks the client to use a door, walking into its use range first as a player's click would.</summary>
    void Use(uint doorId);

    /// <summary>
    /// Whether the client's appraisal of a door says it is locked, or null when the
    /// client has not appraised it.
    /// </summary>
    bool? IsLocked(uint doorId) => null;

    /// <summary>Asks the server to appraise a door, so <see cref="IsLocked"/> can answer; false when the client would not ask.</summary>
    bool Appraise(uint doorId) => false;
}

/// <summary>The local player's body, as a walk samples and moves it.</summary>
internal interface INavigationWalkBody
{
    /// <summary>False while there is no local player in the world.</summary>
    bool TrySample(out NavigationWalkBodySample sample);

    bool BeginMove(in RuntimeMoveRequest request);

    bool StopMove(RuntimeMoveChannel channel);

    /// <summary>Charges a jump for <paramref name="power"/> of a full charge, from 0 to 1, then releases it.</summary>
    bool BeginJump(float power, RuntimeMovePace? leaveAt) => false;
}

/// <summary>Where objects stand in the physics world, and what stands in a walk's way.</summary>
internal interface INavigationGoalSource
{
    bool TryLocate(uint objectId, out Vector3 position);

    /// <summary>
    /// The server object, such as a closed door, whose edge comes nearest
    /// <paramref name="position"/> within <paramref name="radius"/>.
    /// </summary>
    bool TryFindBlocker(Vector3 position, float radius, out NavigationBlocker blocker)
    {
        blocker = default;
        return false;
    }

    /// <summary>
    /// The objects the server has placed within <paramref name="radius"/> of
    /// <paramref name="around"/> that collide and stand still, such as ore deposits
    /// and chests, as the points they stand on and the radii they fill. Doors,
    /// creatures, players and the object <paramref name="goalObjectId"/> are left out.
    /// </summary>
    IReadOnlyList<NavAvoidance> FindObstacles(Vector3 around, float radius, uint goalObjectId) => [];

    /// <summary>
    /// The creatures and players within <paramref name="radius"/> of
    /// <paramref name="around"/>, as the points they stand on and the radii they fill.
    /// They move, and move aside when bumped, so routes pass them where they stand now
    /// rather than keep out of those spots for good. The object
    /// <paramref name="goalObjectId"/> is left out.
    /// </summary>
    IReadOnlyList<NavAvoidance> FindCrowd(Vector3 around, float radius, uint goalObjectId) => [];

    /// <summary>
    /// Where a place stands in the physics world: a point in the landblock-local frame
    /// of the cell <paramref name="cellId"/>, as the client's /loc gives it, or for a cell
    /// of zero a point measured from the corner of the first landblock. False when the
    /// client cannot place it relative to the character.
    /// </summary>
    bool TryLocatePlace(uint cellId, Vector3 local, out Vector3 position)
    {
        position = default;
        return false;
    }

    /// <summary>
    /// Where a point in the physics world stands measured from the corner of the first
    /// landblock, the way a place with no cell is given. False when the client cannot tell
    /// relative to the character.
    /// </summary>
    bool TryGlobalOf(Vector3 world, out Vector3 global)
    {
        global = default;
        return false;
    }
}

/// <summary>
/// Plans routes to objects over navigation grids built from the physics world,
/// and walks the local player along them with scripted moves. A grid covers a
/// square region around the character and its goal and is built off the update
/// thread. Inside a sealed dungeon a grid covers every cell of the dungeon and no
/// terrain, however large the dungeon, so one grid serves every walk there. A
/// goal too far away for one region is walked to in stages, each planned over a
/// region reaching toward the goal. A walk that stops making progress plans
/// again from where the character stands, keeping out of the spot where it
/// stuck, a few times before it gives up and names what stood beside that spot.
/// A walk that meets a closed door on its way, or stops making progress beside
/// one, has the client open it and plans again once it is open. A walk that
/// arrives turns the character to face its goal. A route keeps out of the objects
/// the server placed, such as ore deposits, whenever another way arrives. A walk
/// that stops making progress beside an object keeps out of all of it after, and
/// with no other way ends blocked naming it. A walk waits where the character
/// stands while something else needs the character, such as a plugin fighting a
/// monster, and plans again from there once nothing has for a moment. Routes pass
/// creatures and players around them where there is room, and through them where
/// going around would bring the character nearer walls. A walk looks ahead for one
/// that steps onto its route and plans a way around it without stopping, and a walk
/// stopped by one waits for it to move aside before planning around it.
/// </summary>
internal sealed class NavigationWalkController
{
    public const float DefaultArrivalMeters = 2.5f;
    public const int MaximumReplans = 3;

    /// <summary>Room kept around the character and the goal inside a planning region.</summary>
    internal const float RegionMargin = 24f;
    internal const float MinimumRegion = 96f;
    internal const float MaximumRegion = 320f;

    /// <summary>The side of the grid kept around the character while the grid is shown and nothing is planned.</summary>
    internal const float ViewRegion = 128f;

    /// <summary>
    /// A goal too far away for one region is walked to in stages, each planned
    /// over a region reaching from the character toward the goal. A stage must
    /// bring the character at least this much nearer, and a walk has at most
    /// this many.
    /// </summary>
    internal const float MinimumStageProgress = 16f;
    internal const int MaximumStages = 12;

    /// <summary>
    /// The largest region a grid covering a whole sealed dungeon may have. It is
    /// larger than every dungeon in the game data, the widest of which spans about
    /// 1,020 m, and bounds the cost of a grid over cells placed far apart.
    /// </summary>
    internal const float MaximumDungeonRegion = 2048f;

    /// <summary>
    /// A walk that stops making progress keeps its next plans out of a spot this
    /// far ahead of the character and this wide, and looks this far around that
    /// spot for what blocked it.
    /// </summary>
    internal const float BlockedSpotAhead = 0.75f;
    internal const float BlockedSpotRadius = 0.6f;
    internal const float BlockerSearchRadius = 1.5f;

    /// <summary>
    /// A route keeps out of the objects the server placed when a route that does
    /// arrives, or ends no more than this much farther from the goal than a route
    /// through them.
    /// </summary>
    internal const float ObstacleDetourReach = 1f;

    /// <summary>How far above or below a leg an object's footprint still counts as in its way.</summary>
    private const float NavigationObstacleHeight = 2f;

    /// <summary>
    /// The deepest drop a walk plans. Measured live, a character took no damage from
    /// falls of up to 12 m, and from 13 m on took a little more with every meter, so
    /// walks plan only drops that do no damage.
    /// </summary>
    internal const float SafeDropMeters = 12f;

    /// <summary>A grid is used for a route only while the character and the goal are this far inside it.</summary>
    private const float CoverMargin = 8f;

    private const int MaximumBuildsPerPlan = 2;
    private const int ViewRetryTicks = 120;
    private const float FaceToleranceDegrees = 10f;

    /// <summary>
    /// While walking, a closed door within this far ahead along the leg and this
    /// near the line to it is opened before the character reaches it. The check
    /// runs every few frames. The door is used only this many frames after the
    /// walk stops the character, because a stop that lands after the use cancels
    /// the client's walk into the door's use range and drops the use with it. A
    /// door is waited on this long to open once used, and a door that closes again
    /// before the walk is through is used at most this often.
    /// </summary>
    internal const float DoorLookAheadMeters = 3f;
    internal const float DoorCorridorMeters = 1.2f;
    internal const int DoorUseSettleTicks = 3;
    internal const float DoorOpenWaitSeconds = 5f;
    internal const int MaximumDoorUses = 2;
    private const int DoorCheckTicks = 5;

    /// <summary>
    /// A door the client has never appraised is appraised before a walk uses it,
    /// and the answer is waited on this long, so a locked door is walked around
    /// instead of tried. A door that is locked or would not open is kept out of
    /// the walk's later plans, and a walk with no other way ends blocked naming it.
    /// </summary>
    internal const float DoorAppraisalWaitSeconds = 2f;

    /// <summary>
    /// A walk that waited while something else needed the character plans on only
    /// once nothing has needed it for this long, so a fight that pauses between
    /// blows does not set the walk going between them.
    /// </summary>
    internal const double PauseSettleSeconds = 1.5d;

    /// <summary>
    /// How long a walk asked for while the character is in the air, jumping or thrown, waits
    /// for it to land before planning from wherever it is.
    /// </summary>
    internal const double LandingWaitSeconds = 5d;

    /// <summary>
    /// Routes pass the creatures and players within <see cref="CrowdReach"/> of the
    /// character where they stand when planned. While walking, the next
    /// <see cref="CrowdLookAheadMeters"/> of the route are looked along every
    /// <see cref="CrowdCheckTicks"/> frames for one that has stepped onto it since, and a
    /// way around it is planned while the walk goes on, at most once every
    /// <see cref="DetourIntervalSeconds"/>. A creature counts as one the route was
    /// planned with while it stands within <see cref="CrowdMovedMeters"/> of where it stood.
    /// </summary>
    internal const float CrowdReach = 30f;
    internal const float CrowdLookAheadMeters = 6f;
    internal const int CrowdCheckTicks = 15;
    internal const float CrowdMovedMeters = 0.75f;
    internal const double DetourIntervalSeconds = 1d;

    /// <summary>
    /// A walk stopped by a creature or player waits this long for it to move aside,
    /// at most this many times, before planning a way around it, and may wait again
    /// once it has walked on this far. A creature's spot is never kept out of for the
    /// rest of a walk, since it moves.
    /// </summary>
    internal const double ShuffleWaitSeconds = 1.5d;
    internal const int MaximumShuffles = 2;
    internal const float ShuffleProgressMeters = 3f;

    private readonly PhysicsEngine _physics;
    private readonly INavigationWalkBody _body;
    private readonly INavigationGoalSource _goals;
    private readonly Action<string>? _say;
    private readonly INavigationDoors? _doors;
    private readonly Func<uint, bool>? _isSealedDungeon;
    private readonly object _gate = new();

    private Request? _incoming;
    private bool _stopIncoming;
    private long _sequence;
    private NavigationWalkReport _report;

    private NavGrid? _grid;

    /// <summary>How far the character may move, and how long places stand, before they are found again.</summary>
    private const float PlacesMovedMeters = 8f;
    private const double PlacesFreshSeconds = 15d;

    /// <summary>How near the character a node must be to count as where it stands, as a route finds its start.</summary>
    private const float PlacesStartRadius = 1.5f;
    private const float PlacesStartHeight = 1f;

    private NavigationPlacesReport _places = NavigationPlacesReport.None;
    private bool _placesWanted;
    private Task<IReadOnlyList<NavPlace>>? _placing;
    private NavGrid? _placingGrid;
    private Vector3 _placingFrom;
    private bool _placingInDungeon;
    private NavGrid? _placesGrid;
    private Vector3 _placesFrom;
    private double _placesAt = double.NegativeInfinity;
    private Task<NavGrid>? _building;

    /// <summary>
    /// The landblock of the sealed dungeon the grid covers whole, or zero for a
    /// grid over a region of the world; and the same for the grid being built.
    /// </summary>
    private uint _gridDungeon;
    private uint _buildingDungeon;

    /// <summary>
    /// The cell last asked about whether it lies in a sealed dungeon, and the
    /// answer, since each answer reads the game data.
    /// </summary>
    private uint _classifiedCellId;
    private bool _classifiedSealed;
    private long _tick;
    private double _seconds;
    private long _viewRetryTick;

    private Request? _active;
    private Task<NavRoute>? _routing;
    private Request? _routingFor;
    private RuntimeRouteDriver? _driver;

    public NavigationWalkController(
        PhysicsEngine physics,
        INavigationWalkBody body,
        INavigationGoalSource goals,
        Action<string>? say = null,
        INavigationDoors? doors = null,
        Func<uint, bool>? isSealedDungeon = null)
    {
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
        _body = body ?? throw new ArgumentNullException(nameof(body));
        _goals = goals ?? throw new ArgumentNullException(nameof(goals));
        _say = say;
        _doors = doors;
        _isSealedDungeon = isSealedDungeon;
    }

    /// <summary>Whether to keep a grid built around the character while nothing is planned.</summary>
    public bool ShowGrid { get; set; }

    /// <summary>The spots the character can walk to, as last found.</summary>
    public NavigationPlacesReport Places
    {
        get
        {
            lock (_gate)
                return _places;
        }
    }

    /// <summary>Asks for the spots the character can walk to, mapping the ground around it first when no grid covers it.</summary>
    public void WantPlaces()
    {
        lock (_gate)
        {
            _placesWanted = true;
            if (ReferenceEquals(_places, NavigationPlacesReport.None))
                _places = _places with { State = NavigationPlacesState.Mapping, Reason = "mapping the ground around the character" };
        }
    }

    /// <summary>
    /// What needs the character now, such as a plugin fighting a monster, or null
    /// when nothing does. A walk waits while it names something. It is asked on
    /// every frame a walk is under way.
    /// </summary>
    public Func<string?>? PausedBy { get; set; }

    /// <summary>The grid most recently built.</summary>
    public NavGrid? Grid => _grid;

    /// <summary>
    /// The objects the server placed within <paramref name="radius"/> of a point that
    /// routes keep out of, as they stand now, other than the latest request's goal.
    /// </summary>
    public IReadOnlyList<NavAvoidance> ObstaclesNear(Vector3 around, float radius) =>
        _goals.FindObstacles(around, radius, Report.ObjectId);

    /// <summary>The route most recently searched for.</summary>
    public NavRoute? Route { get; private set; }

    /// <summary>The goal of the most recent request that found its object, and how near counts as arriving.</summary>
    public (Vector3 Position, float ArrivalMeters)? Goal { get; private set; }

    /// <summary>The index in the route's legs of the leg end the character is walking toward, while it walks.</summary>
    public int? LegIndex => _driver?.LegIndex;

    /// <summary>Whether the route being walked is one stage of a walk to a goal beyond it.</summary>
    public bool IsStaged => _active is { Staged: true };

    /// <summary>Whether a grid is being built or a route searched for, off the update thread.</summary>
    internal bool IsSearching => _routing is not null || _building is not null;

    /// <summary>Whether a grid build or route search under way has finished, so the next tick takes it up.</summary>
    internal bool SearchFinished => _building is { IsCompleted: true } || _routing is { IsCompleted: true };

    /// <summary>Whether a request is waiting, being planned or being walked.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                if (_incoming is not null)
                    return true;
            }
            return _active is not null;
        }
    }

    public NavigationWalkReport Report
    {
        get
        {
            lock (_gate)
                return _report;
        }
    }

    /// <summary>Asks for a route to an object without walking it, and returns the request's sequence.</summary>
    public long RouteTo(uint objectId, float arrivalMeters = DefaultArrivalMeters) =>
        Enqueue(objectId, arrivalMeters, walk: false);

    /// <summary>Asks to walk to an object, and returns the request's sequence.</summary>
    public long WalkTo(uint objectId, float arrivalMeters = DefaultArrivalMeters) =>
        Enqueue(objectId, arrivalMeters, walk: true);

    /// <summary>
    /// Asks to walk to a place, a point in the landblock-local frame of the cell
    /// <paramref name="cellId"/> as the client's /loc gives it, or for a cell of zero a
    /// point measured from the corner of the first landblock, and returns the request's
    /// sequence. A walk to a place reports no object and faces nothing on arrival.
    /// </summary>
    public long WalkToPlace(uint cellId, Vector3 local, float arrivalMeters = DefaultArrivalMeters) =>
        Enqueue(0u, arrivalMeters, walk: true, (cellId, local));

    /// <summary>Ends the request under way, stopping the moves its walk began.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _incoming = null;
            _stopIncoming = true;
        }
    }

    /// <summary>
    /// Advances planning and walking by one update frame, <paramref name="elapsedSeconds"/> long.
    /// Call it on the thread that owns the physics world.
    /// </summary>
    public void Tick(double elapsedSeconds)
    {
        _tick++;
        if (elapsedSeconds > 0d && double.IsFinite(elapsedSeconds))
            _seconds += elapsedSeconds;
        CollectBuild();
        CollectRoute();
        CollectPlaces();

        Request? incoming;
        bool stop;
        lock (_gate)
        {
            incoming = _incoming;
            stop = _stopIncoming;
            _incoming = null;
            _stopIncoming = false;
        }

        bool inWorld = _body.TrySample(out NavigationWalkBodySample sample);
        if (stop || incoming is not null)
            Cancel(incoming is null ? "stopped" : "a newer request replaced it");
        if (incoming is not null)
            Begin(incoming, inWorld);

        bool wantPlaces;
        lock (_gate)
            wantPlaces = _placesWanted;
        if (wantPlaces)
            KeepPlaces(inWorld, sample);

        if (_active is { } active)
        {
            if (inWorld)
                Advance(active, sample);
            else
                End(active, NavigationWalkState.Lost, "the character left the world");
            return;
        }
        if (inWorld && (ShowGrid || wantPlaces))
            KeepViewGrid(sample);
    }

    /// <summary>A square region holding both points with room around them, or false when they are too far apart.</summary>
    internal static bool TryChooseRegion(
        Vector3 from,
        Vector3 to,
        out float originX,
        out float originY,
        out float size) =>
        TryChooseSquare(
            new Vector2(MathF.Min(from.X, to.X), MathF.Min(from.Y, to.Y)),
            new Vector2(MathF.Max(from.X, to.X), MathF.Max(from.Y, to.Y)),
            MaximumRegion,
            out originX,
            out originY,
            out size);

    /// <summary>
    /// The region one stage of a walk to a goal too far away for one region is
    /// planned over: the largest region, holding the character with room behind
    /// it and reaching as far toward the goal as it can.
    /// </summary>
    internal static void ChooseStageRegion(
        Vector3 from,
        Vector3 to,
        out float originX,
        out float originY,
        out float size)
    {
        size = MaximumRegion;
        float half = size * 0.5f;
        var toward = new Vector2(to.X - from.X, to.Y - from.Y);
        float longest = MathF.Max(MathF.Abs(toward.X), MathF.Abs(toward.Y));
        Vector2 centre = new Vector2(from.X, from.Y)
            + (longest > 1e-3f ? toward * ((half - RegionMargin) / longest) : Vector2.Zero);
        originX = Snap(centre.X - half);
        originY = Snap(centre.Y - half);
    }

    /// <summary>A square region holding a rectangle with room around it, or false when it would be larger than <paramref name="largest"/>.</summary>
    private static bool TryChooseSquare(
        Vector2 minimum,
        Vector2 maximum,
        float largest,
        out float originX,
        out float originY,
        out float size)
    {
        float span = MathF.Max(maximum.X - minimum.X, maximum.Y - minimum.Y) + (2f * RegionMargin);
        size = MathF.Ceiling(MathF.Max(MinimumRegion, span) / 16f) * 16f;
        if (size > largest)
        {
            originX = 0f;
            originY = 0f;
            return false;
        }
        originX = Snap(((minimum.X + maximum.X) * 0.5f) - (size * 0.5f));
        originY = Snap(((minimum.Y + maximum.Y) * 0.5f) - (size * 0.5f));
        return true;
    }

    private long Enqueue(uint objectId, float arrivalMeters, bool walk, (uint CellId, Vector3 Local)? place = null)
    {
        if (!(arrivalMeters > 0f) || !float.IsFinite(arrivalMeters))
            arrivalMeters = DefaultArrivalMeters;
        lock (_gate)
        {
            long sequence = ++_sequence;
            _incoming = new Request(sequence, objectId, arrivalMeters, walk, place);
            _stopIncoming = false;
            _report = new NavigationWalkReport(
                sequence,
                NavigationWalkState.Planning,
                objectId,
                float.NaN,
                0,
                "waiting for the next frame");
            return sequence;
        }
    }

    private void Begin(Request request, bool inWorld)
    {
        if (!inWorld)
        {
            End(request, NavigationWalkState.Lost, "the character is not in the world");
            return;
        }
        bool located = request.Place is { } place
            ? _goals.TryLocatePlace(place.CellId, place.Local, out Vector3 goal)
            : _goals.TryLocate(request.ObjectId, out goal);
        if (!located)
        {
            End(request, NavigationWalkState.NoRoute, $"the client has no position for {Label(request)}");
            return;
        }

        request.Goal = goal;
        request.GoalKnown = true;
        _active = request;
        Route = null;
        Goal = (goal, request.ArrivalMeters);
        Publish(request, NavigationWalkState.Planning, "planning", float.NaN);
    }

    private void Advance(Request active, in NavigationWalkBodySample sample)
    {
        if (Paused(active, sample))
            return;
        if (active.WaitingOn is { } wait)
        {
            WaitForDoor(active, wait, sample);
            return;
        }
        if (active.ShuffleUntil > _seconds)
            return;
        if (_driver is { } driver)
        {
            Drive(active, driver, sample);
            return;
        }
        if (_building is not null || _routing is not null)
            return;
        if (sample.InPortalSpace)
        {
            End(active, NavigationWalkState.Lost, "the character entered portal space");
            return;
        }

        // A route starts from floor, so a walk asked for in the air plans from where the character lands.
        if (sample.Airborne)
        {
            active.AirborneSince ??= _seconds;
            if (_seconds - active.AirborneSince.Value < LandingWaitSeconds)
            {
                Publish(active, NavigationWalkState.Planning, "waiting for the character to land", float.NaN);
                return;
            }
        }
        else
        {
            active.AirborneSince = null;
        }

        bool inDungeon = TryMeasureDungeon(sample.CellId, out uint dungeon, out Vector2 cellsMinimum, out Vector2 cellsMaximum);
        uint gridDungeon = inDungeon ? dungeon : 0u;
        NavGrid? usable = _grid is { } built
            && built.Body == sample.Body
            && _gridDungeon == gridDungeon
            && built.Contains(sample.Position, CoverMargin)
            && !IsStale(built, gridDungeon)
                ? built
                : null;
        bool staged = false;
        if (usable is null || !usable.Contains(active.Goal, CoverMargin))
        {
            if (inDungeon)
            {
                if (TryChooseSquare(
                        Vector2.Min(cellsMinimum, Vector2.Min(Flat(sample.Position), Flat(active.Goal))),
                        Vector2.Max(cellsMaximum, Vector2.Max(Flat(sample.Position), Flat(active.Goal))),
                        MaximumDungeonRegion,
                        out float dungeonX,
                        out float dungeonY,
                        out float dungeonSize))
                {
                    BuildGrid(active, sample, dungeonX, dungeonY, dungeonSize, dungeon);
                }
                else
                {
                    End(
                        active,
                        NavigationWalkState.NoRoute,
                        $"the goal lies too far outside this dungeon, {HorizontalDistance(sample.Position, active.Goal):0} m away");
                }
                return;
            }
            if (TryChooseRegion(sample.Position, active.Goal, out float originX, out float originY, out float size))
            {
                BuildGrid(active, sample, originX, originY, size, dungeon: 0u);
                return;
            }
            if (usable is null || !ReachesToward(usable, sample.Position, active.Goal))
            {
                ChooseStageRegion(sample.Position, active.Goal, out originX, out originY, out size);
                BuildGrid(active, sample, originX, originY, size, dungeon: 0u);
                return;
            }
            staged = true;
        }

        SearchRoute(active, usable, sample, staged, detour: false);
    }

    /// <summary>
    /// Starts searching a grid for a route from where the character stands to the
    /// goal, or toward it for one stage, passing the creatures and players near the
    /// character where they stand now. A detour is searched while the walk goes on.
    /// </summary>
    private void SearchRoute(Request active, NavGrid grid, in NavigationWalkBodySample sample, bool staged, bool detour)
    {
        Vector3 from = sample.Position;
        Vector3 to = active.Goal;
        float arrival = PlanningRadius(active.ArrivalMeters);
        NavAvoidance[] avoid = [.. active.Avoid, .. active.PassingAvoid];
        active.PassingAvoid.Clear();
        NavAvoidance[] obstacles = Obstacles(grid, sample.Body, active.ObjectId);
        NavAvoidance[] crowd = Crowd(sample, active.ObjectId);
        active.PlannedObstacles = obstacles;
        active.PlannedCrowd = crowd;
        active.Detouring = detour;
        NavLeapAbility? leaps = sample.Leaps;
        active.Staged = staged;
        _routingFor = active;
        _routing = staged
            ? Task.Run(() => AroundObstacles(to, avoid, obstacles, spots => NavRouter.FindToward(grid, from, to, RegionMargin, spots, leaps, crowd)))
            : Task.Run(() => AroundObstacles(to, avoid, obstacles, spots => NavRouter.Find(grid, from, to, arrival, spots, leaps, crowd)));
    }

    /// <summary>The creatures and players near the character, each widened by the body's radius, for a route to pass.</summary>
    private NavAvoidance[] Crowd(in NavigationWalkBodySample sample, uint goalObjectId)
    {
        IReadOnlyList<NavAvoidance> found = _goals.FindCrowd(sample.Position, CrowdReach, goalObjectId);
        var crowd = new NavAvoidance[found.Count];
        for (int index = 0; index < found.Count; index++)
            crowd[index] = found[index] with { Radius = found[index].Radius + sample.Body.Radius };
        return crowd;
    }

    /// <summary>Whether a creature or player the route was not planned with stands across the next stretch of it.</summary>
    private bool CrowdSteppedOnto(Request active, RuntimeRouteDriver driver, in NavigationWalkBodySample sample)
    {
        if (driver.LegIndex >= driver.Legs.Count)
            return false;
        foreach (NavAvoidance spot in Crowd(sample, active.ObjectId))
        {
            if (HorizontalDistance(spot.Centre, sample.Position) > CrowdLookAheadMeters + spot.Radius
                || active.PlannedCrowd.Any(planned => HorizontalDistance(planned.Centre, spot.Centre) <= CrowdMovedMeters))
            {
                continue;
            }
            Vector3 from = sample.Position;
            float left = CrowdLookAheadMeters;
            for (int index = driver.LegIndex; index < driver.Legs.Count && left > 0f; index++)
            {
                Vector3 to = driver.Legs[index];
                float length = HorizontalDistance(from, to);
                Vector3 end = length > left ? Vector3.Lerp(from, to, left / length) : to;
                if (FlatDistanceToSegment(spot.Centre, from, end) < spot.Radius
                    && MathF.Abs(spot.Centre.Z - end.Z) <= NavigationObstacleHeight)
                {
                    return true;
                }
                left -= length;
                from = to;
            }
        }
        return false;
    }

    /// <summary>Starts planning a way around creatures on the route from where the character is, while the walk goes on.</summary>
    private void StartDetour(Request active, in NavigationWalkBodySample sample)
    {
        if (_grid is not { } grid
            || grid.Body != sample.Body
            || !grid.Contains(sample.Position, CoverMargin)
            || (!active.Staged && !grid.Contains(active.Goal, CoverMargin)))
        {
            return;
        }
        active.DetouredAt = _seconds;
        SearchRoute(active, grid, sample, active.Staged, detour: true);
    }

    /// <summary>
    /// A route's legs from where the character is once planning has taken a moment:
    /// the character's position, then the leg ends it has not yet passed.
    /// </summary>
    private static List<Vector3> OnwardLegs(NavRoute route, Vector3 position, out int skipped)
    {
        int first = 1;
        while (first < route.Legs.Count - 1
            && Vector2.Dot(Flat(route.Legs[first]) - Flat(position), Flat(route.Legs[first]) - Flat(route.Legs[first - 1])) <= 0f)
        {
            first++;
        }
        skipped = first - 1;
        var legs = new List<Vector3>(route.Legs.Count - skipped) { position };
        for (int index = first; index < route.Legs.Count; index++)
            legs.Add(route.Legs[index]);
        return legs;
    }

    private static float FlatDistanceToSegment(Vector3 point, Vector3 from, Vector3 to)
    {
        Vector2 start = Flat(from);
        Vector2 along = Flat(to) - start;
        float lengthSquared = along.LengthSquared();
        float t = lengthSquared > 1e-6f ? Math.Clamp(Vector2.Dot(Flat(point) - start, along) / lengthSquared, 0f, 1f) : 0f;
        return Vector2.Distance(Flat(point), start + (along * t));
    }

    private void Drive(Request active, RuntimeRouteDriver driver, in NavigationWalkBodySample sample)
    {
        if (_doors is not null
            && _tick % DoorCheckTicks == 0
            && !driver.IsLeaping
            && ClosedDoorAhead(driver, sample) is { } door
            && !active.AvoidedDoors.Contains(door.ObjectId))
        {
            Apply(driver.Cancel());
            _driver = null;
            OpenDoor(active, door, sample.Body.Radius);
            return;
        }
        if (active.Shuffles > 0 && HorizontalDistance(sample.Position, active.ShuffledAt) >= ShuffleProgressMeters)
            active.Shuffles = 0;
        if (_tick % CrowdCheckTicks == 0
            && !driver.IsLeaping
            && _routing is null
            && _seconds - active.DetouredAt >= DetourIntervalSeconds
            && CrowdSteppedOnto(active, driver, sample))
        {
            StartDetour(active, sample);
        }
        bool wasLeaping = driver.IsLeaping;
        RuntimeRouteDriveStep step = driver.Advance(new RuntimeRouteDriveSample(
            sample.Position,
            sample.HeadingDegrees,
            sample.Moves,
            sample.InPortalSpace,
            sample.Airborne,
            sample.Turning));
        if (!Apply(step))
        {
            Apply(driver.Cancel());
            End(active, NavigationWalkState.Stopped, "the client refused a move");
            return;
        }
        if (driver.IsLeaping && !wasLeaping)
        {
            Vector3 takeoff = driver.Legs[driver.LegIndex - 1];
            Vector3 landing = driver.Legs[driver.LegIndex];
            _say?.Invoke(
                $"Walk to {Label(active)}: leaping from {takeoff.Z:0.0} m to {landing.Z:0.0} m, "
                + $"{HorizontalDistance(takeoff, landing):0.0} m on");
        }
        else if (wasLeaping && !driver.IsLeaping && driver.State == RuntimeRouteDriveState.Driving)
        {
            _say?.Invoke(
                $"Walk to {Label(active)}: the leap landed at {sample.Position.Z:0.0} m, "
                + $"{driver.LandingError:0.0} m from where it was planned");
        }

        switch (driver.State)
        {
            case RuntimeRouteDriveState.Driving:
                Publish(active, NavigationWalkState.Walking, "walking", Remaining(active, driver, sample.Position));
                break;
            case RuntimeRouteDriveState.Arrived when active.Staged:
                NextStage(active, sample);
                break;
            case RuntimeRouteDriveState.Arrived:
                if (active.Place is null)
                    Face(Locate(active), sample);
                End(
                    active,
                    NavigationWalkState.Arrived,
                    active.ArrivalReason is { } why ? $"arrived; {why}" : "arrived");
                break;
            case RuntimeRouteDriveState.Blocked:
                Blocked(active, sample);
                break;
            case RuntimeRouteDriveState.Interrupted:
                End(active, NavigationWalkState.Interrupted, "the player moved the character");
                break;
            case RuntimeRouteDriveState.LandedElsewhere:
                _driver = null;
                active.Builds = 0;
                string elsewhere = $"the leap landed at {sample.Position.Z:0.0} m, {driver.LandingError:0.0} m from where it was planned; planning on from there";
                Publish(active, NavigationWalkState.Planning, elsewhere, float.NaN);
                _say?.Invoke($"Walk to {Label(active)}: {elsewhere}");
                break;
            default:
                End(active, NavigationWalkState.Lost, "the character entered portal space");
                break;
        }
    }

    /// <summary>
    /// Opens a closed door beside the spot ahead of a character that stopped making
    /// progress. Otherwise keeps the next plans out of that spot, notes what stands
    /// beside it, and plans again or gives up.
    /// </summary>
    private void Blocked(Request active, in NavigationWalkBodySample sample)
    {
        float radians = sample.HeadingDegrees * (MathF.PI / 180f);
        Vector3 spot = sample.Position
            + (new Vector3(MathF.Sin(radians), MathF.Cos(radians), 0f) * BlockedSpotAhead);
        active.BlockedBy = _goals.TryFindBlocker(spot, BlockerSearchRadius, out NavigationBlocker blocker)
            ? blocker
            : null;
        if (_doors is not null && active.BlockedBy is { IsClosedDoor: true } door)
        {
            _driver = null;
            OpenDoor(active, new NavigationDoor(door.ObjectId, door.Name, door.Position, door.Radius), sample.Body.Radius);
            return;
        }
        if (active.BlockedBy is { Moves: true } mover)
        {
            if (active.Shuffles < MaximumShuffles && !mover.Hostile)
            {
                active.Shuffles++;
                active.ShuffledAt = sample.Position;
                active.ShuffleUntil = _seconds + ShuffleWaitSeconds;
                active.Builds = 0;
                _driver = null;
                string waiting = $"{mover.Name} (0x{mover.ObjectId:X8}) stands in the way; waiting for it to move ({active.Shuffles} of {MaximumShuffles})";
                Publish(active, NavigationWalkState.Walking, waiting, float.NaN);
                _say?.Invoke($"Walk to {Label(active)}: {waiting}");
                return;
            }
            if (mover.Radius > 0f)
                active.PassingAvoid.Add(new NavAvoidance(mover.Position, mover.Radius + sample.Body.Radius));
        }
        else
        {
            if (active.BlockedBy is { Radius: > 0f } solid)
            {
                active.Avoid.Add(new NavAvoidance(solid.Position, solid.Radius + sample.Body.Radius));
                active.AvoidedObject = solid;
            }
            active.Avoid.Add(new NavAvoidance(spot, BlockedSpotRadius));
        }
        if (active.Replans >= MaximumReplans)
        {
            End(active, NavigationWalkState.Blocked, BlockedReason(active));
            return;
        }

        active.Replans++;
        active.Builds = 0;
        _driver = null;
        string again = $"{BlockedReason(active)}; planning again ({active.Replans} of {MaximumReplans})";
        Publish(active, NavigationWalkState.Planning, again, float.NaN);
        _say?.Invoke($"Walk to {Label(active)}: {again}");
    }

    /// <summary>
    /// Stops a walk where the character stands while something else needs the
    /// character, and plans again from there once nothing has needed it for
    /// <see cref="PauseSettleSeconds"/>. A leap already in the air lands first. A
    /// route planned from where the character stood before is not walked, and a
    /// door the walk was opening is judged again on the new plan.
    /// </summary>
    private bool Paused(Request active, in NavigationWalkBodySample sample)
    {
        if (!active.Walk || _driver is { IsLeaping: true })
            return false;
        string? need = PausedBy?.Invoke();
        if (need is null && active.PausedFor is null)
            return false;
        if (sample.InPortalSpace)
        {
            End(active, NavigationWalkState.Lost, "the character entered portal space");
            return true;
        }
        if (need is not null)
        {
            active.FreeSince = null;
            if (_driver is { } driver)
            {
                Apply(driver.Cancel());
                _driver = null;
            }
            if (ReferenceEquals(_routingFor, active))
                _routingFor = null;
            if (active.WaitingOn is { Appraising: false } opening)
                active.DoorUses[opening.Door.ObjectId]--;
            active.WaitingOn = null;
            if (need != active.PausedFor)
            {
                active.PausedFor = need;
                string waiting = $"waiting: {need}";
                Publish(active, NavigationWalkState.Waiting, waiting, float.NaN);
                _say?.Invoke($"Walk to {Label(active)}: {waiting}");
            }
            return true;
        }
        active.FreeSince ??= _seconds;
        if (_seconds - active.FreeSince.Value < PauseSettleSeconds)
            return true;
        active.PausedFor = null;
        active.FreeSince = null;
        active.Builds = 0;
        const string again = "nothing needs the character any more; planning on from where it stands";
        Publish(active, NavigationWalkState.Planning, again, float.NaN);
        _say?.Invoke($"Walk to {Label(active)}: {again}");
        return true;
    }

    /// <summary>A closed door within the look-ahead along the leg the character is walking, if there is one.</summary>
    private NavigationDoor? ClosedDoorAhead(RuntimeRouteDriver driver, in NavigationWalkBodySample sample)
    {
        if (driver.LegIndex >= driver.Legs.Count)
            return null;
        var from = new Vector2(sample.Position.X, sample.Position.Y);
        Vector3 end = driver.Legs[driver.LegIndex];
        Vector2 toward = new Vector2(end.X, end.Y) - from;
        float length = toward.Length();
        if (length < 1e-3f)
            return null;
        Vector2 ahead = from + (toward * (MathF.Min(length, DoorLookAheadMeters) / length));
        return _doors!.TryFindClosedDoor(sample.Position, new Vector3(ahead, end.Z), DoorCorridorMeters, out NavigationDoor door)
            ? door
            : null;
    }

    /// <summary>
    /// Has the client open a closed door the walk has stopped at and waits for it
    /// to open. A door the client has never appraised is appraised first, and one
    /// that is locked, or keeps closing, is planned around.
    /// </summary>
    private void OpenDoor(Request active, NavigationDoor door, float bodyRadius)
    {
        bool? locked = _doors!.IsLocked(door.ObjectId);
        if (locked is null && active.AppraisedDoors.Add(door.ObjectId) && _doors.Appraise(door.ObjectId))
        {
            active.WaitingOn = new DoorWait(door, _tick, Used: false, _seconds + DoorAppraisalWaitSeconds, Appraising: true);
            string checking = $"checking whether {door.Name} (0x{door.ObjectId:X8}) is locked";
            Publish(active, NavigationWalkState.Walking, checking, float.NaN);
            _say?.Invoke($"Walk to {Label(active)}: {checking}");
            return;
        }
        if (locked == true)
        {
            AvoidDoor(active, door, bodyRadius, "a locked door");
            return;
        }
        int uses = active.DoorUses.GetValueOrDefault(door.ObjectId);
        if (uses >= MaximumDoorUses)
        {
            AvoidDoor(active, door, bodyRadius, "a door kept closing");
            return;
        }
        active.DoorUses[door.ObjectId] = uses + 1;
        active.WaitingOn = new DoorWait(door, _tick + DoorUseSettleTicks, Used: false, DeadlineSeconds: 0d);
        string opening = $"opening {door.Name} (0x{door.ObjectId:X8})";
        Publish(active, NavigationWalkState.Walking, opening, float.NaN);
        _say?.Invoke($"Walk to {Label(active)}: {opening}");
    }

    /// <summary>
    /// Uses the door once the character's stop has landed, and plans again once
    /// the door is open. A door still closed when the wait runs out ends the walk
    /// blocked, since using a door again closes one that opened late.
    /// </summary>
    private void WaitForDoor(Request active, DoorWait wait, in NavigationWalkBodySample sample)
    {
        if (sample.InPortalSpace)
        {
            End(active, NavigationWalkState.Lost, "the character entered portal space");
            return;
        }
        if (_doors!.IsOpen(wait.Door.ObjectId))
        {
            active.WaitingOn = null;
            active.Builds = 0;
            Publish(active, NavigationWalkState.Planning, $"{wait.Door.Name} is open, planning again", float.NaN);
            return;
        }
        if (wait.Appraising)
        {
            if (_doors.IsLocked(wait.Door.ObjectId) is null && _seconds < wait.DeadlineSeconds)
                return;
            active.WaitingOn = null;
            OpenDoor(active, wait.Door, sample.Body.Radius);
            return;
        }
        if (!wait.Used)
        {
            if (_tick < wait.UseAtTick)
                return;
            _doors.Use(wait.Door.ObjectId);
            active.WaitingOn = wait with { Used = true, DeadlineSeconds = _seconds + DoorOpenWaitSeconds };
            return;
        }
        if (_seconds >= wait.DeadlineSeconds)
            AvoidDoor(active, wait.Door, sample.Body.Radius, "a closed door would not open");
    }

    /// <summary>
    /// Keeps the walk's later plans out of a door that is locked or would not open,
    /// widened by the body's radius, and plans again. A door whose footprint is not
    /// known, or that the walk already kept out of, ends the walk blocked.
    /// </summary>
    private void AvoidDoor(Request active, NavigationDoor door, float bodyRadius, string why)
    {
        active.WaitingOn = null;
        if (door.Radius <= 0f || !active.AvoidedDoors.Add(door.ObjectId))
        {
            BlockedAtDoor(active, door, why);
            return;
        }
        active.Avoid.Add(new NavAvoidance(door.Position, door.Radius + bodyRadius));
        active.AvoidedDoor = (door, why);
        active.Builds = 0;
        _driver = null;
        string around = $"{why}: {door.Name} (0x{door.ObjectId:X8}); planning a way around it";
        Publish(active, NavigationWalkState.Planning, around, float.NaN);
        _say?.Invoke($"Walk to {Label(active)}: {around}");
    }

    private void BlockedAtDoor(Request active, NavigationDoor door, string why)
    {
        active.WaitingOn = null;
        active.BlockedBy = new NavigationBlocker(door.ObjectId, door.Name, IsClosedDoor: true);
        End(active, NavigationWalkState.Blocked, $"{why}: {door.Name} (0x{door.ObjectId:X8})");
    }

    /// <summary>Plans the next stage of a walk from where its last stage ended, or gives up after too many.</summary>
    private void NextStage(Request active, in NavigationWalkBodySample sample)
    {
        _driver = null;
        active.Staged = false;
        active.Builds = 0;
        active.Stages++;
        active.Goal = Locate(active);
        Goal = (active.Goal, active.ArrivalMeters);
        float away = HorizontalDistance(sample.Position, active.Goal);
        if (active.Stages >= MaximumStages)
        {
            End(active, NavigationWalkState.NoRoute, $"the goal was still {away:0} m away after {active.Stages} stages");
            return;
        }
        string next = $"stage {active.Stages} walked; planning the next toward the goal, {away:0} m away";
        Publish(active, NavigationWalkState.Planning, next, float.NaN);
        _say?.Invoke($"Walk to {Label(active)}: {next}");
    }

    private void BuildGrid(
        Request active,
        in NavigationWalkBodySample sample,
        float originX,
        float originY,
        float size,
        uint dungeon)
    {
        if (active.Builds >= MaximumBuildsPerPlan)
        {
            End(active, NavigationWalkState.NoRoute, "no grid covers both the character and the goal");
            return;
        }
        active.Builds++;
        if (!StartBuild(originX, originY, size, sample.Body, dungeon))
            End(active, NavigationWalkState.NoRoute, "no collision is loaded around the character");
    }

    /// <summary>
    /// The landblock holding the sealed dungeon a cell lies in, and the horizontal
    /// extent of every cell there, or false outside a sealed dungeon or before its
    /// cells are resident.
    /// </summary>
    private bool TryMeasureDungeon(uint cellId, out uint landblockId, out Vector2 minimum, out Vector2 maximum)
    {
        landblockId = (cellId & 0xFFFF0000u) | 0xFFFFu;
        minimum = default;
        maximum = default;
        if (_isSealedDungeon is null || cellId == 0u)
            return false;
        if (cellId != _classifiedCellId)
        {
            _classifiedCellId = cellId;
            _classifiedSealed = _isSealedDungeon(cellId);
        }
        return _classifiedSealed && NavGeometry.TryMeasureCells(_physics, landblockId, out minimum, out maximum);
    }

    /// <summary>The objects the server placed on a grid, each widened by the body's radius, for a route to keep out of.</summary>
    private NavAvoidance[] Obstacles(NavGrid grid, NavBody body, uint goalObjectId)
    {
        float half = grid.Size * 0.5f;
        var centre = new Vector3(grid.OriginX + half, grid.OriginY + half, 0f);
        IReadOnlyList<NavAvoidance> found = _goals.FindObstacles(centre, half * MathF.Sqrt(2f), goalObjectId);
        var obstacles = new NavAvoidance[found.Count];
        for (int index = 0; index < found.Count; index++)
            obstacles[index] = found[index] with { Radius = found[index].Radius + body.Radius };
        return obstacles;
    }

    /// <summary>
    /// The route that keeps out of the objects the server placed, unless only a
    /// route through them arrives, or one through them ends more than
    /// <see cref="ObstacleDetourReach"/> nearer the goal.
    /// </summary>
    private static NavRoute AroundObstacles(
        Vector3 goal,
        NavAvoidance[] avoid,
        NavAvoidance[] obstacles,
        Func<IReadOnlyList<NavAvoidance>, NavRoute> find)
    {
        if (obstacles.Length == 0)
            return find(avoid);
        NavRoute around = find([.. avoid, .. obstacles]);
        if (around.Outcome == NavRouteOutcome.Routed && around.Reason == "routed")
            return around;
        NavRoute through = find(avoid);
        if (around.Outcome != NavRouteOutcome.Routed)
            return through;
        if (through.Outcome != NavRouteOutcome.Routed)
            return around;
        return HorizontalDistance(around.Legs[^1], goal) <= HorizontalDistance(through.Legs[^1], goal) + ObstacleDetourReach
            ? around
            : through;
    }

    /// <summary>Whether a grid reaches far enough toward a goal beyond it to plan a stage over from where the character stands.</summary>
    private static bool ReachesToward(NavGrid grid, Vector3 from, Vector3 goal)
    {
        if (grid.Size <= 2f * RegionMargin)
            return false;
        float x = Math.Clamp(goal.X, grid.OriginX + RegionMargin, grid.OriginX + grid.Size - RegionMargin);
        float y = Math.Clamp(goal.Y, grid.OriginY + RegionMargin, grid.OriginY + grid.Size - RegionMargin);
        return HorizontalDistance(from, goal) - HorizontalDistance(new Vector3(x, y, 0f), goal)
            >= 2f * MinimumStageProgress;
    }

    private bool Apply(in RuntimeRouteDriveStep step)
    {
        if (step.StopTravel)
            _body.StopMove(RuntimeMoveChannel.Travel);
        if (step.StopTurn)
            _body.StopMove(RuntimeMoveChannel.Turn);
        bool accepted = true;
        if (step.Travel is { } travel)
            accepted &= _body.BeginMove(travel);
        if (step.Turn is { } turn)
            accepted &= _body.BeginMove(turn);
        if (step.Jump is { } power)
            accepted &= _body.BeginJump(power, step.JumpPace);
        return accepted;
    }

    private void Face(Vector3 goal, in NavigationWalkBodySample sample)
    {
        float dx = goal.X - sample.Position.X;
        float dy = goal.Y - sample.Position.Y;
        if ((dx * dx) + (dy * dy) < 0.01f)
            return;
        float heading = MathF.Atan2(dx, dy) * (180f / MathF.PI);
        float error = ((((heading - sample.HeadingDegrees) % 360f) + 540f) % 360f) - 180f;
        if (MathF.Abs(error) <= FaceToleranceDegrees)
            return;
        _body.BeginMove(new RuntimeMoveRequest(
            error > 0f ? RuntimeMoveDirection.TurnRight : RuntimeMoveDirection.TurnLeft,
            RuntimeMovePace.Run,
            MathF.Abs(error)));
    }

    private void Cancel(string reason)
    {
        if (_active is not { } active)
            return;
        if (_driver is { } driver)
            Apply(driver.Cancel());
        End(active, NavigationWalkState.Stopped, reason);
    }

    private void End(Request request, NavigationWalkState state, string reason)
    {
        if (ReferenceEquals(_active, request))
        {
            _active = null;
            _driver = null;
        }
        float remaining = request.GoalKnown && _body.TrySample(out NavigationWalkBodySample sample)
            ? HorizontalDistance(sample.Position, Locate(request))
            : float.NaN;
        Publish(request, state, reason, remaining);
        _say?.Invoke($"{(request.Walk ? "Walk" : "Route")} to {Label(request)}: {reason}");
        if (request.Drives.Count > 0)
        {
            _say?.Invoke(
                $"Walk to {Label(request)}: ran around {request.Drives.Sum(drive => drive.CornersRunAround)} corners "
                + $"and turned in place at {request.Drives.Sum(drive => drive.CornersTurnedInPlace)}");
        }
    }

    private void Publish(Request request, NavigationWalkState state, string reason, float remainingMeters)
    {
        lock (_gate)
        {
            if (request.Sequence < _report.Sequence)
                return;
            _report = new NavigationWalkReport(
                request.Sequence,
                state,
                request.ObjectId,
                remainingMeters,
                request.Replans,
                reason,
                state == NavigationWalkState.Blocked ? request.BlockedBy?.ObjectId ?? 0u : 0u);
        }
    }

    /// <summary>
    /// Whether the resident landblocks a grid over a region was built from have
    /// changed since, or the landblock of a grid over a whole sealed dungeon has left.
    /// </summary>
    private bool IsStale(NavGrid grid, uint dungeon)
    {
        if (dungeon != 0u)
            return !NavGeometry.TryMeasureCells(_physics, dungeon, out _, out _);
        IReadOnlyList<uint> resident = NavGeometry.OverlappingLandblocks(_physics, grid.OriginX, grid.OriginY, grid.Size);
        if (resident.Count != grid.LandblockIds.Count)
            return true;
        foreach (uint landblockId in resident)
        {
            if (!grid.LandblockIds.Contains(landblockId))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Starts building a grid over a region, or over the whole of the sealed
    /// dungeon whose landblock <paramref name="dungeon"/> names when it is not zero.
    /// </summary>
    private bool StartBuild(float originX, float originY, float size, NavBody body, uint dungeon)
    {
        NavGeometry? geometry = dungeon == 0u
            ? NavGeometry.Capture(_physics, originX, originY, size)
            : NavGeometry.CaptureDungeon(_physics, dungeon, originX, originY, size);
        if (geometry is null)
            return false;
        _buildingDungeon = dungeon;
        _building = Task.Run(() => NavGrid.Build(geometry, body));
        return true;
    }

    /// <summary>Keeps a grid around the character while it is shown: the whole dungeon inside a sealed one.</summary>
    private void KeepViewGrid(in NavigationWalkBodySample sample)
    {
        if (_building is not null || _tick < _viewRetryTick)
            return;
        if (TryMeasureDungeon(sample.CellId, out uint dungeon, out Vector2 minimum, out Vector2 maximum))
        {
            if (_grid is { } whole && whole.Body == sample.Body && _gridDungeon == dungeon && !IsStale(whole, dungeon))
                return;
            Vector2 at = Flat(sample.Position);
            if (!TryChooseSquare(
                    Vector2.Min(minimum, at),
                    Vector2.Max(maximum, at),
                    MaximumDungeonRegion,
                    out float originX,
                    out float originY,
                    out float size)
                || !StartBuild(originX, originY, size, sample.Body, dungeon))
            {
                _viewRetryTick = _tick + ViewRetryTicks;
            }
            return;
        }
        if (_grid is { } grid
            && grid.Body == sample.Body
            && _gridDungeon == 0u
            && grid.Contains(sample.Position, ViewRegion / 8f))
        {
            return;
        }
        float half = ViewRegion * 0.5f;
        if (!StartBuild(Snap(sample.Position.X - half), Snap(sample.Position.Y - half), ViewRegion, sample.Body, dungeon: 0u))
            _viewRetryTick = _tick + ViewRetryTicks;
    }

    /// <summary>
    /// Finds the spots the character can walk to over the grid that covers where it stands,
    /// off the game's thread, once there is such a grid, and again once the character has
    /// moved on or the places have grown old.
    /// </summary>
    private void KeepPlaces(bool inWorld, in NavigationWalkBodySample sample)
    {
        if (!inWorld || sample.InPortalSpace)
        {
            PublishPlaces(
                new NavigationPlacesReport(
                    NavigationPlacesState.Unavailable,
                    [],
                    false,
                    inWorld ? "the character is in portal space" : "the character is not in the world"),
                wanted: false);
            return;
        }
        if (_placing is not null)
            return;
        bool inDungeon = TryMeasureDungeon(sample.CellId, out uint dungeon, out _, out _);
        uint gridDungeon = inDungeon ? dungeon : 0u;
        if (_grid is not { } grid
            || grid.Body != sample.Body
            || _gridDungeon != gridDungeon
            || !grid.Contains(sample.Position, CoverMargin)
            || IsStale(grid, gridDungeon))
        {
            PublishPlaces(Places with { State = NavigationPlacesState.Mapping, Reason = "mapping the ground around the character" }, wanted: true);
            return;
        }
        if (ReferenceEquals(grid, _placesGrid)
            && Vector3.Distance(sample.Position, _placesFrom) <= PlacesMovedMeters
            && _seconds - _placesAt <= PlacesFreshSeconds)
        {
            lock (_gate)
                _placesWanted = false;
            return;
        }
        int start = grid.FindWalkableNode(sample.Position, PlacesStartRadius, PlacesStartHeight);
        if (start < 0)
        {
            _placesGrid = grid;
            _placesFrom = sample.Position;
            _placesAt = _seconds;
            _goals.TryGlobalOf(sample.Position, out Vector3 standing);
            PublishPlaces(
                new NavigationPlacesReport(NavigationPlacesState.Ready, [], inDungeon, "the character does not stand on floor the grid holds")
                {
                    FromGlobal = standing,
                },
                wanted: false);
            return;
        }
        _placingGrid = grid;
        _placingFrom = sample.Position;
        _placingInDungeon = inDungeon;
        _placing = Task.Run(() => NavPlaces.Find(grid, start));
    }

    private void CollectPlaces()
    {
        if (_placing is not { IsCompleted: true } placing)
            return;
        _placing = null;
        NavGrid? grid = _placingGrid;
        _placingGrid = null;
        if (!placing.IsCompletedSuccessfully)
        {
            PublishPlaces(
                new NavigationPlacesReport(
                    NavigationPlacesState.Unavailable,
                    [],
                    _placingInDungeon,
                    $"finding places failed: {placing.Exception?.GetBaseException().Message ?? "it was cancelled"}"),
                wanted: false);
            return;
        }
        var places = new List<NavigationPlace>(placing.Result.Count);
        var indexOf = new int[placing.Result.Count];
        for (int index = 0; index < placing.Result.Count; index++)
        {
            NavPlace place = placing.Result[index];
            indexOf[index] = -1;
            if (_goals.TryGlobalOf(place.Position, out Vector3 global))
            {
                indexOf[index] = places.Count;
                places.Add(new NavigationPlace(
                    global,
                    place.WalkMeters,
                    NavigationPlace.KindOf(place.Kind),
                    place.AreaSquareMeters,
                    place.WidthMeters,
                    place.RiseMeters,
                    place.Exits));
            }
        }
        for (int index = 0; index < placing.Result.Count; index++)
        {
            if (indexOf[index] >= 0)
            {
                places[indexOf[index]] = places[indexOf[index]] with
                {
                    Neighbours = [.. placing.Result[index].Neighbours.Select(neighbour => indexOf[neighbour]).Where(neighbour => neighbour >= 0)],
                };
            }
        }
        if (!_placingInDungeon)
            AddGroundAround(_placingFrom, places);
        _placesGrid = grid;
        _placesFrom = _placingFrom;
        _placesAt = _seconds;
        _goals.TryGlobalOf(_placingFrom, out Vector3 from);
        PublishPlaces(
            new NavigationPlacesReport(
                NavigationPlacesState.Ready,
                places,
                _placingInDungeon,
                places.Count == 0 ? "no floor a walk reaches was found" : "found")
            {
                FromGlobal = from,
            },
            wanted: false);
    }

    /// <summary>A landblock whose middle lies at least this deep under water is given as water.</summary>
    private const float PlacesWaterMeters = 0.5f;

    /// <summary>
    /// Outdoors, the loaded landblocks beside the character's own, at their middles on the
    /// ground, and the buildings in its landblock and those beside it, at their origins, for
    /// going farther than the ground one grid covers.
    /// </summary>
    private void AddGroundAround(Vector3 from, List<NavigationPlace> places)
    {
        if (!_physics.TryGetLandblockContext(from.X, from.Y, out uint landblockId, out float offsetX, out float offsetY))
            return;
        int blockX = (int)((landblockId >> 24) & 0xFFu);
        int blockY = (int)((landblockId >> 16) & 0xFFu);
        float half = NavGeometry.LandblockSize * 0.5f;
        for (int north = -1; north <= 1; north++)
        {
            for (int east = -1; east <= 1; east++)
            {
                int x = blockX + east;
                int y = blockY + north;
                if ((east == 0 && north == 0) || x is < 0 or > 255 || y is < 0 or > 255)
                    continue;
                float middleX = offsetX + (east * NavGeometry.LandblockSize) + half;
                float middleY = offsetY + (north * NavGeometry.LandblockSize) + half;
                if (_physics.SampleTerrainZ(middleX, middleY) is not { } ground
                    || !_goals.TryGlobalOf(new Vector3(middleX, middleY, ground), out Vector3 middle))
                {
                    continue;
                }
                places.Add(new NavigationPlace(
                    middle,
                    float.NaN,
                    NavigationPlaceKind.Landblock,
                    NavGeometry.LandblockSize * NavGeometry.LandblockSize,
                    NavGeometry.LandblockSize,
                    0f,
                    0)
                {
                    LandblockId = ((uint)x << 24) | ((uint)y << 16) | 0xFFFFu,
                    IsWater = _physics.SampleWaterDepth(middleX, middleY) >= PlacesWaterMeters,
                });
            }
        }
        if (_physics.DataCache is not { } cache)
            return;
        foreach (uint cellId in cache.BuildingIds)
        {
            if (Math.Abs((int)((cellId >> 24) & 0xFFu) - blockX) > 1
                || Math.Abs((int)((cellId >> 16) & 0xFFu) - blockY) > 1
                || cache.GetBuilding(cellId) is not { } building
                || !_goals.TryGlobalOf(building.WorldTransform.Translation, out Vector3 origin))
            {
                continue;
            }
            places.Add(new NavigationPlace(origin, float.NaN, NavigationPlaceKind.Building, 0f, 0f, 0f, building.Portals.Count)
            {
                LandblockId = (cellId & 0xFFFF0000u) | 0xFFFFu,
            });
        }
    }

    private void PublishPlaces(NavigationPlacesReport report, bool wanted)
    {
        lock (_gate)
        {
            _places = report;
            _placesWanted = wanted;
        }
    }

    private void CollectBuild()
    {
        if (_building is not { IsCompleted: true } building)
            return;
        _building = null;
        if (!building.IsCompletedSuccessfully)
        {
            string message = building.Exception?.GetBaseException().Message ?? "it was cancelled";
            _say?.Invoke($"Navmesh: the build failed: {message}");
            if (_active is { } active)
                End(active, NavigationWalkState.NoRoute, $"the grid build failed: {message}");
            return;
        }

        NavGrid grid = building.Result;
        _grid = grid;
        _gridDungeon = _buildingDungeon;
        NavGridBuildReport report = grid.Report;
        _say?.Invoke(
            $"Navmesh: {report.Nodes} standing points ({report.ClearNodes} clear) over {grid.Size:0} m "
            + $"from {grid.LandblockIds.Count} landblocks in {report.Milliseconds:0} ms");
    }

    private void CollectRoute()
    {
        if (_routing is not { IsCompleted: true } routing)
            return;
        _routing = null;
        Request? requester = _routingFor;
        _routingFor = null;
        if (requester is null || !ReferenceEquals(requester, _active))
            return;
        if (requester.Detouring)
        {
            requester.Detouring = false;
            if (_driver is not { State: RuntimeRouteDriveState.Driving, IsLeaping: false }
                || !routing.IsCompletedSuccessfully
                || routing.Result is not { Outcome: NavRouteOutcome.Routed } detour
                || detour.Legs.Count < 2
                || !_body.TrySample(out NavigationWalkBodySample now))
            {
                return;
            }
            Route = detour;
            List<Vector3> onward = OnwardLegs(detour, now.Position, out int skipped);
            RuntimeRouteLeap[] leaps = [.. detour.Leaps
                .Where(leap => leap.LegIndex - skipped >= 1)
                .Select(leap => new RuntimeRouteLeap(leap.LegIndex - skipped, leap.Power, leap.Run))];
            _driver = Drive(requester, new RuntimeRouteDriver(onward, leaps, takeOverMoves: true, canCutAlong: CornerCuts()));
            _say?.Invoke($"Walk to {Label(requester)}: planned a way around a creature or player on the route");
            return;
        }
        if (!routing.IsCompletedSuccessfully)
        {
            End(
                requester,
                NavigationWalkState.NoRoute,
                $"the search failed: {routing.Exception?.GetBaseException().Message ?? "it was cancelled"}");
            return;
        }

        NavRoute route = routing.Result;
        Route = route;
        if (route.Outcome == NavRouteOutcome.Routed)
        {
            requester.AvoidedDoor = null;
            requester.AvoidedObject = null;
            int passed = PassedObstacles(route, requester.PlannedObstacles);
            if (passed > 0)
                _say?.Invoke($"Route: no way around {passed} of the objects the server placed arrives, so the route passes them");
        }
        if (route.Outcome != NavRouteOutcome.Routed)
        {
            if (requester.AvoidedDoor is { } avoided)
            {
                requester.BlockedBy = new NavigationBlocker(avoided.Door.ObjectId, avoided.Door.Name, IsClosedDoor: true);
                End(
                    requester,
                    NavigationWalkState.Blocked,
                    $"{avoided.Why}: {avoided.Door.Name} (0x{avoided.Door.ObjectId:X8}), and no other way around it was found");
            }
            else if (requester.AvoidedObject is { } solid)
            {
                requester.BlockedBy = solid;
                End(
                    requester,
                    NavigationWalkState.Blocked,
                    $"{solid.Name} (0x{solid.ObjectId:X8}) stands in the way, and no other way around it was found");
            }
            else if (requester.Replans > 0)
                End(requester, NavigationWalkState.Blocked, $"{BlockedReason(requester)}, and no other way around it was found");
            else
                End(requester, NavigationWalkState.NoRoute, route.Reason);
            return;
        }
        _say?.Invoke(
            $"Route: {route.Legs.Count - 1} legs, {route.Length:0.0} m, "
            + $"{route.Expansions} expansions in {route.Milliseconds:0} ms");
        if (requester.Staged)
        {
            float left = HorizontalDistance(route.Legs[^1], requester.Goal);
            float before = _body.TrySample(out NavigationWalkBodySample here)
                ? HorizontalDistance(here.Position, requester.Goal)
                : left;
            if (before - left < MinimumStageProgress)
            {
                End(requester, NavigationWalkState.NoRoute, $"no way on toward the goal was found; it is {before:0} m away");
                return;
            }
            if (!requester.Walk)
            {
                End(
                    requester,
                    NavigationWalkState.Planned,
                    $"a route was found for the first {route.Length:0} m; the rest is planned on the way");
                return;
            }
            _driver = Drive(requester, new RuntimeRouteDriver(route.Legs, LeapsOf(route), canCutAlong: CornerCuts()));
            Publish(requester, NavigationWalkState.Walking, "walking", route.Length + left);
            return;
        }
        if (!requester.Walk)
        {
            End(requester, NavigationWalkState.Planned, "a route was found");
            return;
        }
        if (route.Legs.Count < 2)
        {
            if (requester.Place is null && _body.TrySample(out NavigationWalkBodySample sample))
                Face(Locate(requester), sample);
            End(requester, NavigationWalkState.Arrived, "already there");
            return;
        }
        requester.ArrivalReason = route.Reason == "routed" ? null : route.Reason;
        _driver = Drive(requester, new RuntimeRouteDriver(route.Legs, LeapsOf(route), canCutAlong: CornerCuts()));
        Publish(requester, NavigationWalkState.Walking, "walking", route.Length);
    }

    private static string BlockedReason(Request request) =>
        request.BlockedBy is { } blocker
            ? $"the character stopped making progress beside {(blocker.IsClosedDoor ? "a closed door, " : blocker.Hostile ? "a hostile monster, " : string.Empty)}"
                + $"{blocker.Name} (0x{blocker.ObjectId:X8})"
            : "the character stopped making progress";

    /// <summary>
    /// How near the goal a route must end for the character to stop within
    /// <paramref name="arrivalMeters"/> of it, since the driver counts a leg's end
    /// as reached a little before the character stands on it.
    /// </summary>
    internal static float PlanningRadius(float arrivalMeters) =>
        MathF.Max(arrivalMeters - RuntimeRouteDriver.ArrivalRadius, NavGrid.DefaultCellSize);

    /// <summary>Where a request's goal stands now, or where it stood when planned once the client has lost it.</summary>
    private Vector3 Locate(Request request)
    {
        bool located = request.Place is { } place
            ? _goals.TryLocatePlace(place.CellId, place.Local, out Vector3 position)
            : _goals.TryLocate(request.ObjectId, out position);
        return located ? position : request.Goal;
    }

    /// <summary>
    /// How a request's goal is named in what a walk says: its object's id, a place with a cell
    /// by its cell and point, and a place with no cell by its map coordinates.
    /// </summary>
    private static string Label(Request request)
    {
        if (request.Place is not { } place)
            return $"0x{request.ObjectId:X8}";
        if (place.CellId != 0u)
            return $"0x{place.CellId:X8} [{place.Local.X:0.0} {place.Local.Y:0.0} {place.Local.Z:0.0}]";
        float northSouth = (place.Local.Y - (127f * NavGeometry.LandblockSize) - 84f) / 240f;
        float eastWest = (place.Local.X - (127f * NavGeometry.LandblockSize) - 84f) / 240f;
        return $"{MathF.Abs(northSouth):0.000}{(northSouth < 0f ? 'S' : 'N')}, {MathF.Abs(eastWest):0.000}{(eastWest < 0f ? 'W' : 'E')}";
    }

    /// <summary>The route left to walk, and for one stage of a longer walk the straight line on from its end to the goal.</summary>
    private static float Remaining(Request request, RuntimeRouteDriver driver, Vector3 position)
    {
        float remaining = 0f;
        var at = new Vector2(position.X, position.Y);
        for (int index = driver.LegIndex; index < driver.Legs.Count; index++)
        {
            var end = new Vector2(driver.Legs[index].X, driver.Legs[index].Y);
            remaining += Vector2.Distance(at, end);
            at = end;
        }
        return request.Staged
            ? remaining + Vector2.Distance(at, new Vector2(request.Goal.X, request.Goal.Y))
            : remaining;
    }

    private static Vector2 Flat(Vector3 point) => new(point.X, point.Y);

    /// <summary>How many of the objects the server placed a route's legs pass through.</summary>
    private static int PassedObstacles(NavRoute route, NavAvoidance[] obstacles)
    {
        int passed = 0;
        foreach (NavAvoidance obstacle in obstacles)
        {
            var centre = Flat(obstacle.Centre);
            for (int index = 1; index < route.Legs.Count; index++)
            {
                Vector2 start = Flat(route.Legs[index - 1]);
                Vector2 along = Flat(route.Legs[index]) - start;
                float lengthSquared = along.LengthSquared();
                float t = lengthSquared > 1e-6f ? Math.Clamp(Vector2.Dot(centre - start, along) / lengthSquared, 0f, 1f) : 0f;
                if (Vector2.Distance(centre, start + (along * t)) < obstacle.Radius
                    && MathF.Abs(route.Legs[index].Z - obstacle.Centre.Z) <= NavigationObstacleHeight)
                {
                    passed++;
                    break;
                }
            }
        }
        return passed;
    }

    private static RuntimeRouteLeap[] LeapsOf(NavRoute route) =>
        [.. route.Leaps.Select(leap => new RuntimeRouteLeap(leap.LegIndex, leap.Power, leap.Run))];

    private static RuntimeRouteDriver Drive(Request request, RuntimeRouteDriver driver)
    {
        request.Drives.Add(driver);
        return driver;
    }

    /// <summary>What a drive cuts corners along: arcs the grid lets a body brush along, or none before there is a grid.</summary>
    private Func<IReadOnlyList<Vector3>, bool>? CornerCuts() => _grid is { } grid ? grid.CanBrushAlong : null;

    private static float HorizontalDistance(Vector3 from, Vector3 to) =>
        Vector2.Distance(new Vector2(from.X, from.Y), new Vector2(to.X, to.Y));

    private static float Snap(float coordinate) =>
        MathF.Floor(coordinate / NavGrid.DefaultCellSize) * NavGrid.DefaultCellSize;

    /// <summary>A door a walk waits on: to appraise it, or to use it and see it open.</summary>
    private readonly record struct DoorWait(
        NavigationDoor Door,
        long UseAtTick,
        bool Used,
        double DeadlineSeconds,
        bool Appraising = false);

    private sealed class Request
    {
        public Request(long sequence, uint objectId, float arrivalMeters, bool walk, (uint CellId, Vector3 Local)? place = null)
        {
            Sequence = sequence;
            ObjectId = objectId;
            ArrivalMeters = arrivalMeters;
            Walk = walk;
            Place = place;
        }

        /// <summary>The place walked to, as a cell and a landblock-local point, for a walk to a place rather than an object.</summary>
        public (uint CellId, Vector3 Local)? Place { get; }

        public long Sequence { get; }

        public uint ObjectId { get; }

        public float ArrivalMeters { get; }

        /// <summary>False when only a route was asked for.</summary>
        public bool Walk { get; }

        public Vector3 Goal { get; set; }

        public bool GoalKnown { get; set; }

        public int Replans { get; set; }

        /// <summary>Grids built for the current plan.</summary>
        public int Builds { get; set; }

        /// <summary>Whether the route planned or walked is one stage of a walk to a goal beyond it.</summary>
        public bool Staged { get; set; }

        /// <summary>The stages of the walk already walked.</summary>
        public int Stages { get; set; }

        /// <summary>The spots where the walk stopped making progress, which later plans keep out of.</summary>
        public List<NavAvoidance> Avoid { get; } = [];

        /// <summary>
        /// The object the walk last stopped making progress beside, whose whole
        /// footprint later plans keep out of, named if no way around it is found.
        /// </summary>
        public NavigationBlocker? AvoidedObject { get; set; }

        /// <summary>The objects the server placed that the latest plan was asked to keep out of.</summary>
        public NavAvoidance[] PlannedObstacles { get; set; } = [];

        /// <summary>What stood beside the spot where the walk last stopped making progress.</summary>
        public NavigationBlocker? BlockedBy { get; set; }

        /// <summary>Why the route ends farther from the goal than the arrival radius, when it does.</summary>
        public string? ArrivalReason { get; set; }

        /// <summary>The door the walk is waiting on to open, if any.</summary>
        public DoorWait? WaitingOn { get; set; }

        /// <summary>How often the walk has used each door.</summary>
        public Dictionary<uint, int> DoorUses { get; } = [];

        /// <summary>The doors the walk asked the server to appraise.</summary>
        public HashSet<uint> AppraisedDoors { get; } = [];

        /// <summary>The doors, locked or that would not open, that later plans keep out of.</summary>
        public HashSet<uint> AvoidedDoors { get; } = [];

        /// <summary>The door the walk last planned a way around, and why, named if no way around it is found.</summary>
        public (NavigationDoor Door, string Why)? AvoidedDoor { get; set; }

        /// <summary>The creatures and players the latest plan passed, where they stood, each widened by the body's radius.</summary>
        public NavAvoidance[] PlannedCrowd { get; set; } = [];

        /// <summary>Whether the route being searched for is a way around creatures, searched while the walk goes on.</summary>
        public bool Detouring { get; set; }

        /// <summary>When the walk, waiting to plan, first found the character in the air, while it still is.</summary>
        public double? AirborneSince { get; set; }

        /// <summary>When the walk last planned a way around creatures, in the controller's seconds.</summary>
        public double DetouredAt { get; set; } = double.NegativeInfinity;

        /// <summary>The drives the walk has made, which count the corners they took.</summary>
        public List<RuntimeRouteDriver> Drives { get; } = [];

        /// <summary>How often the walk has waited for a creature or player to move aside since it last walked on.</summary>
        public int Shuffles { get; set; }

        /// <summary>Where the walk last waited for a creature or player to move aside, and until when.</summary>
        public Vector3 ShuffledAt { get; set; }

        public double ShuffleUntil { get; set; }

        /// <summary>Spots the next plan alone keeps out of, such as where a creature that would not move aside stood.</summary>
        public List<NavAvoidance> PassingAvoid { get; } = [];

        /// <summary>What the walk waits on while something else needs the character.</summary>
        public string? PausedFor { get; set; }

        /// <summary>Since when nothing has needed the character while the walk waits, in the controller's seconds.</summary>
        public double? FreeSince { get; set; }
    }
}
