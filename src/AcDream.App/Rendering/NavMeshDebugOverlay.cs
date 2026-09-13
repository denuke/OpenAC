using System.Numerics;
using AcDream.App.Navigation;
using AcDream.Core.Navigation;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Rendering;

/// <summary>
/// A development view of navigation: the grid around the player, and the route
/// and goal of the most recent walk or route request, drawn as debug lines. The
/// leg the character is walking is drawn in its own colour.
/// </summary>
internal sealed class NavMeshDebugOverlay
{
    internal const float DrawRadius = 20f;
    internal const int DrawStride = 2;

    private const float MarkerHalfSize = 0.08f;

    private static readonly Vector3 NodeLift = new(0f, 0f, 0.04f);
    private static readonly Vector3 PathLift = new(0f, 0f, 0.15f);
    private static readonly Vector3 LegLift = new(0f, 0f, 0.3f);
    private static readonly Vector3 LegPost = new(0f, 0f, 1f);
    private static readonly Vector3 ClearColour = new(0.2f, 0.95f, 0.35f);
    private static readonly Vector3 TightColour = new(0.9f, 0.25f, 0.15f);
    private static readonly Vector3 PathColour = new(0.85f, 0.85f, 0.85f);
    private static readonly Vector3 LegColour = new(1f, 0.85f, 0.1f);
    private static readonly Vector3 WalkingLegColour = new(0.1f, 0.9f, 1f);
    private static readonly Vector3 GoalColour = new(1f, 0.2f, 0.9f);

    private readonly NavigationWalkController _walk;

    public NavMeshDebugOverlay(NavigationWalkController walk)
    {
        _walk = walk ?? throw new ArgumentNullException(nameof(walk));
    }

    public void Draw(DebugLineRenderer lines, PlayerMovementController? player)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (player is not null && _walk.Grid is { } grid)
            DrawGrid(lines, grid, player.Position);
        DrawRoute(lines);
    }

    private static void DrawGrid(DebugLineRenderer lines, NavGrid grid, Vector3 centre)
    {
        int reach = (int)(DrawRadius / grid.CellSize);
        int centreX = (int)MathF.Floor((centre.X - grid.OriginX) / grid.CellSize);
        int centreY = (int)MathF.Floor((centre.Y - grid.OriginY) / grid.CellSize);
        int startX = Math.Max(0, centreX - reach);
        int startY = Math.Max(0, centreY - reach);
        startX -= startX % DrawStride;
        startY -= startY % DrawStride;
        int endX = Math.Min(grid.Side - 1, centreX + reach);
        int endY = Math.Min(grid.Side - 1, centreY + reach);
        var alongX = new Vector3(MarkerHalfSize, 0f, 0f);
        var alongY = new Vector3(0f, MarkerHalfSize, 0f);
        for (int y = startY; y <= endY; y += DrawStride)
        {
            for (int x = startX; x <= endX; x += DrawStride)
            {
                (int first, int count) = grid.NodesInColumn(x, y);
                for (int node = first; node < first + count; node++)
                {
                    Vector3 at = grid.Position(node) + NodeLift;
                    float dx = at.X - centre.X;
                    float dy = at.Y - centre.Y;
                    if ((dx * dx) + (dy * dy) > DrawRadius * DrawRadius)
                        continue;
                    Vector3 colour = grid.IsClear(node) ? ClearColour : TightColour;
                    lines.AddLine(at - alongX, at + alongX, colour);
                    lines.AddLine(at - alongY, at + alongY, colour);
                }
            }
        }
    }

    private void DrawRoute(DebugLineRenderer lines)
    {
        if (_walk.Goal is { } goal)
            lines.AddCylinder(goal.Position, goal.ArrivalMeters, 0.1f, GoalColour);
        if (_walk.Route is not { Outcome: NavRouteOutcome.Routed } route)
            return;

        for (int index = 1; index < route.Path.Count; index++)
            lines.AddLine(route.Path[index - 1] + PathLift, route.Path[index] + PathLift, PathColour);
        int? walking = _walk.LegIndex;
        for (int index = 0; index < route.Legs.Count; index++)
        {
            lines.AddLine(route.Legs[index], route.Legs[index] + LegPost, LegColour);
            if (index > 0)
            {
                lines.AddLine(
                    route.Legs[index - 1] + LegLift,
                    route.Legs[index] + LegLift,
                    index == walking ? WalkingLegColour : LegColour);
            }
        }
    }
}
