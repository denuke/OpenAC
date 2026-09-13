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
/// </summary>
internal readonly record struct NavigationWalkReport(
    long Sequence,
    NavigationWalkState State,
    uint ObjectId,
    float RemainingMeters,
    int Replans,
    string Reason);

/// <summary>The local player's body on one frame, as a walk sees it.</summary>
internal readonly record struct NavigationWalkBodySample(
    Vector3 Position,
    float HeadingDegrees,
    NavBody Body,
    RuntimeScriptedMoveSnapshot Moves,
    bool InPortalSpace);

/// <summary>The local player's body, as a walk samples and moves it.</summary>
internal interface INavigationWalkBody
{
    /// <summary>False while there is no local player in the world.</summary>
    bool TrySample(out NavigationWalkBodySample sample);

    bool BeginMove(in RuntimeMoveRequest request);

    bool StopMove(RuntimeMoveChannel channel);
}

/// <summary>Where objects stand in the physics world.</summary>
internal interface INavigationGoalSource
{
    bool TryLocate(uint objectId, out Vector3 position);
}

/// <summary>
/// Plans routes to objects over navigation grids built from the physics world,
/// and walks the local player along them with scripted moves. A grid covers a
/// square region around the character and its goal and is built off the update
/// thread. A walk that stops making progress plans again from where the
/// character stands, a few times, before it gives up; a walk that arrives turns
/// the character to face its goal.
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

    /// <summary>A grid is used for a route only while the character and the goal are this far inside it.</summary>
    private const float CoverMargin = 8f;

    private const int MaximumBuildsPerPlan = 2;
    private const int ViewRetryTicks = 120;
    private const float FaceToleranceDegrees = 10f;

    private readonly PhysicsEngine _physics;
    private readonly INavigationWalkBody _body;
    private readonly INavigationGoalSource _goals;
    private readonly Action<string>? _say;
    private readonly object _gate = new();

    private Request? _incoming;
    private bool _stopIncoming;
    private long _sequence;
    private NavigationWalkReport _report;

    private NavGrid? _grid;
    private Task<NavGrid>? _building;
    private long _tick;
    private long _viewRetryTick;

    private Request? _active;
    private Task<NavRoute>? _routing;
    private Request? _routingFor;
    private RuntimeRouteDriver? _driver;

    public NavigationWalkController(
        PhysicsEngine physics,
        INavigationWalkBody body,
        INavigationGoalSource goals,
        Action<string>? say = null)
    {
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
        _body = body ?? throw new ArgumentNullException(nameof(body));
        _goals = goals ?? throw new ArgumentNullException(nameof(goals));
        _say = say;
    }

    /// <summary>Whether to keep a grid built around the character while nothing is planned.</summary>
    public bool ShowGrid { get; set; }

    /// <summary>The grid most recently built.</summary>
    public NavGrid? Grid => _grid;

    /// <summary>The route most recently searched for.</summary>
    public NavRoute? Route { get; private set; }

    /// <summary>The goal of the most recent request that found its object, and how near counts as arriving.</summary>
    public (Vector3 Position, float ArrivalMeters)? Goal { get; private set; }

    /// <summary>The index in the route's legs of the leg end the character is walking toward, while it walks.</summary>
    public int? LegIndex => _driver?.LegIndex;

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

    /// <summary>Ends the request under way, stopping the moves its walk began.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _incoming = null;
            _stopIncoming = true;
        }
    }

    /// <summary>Advances planning and walking by one update frame. Call it on the thread that owns the physics world.</summary>
    public void Tick()
    {
        _tick++;
        CollectBuild();
        CollectRoute();

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

        if (_active is { } active)
        {
            if (inWorld)
                Advance(active, sample);
            else
                End(active, NavigationWalkState.Lost, "the character left the world");
            return;
        }
        if (inWorld && ShowGrid)
            KeepViewGrid(sample);
    }

    /// <summary>A square region holding both points with room around them, or false when they are too far apart.</summary>
    internal static bool TryChooseRegion(
        Vector3 from,
        Vector3 to,
        out float originX,
        out float originY,
        out float size)
    {
        float span = MathF.Max(MathF.Abs(to.X - from.X), MathF.Abs(to.Y - from.Y)) + (2f * RegionMargin);
        size = MathF.Ceiling(MathF.Max(MinimumRegion, span) / 16f) * 16f;
        if (size > MaximumRegion)
        {
            originX = 0f;
            originY = 0f;
            return false;
        }
        originX = Snap(((from.X + to.X) * 0.5f) - (size * 0.5f));
        originY = Snap(((from.Y + to.Y) * 0.5f) - (size * 0.5f));
        return true;
    }

    private long Enqueue(uint objectId, float arrivalMeters, bool walk)
    {
        if (!(arrivalMeters > 0f) || !float.IsFinite(arrivalMeters))
            arrivalMeters = DefaultArrivalMeters;
        lock (_gate)
        {
            long sequence = ++_sequence;
            _incoming = new Request(sequence, objectId, arrivalMeters, walk);
            _stopIncoming = false;
            _report = new NavigationWalkReport(
                sequence,
                NavigationWalkState.Planning,
                objectId,
                0f,
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
        if (!_goals.TryLocate(request.ObjectId, out Vector3 goal))
        {
            End(request, NavigationWalkState.NoRoute, $"the client has no position for 0x{request.ObjectId:X8}");
            return;
        }

        request.Goal = goal;
        _active = request;
        Route = null;
        Goal = (goal, request.ArrivalMeters);
        Publish(request, NavigationWalkState.Planning, "planning", 0f);
    }

    private void Advance(Request active, in NavigationWalkBodySample sample)
    {
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

        if (_grid is not { } grid
            || grid.Body != sample.Body
            || !grid.Contains(sample.Position, CoverMargin)
            || !grid.Contains(active.Goal, CoverMargin)
            || IsStale(grid))
        {
            if (!TryChooseRegion(sample.Position, active.Goal, out float originX, out float originY, out float size))
            {
                End(active, NavigationWalkState.NoRoute, "the goal is too far away to plan a route to");
                return;
            }
            if (active.Builds >= MaximumBuildsPerPlan)
            {
                End(active, NavigationWalkState.NoRoute, "no grid covers both the character and the goal");
                return;
            }
            active.Builds++;
            if (!StartBuild(originX, originY, size, sample.Body))
                End(active, NavigationWalkState.NoRoute, "no collision is loaded around the character");
            return;
        }

        Vector3 from = sample.Position;
        Vector3 to = active.Goal;
        float arrival = active.ArrivalMeters;
        _routingFor = active;
        _routing = Task.Run(() => NavRouter.Find(grid, from, to, arrival));
    }

    private void Drive(Request active, RuntimeRouteDriver driver, in NavigationWalkBodySample sample)
    {
        RuntimeRouteDriveStep step = driver.Advance(new RuntimeRouteDriveSample(
            sample.Position,
            sample.HeadingDegrees,
            sample.Moves,
            sample.InPortalSpace));
        if (!Apply(step))
        {
            Apply(driver.Cancel());
            End(active, NavigationWalkState.Stopped, "the client refused a move");
            return;
        }

        switch (driver.State)
        {
            case RuntimeRouteDriveState.Driving:
                Publish(active, NavigationWalkState.Walking, "walking", Remaining(driver, sample.Position));
                break;
            case RuntimeRouteDriveState.Arrived:
                Face(active.Goal, sample);
                End(active, NavigationWalkState.Arrived, "arrived");
                break;
            case RuntimeRouteDriveState.Blocked when active.Replans < MaximumReplans:
                active.Replans++;
                active.Builds = 0;
                _driver = null;
                string again = $"blocked, planning again ({active.Replans} of {MaximumReplans})";
                Publish(active, NavigationWalkState.Planning, again, 0f);
                _say?.Invoke($"Walk to 0x{active.ObjectId:X8}: {again}");
                break;
            case RuntimeRouteDriveState.Blocked:
                End(active, NavigationWalkState.Blocked, "the character stopped making progress");
                break;
            case RuntimeRouteDriveState.Interrupted:
                End(active, NavigationWalkState.Interrupted, "the player moved the character");
                break;
            default:
                End(active, NavigationWalkState.Lost, "the character entered portal space");
                break;
        }
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
        Publish(request, state, reason, 0f);
        _say?.Invoke($"{(request.Walk ? "Walk" : "Route")} to 0x{request.ObjectId:X8}: {reason}");
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
                reason);
        }
    }

    private bool IsStale(NavGrid grid)
    {
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

    private bool StartBuild(float originX, float originY, float size, NavBody body)
    {
        NavGeometry? geometry = NavGeometry.Capture(_physics, originX, originY, size);
        if (geometry is null)
            return false;
        _building = Task.Run(() => NavGrid.Build(geometry, body));
        return true;
    }

    private void KeepViewGrid(in NavigationWalkBodySample sample)
    {
        if (_building is not null || _tick < _viewRetryTick)
            return;
        if (_grid is { } grid && grid.Body == sample.Body && grid.Contains(sample.Position, ViewRegion / 8f))
            return;
        float half = ViewRegion * 0.5f;
        if (!StartBuild(Snap(sample.Position.X - half), Snap(sample.Position.Y - half), ViewRegion, sample.Body))
            _viewRetryTick = _tick + ViewRetryTicks;
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
        if (route.Outcome != NavRouteOutcome.Routed)
        {
            End(requester, NavigationWalkState.NoRoute, route.Reason);
            return;
        }
        _say?.Invoke(
            $"Route: {route.Legs.Count - 1} legs, {route.Length:0.0} m, "
            + $"{route.Expansions} expansions in {route.Milliseconds:0} ms");
        if (!requester.Walk)
        {
            End(requester, NavigationWalkState.Planned, "a route was found");
            return;
        }
        if (route.Legs.Count < 2)
        {
            if (_body.TrySample(out NavigationWalkBodySample sample))
                Face(requester.Goal, sample);
            End(requester, NavigationWalkState.Arrived, "already there");
            return;
        }
        _driver = new RuntimeRouteDriver(route.Legs);
        Publish(requester, NavigationWalkState.Walking, "walking", route.Length);
    }

    private static float Remaining(RuntimeRouteDriver driver, Vector3 position)
    {
        float remaining = 0f;
        var at = new Vector2(position.X, position.Y);
        for (int index = driver.LegIndex; index < driver.Legs.Count; index++)
        {
            var end = new Vector2(driver.Legs[index].X, driver.Legs[index].Y);
            remaining += Vector2.Distance(at, end);
            at = end;
        }
        return remaining;
    }

    private static float Snap(float coordinate) =>
        MathF.Floor(coordinate / NavGrid.DefaultCellSize) * NavGrid.DefaultCellSize;

    private sealed class Request
    {
        public Request(long sequence, uint objectId, float arrivalMeters, bool walk)
        {
            Sequence = sequence;
            ObjectId = objectId;
            ArrivalMeters = arrivalMeters;
            Walk = walk;
        }

        public long Sequence { get; }

        public uint ObjectId { get; }

        public float ArrivalMeters { get; }

        /// <summary>False when only a route was asked for.</summary>
        public bool Walk { get; }

        public Vector3 Goal { get; set; }

        public int Replans { get; set; }

        /// <summary>Grids built for the current plan.</summary>
        public int Builds { get; set; }
    }
}
