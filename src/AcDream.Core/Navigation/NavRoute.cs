using System.Diagnostics;
using System.Numerics;

namespace AcDream.Core.Navigation;

public enum NavRouteOutcome
{
    Routed,

    /// <summary>No clear node near the start can be walked to without passing a wall.</summary>
    NoStart,

    /// <summary>No clear node near enough to the goal can see it.</summary>
    NoGoal,

    /// <summary>No clear path joins the start to a node that can see the goal.</summary>
    NoPath,
}

/// <summary>A spot a route keeps out of, such as where a walk last stopped making progress.</summary>
public readonly record struct NavAvoidance(Vector3 Centre, float Radius);

/// <summary>
/// A route over a <see cref="NavGrid"/>: the nodes it steps through, and the
/// straight legs between them that a body can walk.
/// </summary>
public sealed record NavRoute(
    NavRouteOutcome Outcome,
    string Reason,
    IReadOnlyList<Vector3> Path,
    IReadOnlyList<Vector3> Legs,
    float Length,
    int Expansions,
    double Milliseconds);

/// <summary>
/// Finds routes over a <see cref="NavGrid"/> with A*. A route starts from a
/// clear node the start can walk to without passing through a wall, and ends at
/// a clear node near the goal from which a body can see the goal, so a wall
/// between the two never counts as arriving.
/// </summary>
public static class NavRouter
{
    public const float StartRadius = 1.5f;
    public const float StartHeightTolerance = 1f;

    /// <summary>How far above or below the goal an arrival node may stand.</summary>
    public const float GoalHeightTolerance = 1.2f;

    /// <summary>
    /// When no node within the arrival radius can be both reached and see the
    /// goal, as for a vendor behind a counter, the route ends at the nearest
    /// reachable node within this far that can see it. When none can see it, as
    /// through a window whose collision fills the opening, and the route is not
    /// keeping out of a spot where a walk was blocked, it ends at the nearest
    /// reachable node within this far, without a line of sight.
    /// </summary>
    public const float FallbackReach = 10f;

    public const int MaximumExpansions = 4_000_000;

    /// <summary>Nodes nearer a wall or a ledge than this cost more, so routes keep to the middle of doorways.</summary>
    private const float ComfortableClearance = 1.25f;

    /// <summary>How far above or below an avoided spot a node is still kept out of it.</summary>
    private const float AvoidanceHeight = 2f;

    /// <summary>A start this far from its node walks to the node first when it cannot walk the first leg straight.</summary>
    private const float StartOffNode = 0.3f;

    private const float DiagonalStep = 1.41421356f;

    public static NavRoute Find(
        NavGrid grid,
        Vector3 from,
        Vector3 to,
        float arrivalRadius,
        IReadOnlyList<NavAvoidance>? avoid = null)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var clock = Stopwatch.StartNew();
        IReadOnlyList<NavAvoidance> avoided = avoid ?? [];
        float reach = MathF.Max(arrivalRadius, grid.CellSize);
        int start = grid.FindWalkableNode(from, StartRadius, StartHeightTolerance);
        if (start < 0)
            return NoStart(clock);
        var goal = new GoalTest(grid, to, reach, avoided);
        bool nearGoalSeen = goal.MaySucceed();

        var search = new Search(grid, start, to, reach, avoided);
        int end = search.Run(node => nearGoalSeen && goal.IsReachedAt(node));
        if (end >= 0)
            return Routed(grid, from, start, end, search.Parent, avoided, search.Expansions, clock, "routed");
        if (end == Search.GaveUp)
            return SearchGaveUp(search, clock);
        int nearest = goal.NearestSeeingAmong(search.Closed);
        if (nearest >= 0)
        {
            Vector3 at = grid.Position(nearest);
            float away = Vector2.Distance(new Vector2(at.X, at.Y), new Vector2(to.X, to.Y));
            return Routed(
                grid,
                from,
                start,
                nearest,
                search.Parent,
                avoided,
                search.Expansions,
                clock,
                $"nothing within {reach:0.#} m of the goal can be reached and see it, so the route ends at "
                + $"the nearest spot that can, {away:0.0} m from it");
        }
        int nearestUnseeing = avoided.Count == 0 ? goal.NearestAmong(search.Closed) : -1;
        if (nearestUnseeing >= 0)
        {
            Vector3 at = grid.Position(nearestUnseeing);
            float away = Vector2.Distance(new Vector2(at.X, at.Y), new Vector2(to.X, to.Y));
            return Routed(
                grid,
                from,
                start,
                nearestUnseeing,
                search.Parent,
                avoided,
                search.Expansions,
                clock,
                $"no reachable spot can see the goal, so the route ends at the nearest spot, {away:0.0} m from it, "
                + "without a line of sight");
        }
        return nearGoalSeen
            ? Failed(
                NavRouteOutcome.NoPath,
                $"no clear path leads from the start to a spot within {FallbackReach:0} m that can see the goal",
                search.Expansions,
                clock)
            : Failed(
                NavRouteOutcome.NoGoal,
                $"no spot within {FallbackReach:0} m of the goal that the start can reach can see it",
                search.Expansions,
                clock);
    }

    /// <summary>
    /// Finds a route toward a goal that lies beyond the grid, for a walk too long
    /// for one grid to hold: to the first reachable node within
    /// <paramref name="band"/> of the nearest the grid comes to the goal, or, when
    /// none is reachable, to the reachable node nearest the goal.
    /// </summary>
    public static NavRoute FindToward(
        NavGrid grid,
        Vector3 from,
        Vector3 goal,
        float band,
        IReadOnlyList<NavAvoidance>? avoid = null)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var clock = Stopwatch.StartNew();
        IReadOnlyList<NavAvoidance> avoided = avoid ?? [];
        int start = grid.FindWalkableNode(from, StartRadius, StartHeightTolerance);
        if (start < 0)
            return NoStart(clock);

        float enough = DistanceBeyond(grid, goal) + MathF.Max(band, grid.CellSize);
        int nearest = start;
        float nearestAway = Away(grid.Position(start), goal);
        var search = new Search(grid, start, goal, 0f, avoided);
        int end = search.Run(node =>
        {
            float away = Away(grid.Position(node), goal);
            if (away < nearestAway)
            {
                nearest = node;
                nearestAway = away;
            }
            return away <= enough;
        });
        if (end >= 0)
        {
            return Routed(
                grid,
                from,
                start,
                end,
                search.Parent,
                avoided,
                search.Expansions,
                clock,
                $"the goal lies beyond this grid, so the route ends at its edge toward the goal, {nearestAway:0} m from it");
        }
        if (nearest != start && Away(from, goal) - nearestAway >= grid.CellSize)
        {
            return Routed(
                grid,
                from,
                start,
                nearest,
                search.Parent,
                avoided,
                search.Expansions,
                clock,
                $"the goal lies beyond this grid, and the route ends at the reachable spot nearest it, {nearestAway:0} m from it");
        }
        return end == Search.GaveUp
            ? SearchGaveUp(search, clock)
            : Failed(NavRouteOutcome.NoPath, "no clear path leads any nearer the goal", search.Expansions, clock);
    }

    private static NavRoute Routed(
        NavGrid grid,
        Vector3 from,
        int start,
        int end,
        int[] parent,
        IReadOnlyList<NavAvoidance> avoided,
        int expansions,
        Stopwatch clock,
        string reason)
    {
        var nodes = new List<int>();
        for (int node = end; ; node = parent[node])
        {
            nodes.Add(node);
            if (node == start)
                break;
        }
        nodes.Reverse();

        var path = new List<Vector3>(nodes.Count);
        foreach (int node in nodes)
            path.Add(grid.Position(node));

        var legs = new List<Vector3> { path[0] };
        int anchor = 0;
        while (anchor < nodes.Count - 1)
        {
            int furthest = anchor + 1;
            while (furthest + 1 < nodes.Count
                && grid.CanWalkStraight(nodes[anchor], nodes[furthest + 1])
                && !PassesAvoided(path[anchor], path[furthest + 1], avoided))
            {
                furthest++;
            }
            legs.Add(path[furthest]);
            anchor = furthest;
        }

        float offNode = Vector2.Distance(new Vector2(from.X, from.Y), new Vector2(path[0].X, path[0].Y));
        if (legs.Count > 1 && offNode > StartOffNode && !grid.CanSweep(from, legs[1]))
            legs.Insert(0, from);

        float length = 0f;
        for (int index = 1; index < legs.Count; index++)
            length += Vector3.Distance(legs[index - 1], legs[index]);
        return new NavRoute(
            NavRouteOutcome.Routed,
            reason,
            path,
            legs,
            length,
            expansions,
            clock.Elapsed.TotalMilliseconds);
    }

    private static float WallCost(NavGrid grid, int node)
    {
        float clearance = MathF.Min(grid.WallDistance(node), grid.BorderDistance(node) * grid.CellSize);
        return clearance >= ComfortableClearance ? 0f : (ComfortableClearance - clearance) * 0.5f;
    }

    private static bool IsAvoided(Vector3 point, IReadOnlyList<NavAvoidance> avoided)
    {
        foreach (NavAvoidance avoidance in avoided)
        {
            float dx = point.X - avoidance.Centre.X;
            float dy = point.Y - avoidance.Centre.Y;
            if ((dx * dx) + (dy * dy) <= avoidance.Radius * avoidance.Radius
                && MathF.Abs(point.Z - avoidance.Centre.Z) <= AvoidanceHeight)
            {
                return true;
            }
        }
        return false;
    }

    private static bool PassesAvoided(Vector3 from, Vector3 to, IReadOnlyList<NavAvoidance> avoided)
    {
        foreach (NavAvoidance avoidance in avoided)
        {
            var start = new Vector2(from.X, from.Y);
            Vector2 along = new Vector2(to.X, to.Y) - start;
            var centre = new Vector2(avoidance.Centre.X, avoidance.Centre.Y);
            float lengthSquared = along.LengthSquared();
            float t = lengthSquared > 1e-12f
                ? Math.Clamp(Vector2.Dot(centre - start, along) / lengthSquared, 0f, 1f)
                : 0f;
            float height = from.Z + ((to.Z - from.Z) * t);
            if (Vector2.Distance(centre, start + (along * t)) <= avoidance.Radius
                && MathF.Abs(height - avoidance.Centre.Z) <= AvoidanceHeight)
            {
                return true;
            }
        }
        return false;
    }

    private static float Remaining(Vector3 position, Vector3 goal, float reach)
    {
        float dx = position.X - goal.X;
        float dy = position.Y - goal.Y;
        return MathF.Max(0f, MathF.Sqrt((dx * dx) + (dy * dy)) - reach);
    }

    private static NavRoute Failed(NavRouteOutcome outcome, string reason, int expansions, Stopwatch clock) =>
        new(outcome, reason, [], [], 0f, expansions, clock.Elapsed.TotalMilliseconds);

    private static NavRoute NoStart(Stopwatch clock) =>
        Failed(NavRouteOutcome.NoStart, "no clear spot near the start can be walked to without passing a wall", 0, clock);

    private static NavRoute SearchGaveUp(Search search, Stopwatch clock) =>
        Failed(
            NavRouteOutcome.NoPath,
            $"the search gave up after {search.Expansions} expansions",
            search.Expansions,
            clock);

    private static float Away(Vector3 point, Vector3 goal) =>
        Vector2.Distance(new Vector2(point.X, point.Y), new Vector2(goal.X, goal.Y));

    /// <summary>How far a point lies outside a grid's square, measured flat; zero inside it.</summary>
    private static float DistanceBeyond(NavGrid grid, Vector3 point)
    {
        float dx = MathF.Max(0f, MathF.Max(grid.OriginX - point.X, point.X - (grid.OriginX + grid.Size)));
        float dy = MathF.Max(0f, MathF.Max(grid.OriginY - point.Y, point.Y - (grid.OriginY + grid.Size)));
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>
    /// A best-first expansion of the clear nodes reachable from a start, ordered
    /// by the cost so far plus the distance left to a point.
    /// </summary>
    private sealed class Search
    {
        /// <summary>What <see cref="Run"/> returns once every reachable node has been expanded.</summary>
        public const int Exhausted = -1;

        /// <summary>What <see cref="Run"/> returns when it stops at <see cref="MaximumExpansions"/>.</summary>
        public const int GaveUp = -2;

        private readonly NavGrid _grid;
        private readonly Vector3 _toward;
        private readonly float _reach;
        private readonly IReadOnlyList<NavAvoidance> _avoided;
        private readonly float[] _cost;
        private readonly PriorityQueue<int, float> _open = new();

        public Search(NavGrid grid, int start, Vector3 toward, float reach, IReadOnlyList<NavAvoidance> avoided)
        {
            _grid = grid;
            _toward = toward;
            _reach = reach;
            _avoided = avoided;
            _cost = new float[grid.NodeCount];
            Array.Fill(_cost, float.PositiveInfinity);
            Parent = new int[grid.NodeCount];
            Closed = new bool[grid.NodeCount];
            _cost[start] = 0f;
            Parent[start] = start;
            _open.Enqueue(start, Remaining(grid.Position(start), toward, reach));
        }

        public int[] Parent { get; }

        /// <summary>The nodes expanded so far.</summary>
        public bool[] Closed { get; }

        public int Expansions { get; private set; }

        /// <summary>Expands nodes until one is an end and returns it, or returns <see cref="Exhausted"/> or <see cref="GaveUp"/>.</summary>
        public int Run(Func<int, bool> isEnd)
        {
            while (_open.TryDequeue(out int node, out _))
            {
                if (Closed[node])
                    continue;
                Closed[node] = true;
                Expansions++;
                if (isEnd(node))
                    return node;
                if (Expansions >= MaximumExpansions)
                    return GaveUp;

                Vector3 here = _grid.Position(node);
                for (int direction = 0; direction < NavGrid.DirectionCount; direction++)
                {
                    int next = _grid.Link(node, direction);
                    if (next < 0 || Closed[next] || !_grid.IsClear(next))
                        continue;
                    Vector3 there = _grid.Position(next);
                    if (IsAvoided(there, _avoided))
                        continue;
                    float step = (_grid.CellSize * (direction < 4 ? 1f : DiagonalStep))
                        + MathF.Abs(there.Z - here.Z)
                        + WallCost(_grid, next);
                    float total = _cost[node] + step;
                    if (total >= _cost[next])
                        continue;
                    _cost[next] = total;
                    Parent[next] = node;
                    _open.Enqueue(next, total + Remaining(there, _toward, _reach));
                }
            }
            return Exhausted;
        }
    }

    /// <summary>
    /// Which nodes count as arriving at a goal: near enough to it, and able to
    /// see it along a line that crosses no wall and no avoided spot.
    /// </summary>
    private sealed class GoalTest
    {
        /// <summary>How many nodes near the goal are tried for a line of sight before planning goes ahead regardless.</summary>
        private const int MaximumProbes = 2048;

        private readonly NavGrid _grid;
        private readonly Vector3 _goal;
        private readonly float _reach;
        private readonly IReadOnlyList<NavAvoidance> _avoided;
        private readonly Dictionary<int, bool> _sight = [];

        public GoalTest(NavGrid grid, Vector3 goal, float reach, IReadOnlyList<NavAvoidance> avoided)
        {
            _grid = grid;
            _goal = goal;
            _reach = reach;
            _avoided = avoided;
        }

        public bool IsReachedAt(int node)
        {
            Vector3 at = _grid.Position(node);
            float dx = at.X - _goal.X;
            float dy = at.Y - _goal.Y;
            if ((dx * dx) + (dy * dy) > _reach * _reach || MathF.Abs(at.Z - _goal.Z) > GoalHeightTolerance)
                return false;
            return Sees(node);
        }

        /// <summary>The reached node within <see cref="FallbackReach"/> of the goal nearest to it that can see it, or -1.</summary>
        public int NearestSeeingAmong(bool[] reached)
        {
            foreach ((int node, _) in ReachedNear(reached))
            {
                if (Sees(node))
                    return node;
            }
            return -1;
        }

        /// <summary>The reached node within <see cref="FallbackReach"/> of the goal nearest to it, or -1.</summary>
        public int NearestAmong(bool[] reached)
        {
            List<(int Node, float Distance)> near = ReachedNear(reached);
            return near.Count == 0 ? -1 : near[0].Node;
        }

        private List<(int Node, float Distance)> ReachedNear(bool[] reached)
        {
            int centreX = (int)MathF.Floor((_goal.X - _grid.OriginX) / _grid.CellSize);
            int centreY = (int)MathF.Floor((_goal.Y - _grid.OriginY) / _grid.CellSize);
            int reach = (int)MathF.Ceiling(FallbackReach / _grid.CellSize);
            var candidates = new List<(int Node, float Distance)>();
            for (int y = centreY - reach; y <= centreY + reach; y++)
            {
                for (int x = centreX - reach; x <= centreX + reach; x++)
                {
                    (int first, int count) = _grid.NodesInColumn(x, y);
                    for (int node = first; node < first + count; node++)
                    {
                        if (!reached[node] || !_grid.IsClear(node))
                            continue;
                        Vector3 at = _grid.Position(node);
                        if (MathF.Abs(at.Z - _goal.Z) > GoalHeightTolerance)
                            continue;
                        float distance = Vector2.Distance(new Vector2(at.X, at.Y), new Vector2(_goal.X, _goal.Y));
                        if (distance <= FallbackReach)
                            candidates.Add((node, distance));
                    }
                }
            }
            candidates.Sort(static (left, right) => left.Distance.CompareTo(right.Distance));
            return candidates;
        }

        private bool Sees(int node)
        {
            if (_sight.TryGetValue(node, out bool sees))
                return sees;
            Vector3 at = _grid.Position(node);
            sees = _grid.CanSee(node, _goal) && !PassesAvoided(at, _goal, _avoided);
            _sight[node] = sees;
            return sees;
        }

        /// <summary>False only when every clear node near enough to the goal was tried and none can see it.</summary>
        public bool MaySucceed()
        {
            int centreX = (int)MathF.Floor((_goal.X - _grid.OriginX) / _grid.CellSize);
            int centreY = (int)MathF.Floor((_goal.Y - _grid.OriginY) / _grid.CellSize);
            int reach = (int)MathF.Ceiling(_reach / _grid.CellSize);
            int probes = 0;
            for (int y = centreY - reach; y <= centreY + reach; y++)
            {
                for (int x = centreX - reach; x <= centreX + reach; x++)
                {
                    (int first, int count) = _grid.NodesInColumn(x, y);
                    for (int node = first; node < first + count; node++)
                    {
                        if (!_grid.IsClear(node))
                            continue;
                        if (++probes > MaximumProbes)
                            return true;
                        if (IsReachedAt(node))
                            return true;
                    }
                }
            }
            return false;
        }
    }
}
