using System.Diagnostics;
using System.Numerics;

namespace AcDream.Core.Navigation;

public enum NavRouteOutcome
{
    Routed,

    /// <summary>The start is not on a clear node.</summary>
    NoStart,

    /// <summary>No clear node is within reach of the goal.</summary>
    NoGoal,

    /// <summary>No clear path joins the start to the goal.</summary>
    NoPath,
}

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

/// <summary>Finds routes over a <see cref="NavGrid"/> with A*.</summary>
public static class NavRouter
{
    public const float StartRadius = 1.5f;
    public const float StartHeightTolerance = 1f;
    public const float GoalHeightTolerance = 3f;
    public const int MaximumExpansions = 4_000_000;

    /// <summary>Nodes nearer a wall or a ledge than this cost more, so routes keep to the middle of doorways.</summary>
    private const float ComfortableClearance = 1.25f;

    private const float DiagonalStep = 1.41421356f;

    public static NavRoute Find(NavGrid grid, Vector3 from, Vector3 to, float arrivalRadius)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var clock = Stopwatch.StartNew();
        float reach = MathF.Max(arrivalRadius, grid.CellSize);
        int start = grid.FindNode(from, StartRadius, StartHeightTolerance);
        if (start < 0)
            return Failed(NavRouteOutcome.NoStart, "the start is not on a clear node", 0, clock);
        if (grid.FindNode(to, reach, GoalHeightTolerance) < 0)
            return Failed(NavRouteOutcome.NoGoal, "no clear node is within reach of the goal", 0, clock);

        var cost = new float[grid.NodeCount];
        Array.Fill(cost, float.PositiveInfinity);
        var parent = new int[grid.NodeCount];
        var closed = new bool[grid.NodeCount];
        var open = new PriorityQueue<int, float>();
        cost[start] = 0f;
        parent[start] = start;
        open.Enqueue(start, Remaining(grid.Position(start), to, reach));
        int expansions = 0;
        while (open.TryDequeue(out int node, out _))
        {
            if (closed[node])
                continue;
            closed[node] = true;
            expansions++;
            Vector3 here = grid.Position(node);
            if (Arrived(here, to, reach))
                return Routed(grid, start, node, parent, expansions, clock);
            if (expansions >= MaximumExpansions)
            {
                return Failed(
                    NavRouteOutcome.NoPath,
                    $"the search gave up after {expansions} expansions",
                    expansions,
                    clock);
            }

            for (int direction = 0; direction < NavGrid.DirectionCount; direction++)
            {
                int next = grid.Link(node, direction);
                if (next < 0 || closed[next] || !grid.IsClear(next))
                    continue;
                Vector3 there = grid.Position(next);
                float step = (grid.CellSize * (direction < 4 ? 1f : DiagonalStep))
                    + MathF.Abs(there.Z - here.Z)
                    + WallCost(grid, next);
                float total = cost[node] + step;
                if (total >= cost[next])
                    continue;
                cost[next] = total;
                parent[next] = node;
                open.Enqueue(next, total + Remaining(there, to, reach));
            }
        }
        return Failed(NavRouteOutcome.NoPath, "no clear path joins the start to the goal", expansions, clock);
    }

    private static NavRoute Routed(
        NavGrid grid,
        int start,
        int end,
        int[] parent,
        int expansions,
        Stopwatch clock)
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
            while (furthest + 1 < nodes.Count && grid.CanWalkStraight(nodes[anchor], nodes[furthest + 1]))
                furthest++;
            legs.Add(path[furthest]);
            anchor = furthest;
        }

        float length = 0f;
        for (int index = 1; index < legs.Count; index++)
            length += Vector3.Distance(legs[index - 1], legs[index]);
        return new NavRoute(
            NavRouteOutcome.Routed,
            "routed",
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

    private static bool Arrived(Vector3 position, Vector3 goal, float reach)
    {
        float dx = position.X - goal.X;
        float dy = position.Y - goal.Y;
        return (dx * dx) + (dy * dy) <= reach * reach
            && MathF.Abs(position.Z - goal.Z) <= GoalHeightTolerance;
    }

    private static float Remaining(Vector3 position, Vector3 goal, float reach)
    {
        float dx = position.X - goal.X;
        float dy = position.Y - goal.Y;
        return MathF.Max(0f, MathF.Sqrt((dx * dx) + (dy * dy)) - reach);
    }

    private static NavRoute Failed(NavRouteOutcome outcome, string reason, int expansions, Stopwatch clock) =>
        new(outcome, reason, [], [], 0f, expansions, clock.Elapsed.TotalMilliseconds);
}
