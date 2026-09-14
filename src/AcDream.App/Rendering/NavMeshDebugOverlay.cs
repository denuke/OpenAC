using System.Numerics;
using AcDream.App.Navigation;
using AcDream.Core.Navigation;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Rendering;

/// <summary>
/// The navigation view. The route of a walk under way, or of a route asked for
/// alone, is drawn as a magenta line from the character along the legs still to
/// walk, a leap drawn as an arc, with a dimmer line on to a goal beyond a stage.
/// While the grid is shown it adds the grid around the player, the path the
/// route was straightened from, a post at the end of every leg, and an orange
/// cylinder around each object the server placed that routes keep out of.
/// </summary>
internal sealed class NavMeshDebugOverlay
{
    internal const float DrawRadius = 20f;
    internal const int DrawStride = 2;

    private const float MarkerHalfSize = 0.08f;
    private const float ObstacleHeight = 2f;
    private const int LeapArcSegments = 8;
    private const float LeapArcHeight = 1f;

    private static readonly Vector3 NodeLift = new(0f, 0f, 0.04f);
    private static readonly Vector3 PathLift = new(0f, 0f, 0.15f);
    private static readonly Vector3 RouteLift = new(0f, 0f, 0.1f);
    private static readonly Vector3 LegPost = new(0f, 0f, 1f);
    private static readonly Vector3 ClearColour = new(0.2f, 0.95f, 0.35f);
    private static readonly Vector3 TightColour = new(0.9f, 0.25f, 0.15f);
    private static readonly Vector3 PathColour = new(0.85f, 0.85f, 0.85f);
    private static readonly Vector3 RouteColour = new(1f, 0f, 1f);
    private static readonly Vector3 BeyondColour = new(0.5f, 0f, 0.5f);
    private static readonly Vector3 ObstacleColour = new(1f, 0.55f, 0.1f);

    private readonly NavigationWalkController _walk;

    public NavMeshDebugOverlay(NavigationWalkController walk)
    {
        _walk = walk ?? throw new ArgumentNullException(nameof(walk));
    }

    /// <summary>Whether there is a route to draw while the grid is hidden: a request under way, or a route found alone.</summary>
    public bool HasRouteToShow => _walk.IsBusy || _walk.Report.State == NavigationWalkState.Planned;

    public void Draw(DebugLineRenderer lines, PlayerMovementController? player, bool showGrid)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (showGrid && player is not null && _walk.Grid is { } grid)
            DrawGrid(lines, grid, player.Position);
        if (showGrid && player is not null)
        {
            foreach (NavAvoidance obstacle in _walk.ObstaclesNear(player.Position, DrawRadius))
                lines.AddCylinder(obstacle.Centre, obstacle.Radius, ObstacleHeight, ObstacleColour);
        }
        DrawRoute(lines, player?.Position, showGrid);
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

    private void DrawRoute(DebugLineRenderer lines, Vector3? character, bool showGrid)
    {
        if (_walk.Goal is { } goal)
            lines.AddCylinder(goal.Position, goal.ArrivalMeters, 0.1f, RouteColour);
        if (_walk.Route is not { Outcome: NavRouteOutcome.Routed } route || route.Legs.Count == 0)
            return;

        if (showGrid)
        {
            for (int index = 1; index < route.Path.Count; index++)
                lines.AddLine(route.Path[index - 1] + PathLift, route.Path[index] + PathLift, PathColour);
            foreach (Vector3 end in route.Legs)
                lines.AddLine(end, end + LegPost, RouteColour);
        }

        int next = 1;
        if (_walk.LegIndex is { } walking && character is { } at && walking < route.Legs.Count)
        {
            lines.AddLine(at + RouteLift, route.Legs[walking] + RouteLift, RouteColour);
            next = walking + 1;
        }
        for (int index = Math.Max(next, 1); index < route.Legs.Count; index++)
            DrawLeg(lines, route, index);
        if (_walk.IsStaged && _walk.Goal is { } beyond)
            lines.AddLine(route.Legs[^1] + RouteLift, beyond.Position + RouteLift, BeyondColour);
    }

    /// <summary>Draws one leg of a route: a straight line, or an arc for a leap.</summary>
    private static void DrawLeg(DebugLineRenderer lines, NavRoute route, int index)
    {
        Vector3 from = route.Legs[index - 1] + RouteLift;
        Vector3 to = route.Legs[index] + RouteLift;
        if (!route.Leaps.Any(leap => leap.LegIndex == index))
        {
            lines.AddLine(from, to, RouteColour);
            return;
        }
        Vector3 previous = from;
        for (int segment = 1; segment <= LeapArcSegments; segment++)
        {
            float along = segment / (float)LeapArcSegments;
            Vector3 point = Vector3.Lerp(from, to, along) + new Vector3(0f, 0f, LeapArcHeight * 4f * along * (1f - along));
            lines.AddLine(previous, point, RouteColour);
            previous = point;
        }
    }
}
