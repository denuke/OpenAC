using System.Diagnostics;
using System.Numerics;
using AcDream.Core.Physics;

namespace AcDream.Core.Navigation;

/// <summary>What building one grid produced, and what it cost.</summary>
public readonly record struct NavGridBuildReport(
    int Triangles,
    int Cylinders,
    int Spans,
    int WallPieces,
    int Nodes,
    int ClearNodes,
    double Milliseconds);

/// <summary>
/// Where a body can stand in a square region of the world, and where it can
/// step next. The region's collision geometry is rasterized into columns of
/// solid spans. A node is the top of a walkable span with headroom for the
/// whole body, and a link joins nodes in neighbouring columns whose heights
/// differ by no more than the body can step up or down. A node is clear to walk
/// through when no wall between step height and head height comes nearer its
/// centre than <see cref="NearestWall"/>, and when it is not on the very edge of
/// a ledge. The walls are kept, so straight walks and lines of sight can be
/// tested against them.
/// </summary>
public sealed class NavGrid
{
    public const float DefaultCellSize = 0.25f;
    public const int DirectionCount = 8;

    /// <summary>How high a standing body's eyes are above its feet.</summary>
    public const float EyeHeight = 1.6f;

    /// <summary>How high above an object's feet a body looks when it looks at the object.</summary>
    public const float TargetHeight = 1.2f;

    /// <summary>Terrain is a surface with this much solid ground under it.</summary>
    private const float TerrainThickness = 0.5f;

    /// <summary>Overlapping spans whose tops are this close share walkability.</summary>
    private const float FlagMergeHeight = 0.1f;

    /// <summary>A walkable polygon covering less of a column than this only touches its edge.</summary>
    private const float MinimumFloorArea = 1e-5f;

    /// <summary>How much nearer than the body's radius a wall may come, as a share of a column.</summary>
    private const float ClearanceToleranceColumns = 0.25f;

    /// <summary>A node must be at least this many steps from a node beside a ledge or a wall.</summary>
    private const int EdgeMargin = 1;

    /// <summary>How near a straight walk may pass a wall's footprint before it counts as passing through it.</summary>
    private const float WalkMargin = 0.05f;

    /// <summary>How near a line of sight may pass a wall's footprint before the wall hides what is behind it.</summary>
    private const float SightMargin = 0.02f;

    private const byte UnboundedDistance = byte.MaxValue;
    private const int ClipCapacity = 32;

    // East, north, west and south, then the diagonals.
    private static readonly int[] StepX = [1, 0, -1, 0, 1, -1, -1, 1];
    private static readonly int[] StepY = [0, 1, 0, -1, 1, 1, -1, -1];

    private readonly int[] _columnFirstNode;
    private readonly int[] _columnNodeCount;
    private readonly int[] _nodeColumn;
    private readonly float[] _nodeZ;
    private readonly float[] _nodeCeiling;
    private readonly int[] _links;
    private readonly byte[] _borderDistance;
    private readonly float[] _wallDistance;
    private readonly bool[] _clear;
    private readonly WallPieces _walls;

    private NavGrid(
        NavGeometry geometry,
        float cellSize,
        int side,
        NavBody body,
        int[] columnFirstNode,
        int[] columnNodeCount,
        int[] nodeColumn,
        float[] nodeZ,
        float[] nodeCeiling,
        int[] links,
        byte[] borderDistance,
        float[] wallDistance,
        bool[] clear,
        WallPieces walls,
        NavGridBuildReport report)
    {
        OriginX = geometry.OriginX;
        OriginY = geometry.OriginY;
        LandblockIds = geometry.LandblockIds;
        CellSize = cellSize;
        Side = side;
        Body = body;
        NearestWall = NearestWallFor(body, cellSize);
        _columnFirstNode = columnFirstNode;
        _columnNodeCount = columnNodeCount;
        _nodeColumn = nodeColumn;
        _nodeZ = nodeZ;
        _nodeCeiling = nodeCeiling;
        _links = links;
        _borderDistance = borderDistance;
        _wallDistance = wallDistance;
        _clear = clear;
        _walls = walls;
        Report = report;
    }

    /// <summary>The world position of the region's south-west corner.</summary>
    public float OriginX { get; }

    public float OriginY { get; }

    public float CellSize { get; }

    /// <summary>Columns along each edge of the region.</summary>
    public int Side { get; }

    /// <summary>The length of the region's sides in meters.</summary>
    public float Size => Side * CellSize;

    /// <summary>The resident landblocks whose geometry the grid was built from.</summary>
    public IReadOnlyList<uint> LandblockIds { get; }

    public NavBody Body { get; }

    /// <summary>
    /// The nearest a wall may come to a body walking the grid: the body's radius,
    /// less a quarter of a column, because the body can stand anywhere in its
    /// column and slides along walls.
    /// </summary>
    public float NearestWall { get; }

    public NavGridBuildReport Report { get; }

    public int NodeCount => _nodeZ.Length;

    public static int DirectionOf(int stepX, int stepY)
    {
        for (int direction = 0; direction < DirectionCount; direction++)
        {
            if (StepX[direction] == stepX && StepY[direction] == stepY)
                return direction;
        }
        return -1;
    }

    /// <summary>Whether a point lies inside the region, at least <paramref name="margin"/> from its edges.</summary>
    public bool Contains(Vector3 point, float margin = 0f) =>
        point.X >= OriginX + margin
        && point.Y >= OriginY + margin
        && point.X <= OriginX + Size - margin
        && point.Y <= OriginY + Size - margin;

    /// <summary>The node a step from <paramref name="node"/> lands on, or -1.</summary>
    public int Link(int node, int direction) => _links[(node * DirectionCount) + direction];

    public bool IsClear(int node) => _clear[node];

    /// <summary>Steps from <paramref name="node"/> to the nearest node beside a ledge or a wall.</summary>
    public int BorderDistance(int node) => _borderDistance[node];

    /// <summary>Horizontal distance from the node's centre to the nearest wall the body would touch, or infinity.</summary>
    public float WallDistance(int node) => _wallDistance[node];

    public Vector3 Position(int node)
    {
        int column = _nodeColumn[node];
        return new Vector3(
            OriginX + (((column % Side) + 0.5f) * CellSize),
            OriginY + (((column / Side) + 0.5f) * CellSize),
            _nodeZ[node]);
    }

    public (int X, int Y) ColumnOf(int node) =>
        (_nodeColumn[node] % Side, _nodeColumn[node] / Side);

    /// <summary>The nodes standing in one column, lowest first.</summary>
    public (int First, int Count) NodesInColumn(int x, int y)
    {
        if ((uint)x >= (uint)Side || (uint)y >= (uint)Side)
            return (0, 0);
        int column = (y * Side) + x;
        return (_columnFirstNode[column], _columnNodeCount[column]);
    }

    /// <summary>
    /// The clear node nearest <paramref name="position"/> within a horizontal
    /// radius and a height tolerance, or -1 when there is none.
    /// </summary>
    public int FindNode(Vector3 position, float radius, float heightTolerance)
    {
        List<(int Node, float Score)> near = NodesNear(position, radius, heightTolerance);
        return near.Count == 0 ? -1 : near[0].Node;
    }

    /// <summary>
    /// The clear node nearest <paramref name="position"/>, within a horizontal
    /// radius and a height tolerance, that a body standing there could walk to in
    /// a straight line without passing through a wall, or -1 when there is none.
    /// </summary>
    public int FindWalkableNode(Vector3 position, float radius, float heightTolerance)
    {
        foreach ((int node, _) in NodesNear(position, radius, heightTolerance))
        {
            if (IsOpenLine(position, Position(node)))
                return node;
        }
        return -1;
    }

    /// <summary>
    /// Whether a body walking straight between two points, standing at their
    /// heights, keeps at least <see cref="NearestWall"/> from every wall it would
    /// touch.
    /// </summary>
    public bool CanSweep(Vector3 from, Vector3 to) => !WallNear(from, to, NearestWall, sight: false);

    /// <summary>Whether a body walking straight between two points, standing at their heights, passes through no wall.</summary>
    public bool IsOpenLine(Vector3 from, Vector3 to) => !WallNear(from, to, WalkMargin, sight: false);

    /// <summary>
    /// Whether a body standing on <paramref name="node"/> can see an object whose
    /// feet are at <paramref name="target"/>: no wall crosses the line from the
    /// body's eyes to the object, the object is not above the node's ceiling, and
    /// it is not so far below the node that the node's own floor hides it.
    /// </summary>
    public bool CanSee(int node, Vector3 target)
    {
        Vector3 feet = Position(node);
        var eye = new Vector3(feet.X, feet.Y, feet.Z + EyeHeight);
        var seen = new Vector3(target.X, target.Y, target.Z + TargetHeight);
        return seen.Z < _nodeCeiling[node]
            && seen.Z >= feet.Z
            && !WallNear(eye, seen, SightMargin, sight: true);
    }

    /// <summary>
    /// The wall pieces that hide <paramref name="target"/> from a body standing on
    /// <paramref name="node"/>: for each, its column, its height range, and the
    /// sight line's height where they are nearest. For diagnostics.
    /// </summary>
    internal IReadOnlyList<(int X, int Y, float Low, float High, float LineHeight)> SightBlockers(int node, Vector3 target)
    {
        Vector3 feet = Position(node);
        var eye = new Vector3(feet.X, feet.Y, feet.Z + EyeHeight);
        var seen = new Vector3(target.X, target.Y, target.Z + TargetHeight);
        var start = new Vector2(eye.X - OriginX, eye.Y - OriginY);
        var end = new Vector2(seen.X - OriginX, seen.Y - OriginY);
        int x0 = Math.Max(0, (int)MathF.Floor((MathF.Min(start.X, end.X) - SightMargin) / CellSize));
        int x1 = Math.Min(Side - 1, (int)MathF.Floor((MathF.Max(start.X, end.X) + SightMargin) / CellSize));
        int y0 = Math.Max(0, (int)MathF.Floor((MathF.Min(start.Y, end.Y) - SightMargin) / CellSize));
        int y1 = Math.Min(Side - 1, (int)MathF.Floor((MathF.Max(start.Y, end.Y) + SightMargin) / CellSize));
        var blockers = new List<(int X, int Y, float Low, float High, float LineHeight)>();
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                for (int piece = _walls.Head[(y * Side) + x]; piece != -1; piece = _walls.Next[piece])
                {
                    (float distance, float along) = _walls.DistanceToSegment(piece, start, end);
                    if (distance >= SightMargin)
                        continue;
                    float height = eye.Z + ((seen.Z - eye.Z) * along);
                    if (_walls.Low[piece] <= height && _walls.High[piece] >= height)
                        blockers.Add((x, y, _walls.Low[piece], _walls.High[piece], height));
                }
            }
        }
        return blockers;
    }

    /// <summary>
    /// Whether a body can walk straight from one node to another: every column
    /// along the line between them holds a clear node linked to the last, and no
    /// wall comes nearer the line than <see cref="NearestWall"/>.
    /// </summary>
    public bool CanWalkStraight(int from, int to)
    {
        (int x, int y) = ColumnOf(from);
        (int targetX, int targetY) = ColumnOf(to);
        int distanceX = Math.Abs(targetX - x);
        int distanceY = Math.Abs(targetY - y);
        int signX = Math.Sign(targetX - x);
        int signY = Math.Sign(targetY - y);
        int error = distanceX - distanceY;
        int node = from;
        while (x != targetX || y != targetY)
        {
            int moveX = 0;
            int moveY = 0;
            int doubled = error * 2;
            if (doubled > -distanceY)
            {
                error -= distanceY;
                x += signX;
                moveX = signX;
            }
            if (doubled < distanceX)
            {
                error += distanceX;
                y += signY;
                moveY = signY;
            }
            node = Link(node, DirectionOf(moveX, moveY));
            if (node < 0 || !IsClear(node))
                return false;
        }
        return node == to && CanSweep(Position(from), Position(to));
    }

    public static NavGrid Build(NavGeometry geometry, NavBody body, float cellSize = DefaultCellSize)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (!(cellSize > 0f) || cellSize > geometry.Size)
            throw new ArgumentOutOfRangeException(nameof(cellSize));

        var clock = Stopwatch.StartNew();
        int side = (int)MathF.Ceiling((geometry.Size / cellSize) - 0.001f);
        int columns = side * side;
        var spans = new SpanColumns(columns);
        var walls = new WallPieces(columns);
        var coveredByCells = new bool[columns];
        var origin = new Vector3(geometry.OriginX, geometry.OriginY, 0f);

        foreach (NavTriangle triangle in geometry.CellTriangles)
            RasterizeTriangle(spans, walls, triangle, origin, cellSize, side, coveredByCells);
        foreach (NavTriangle triangle in geometry.ObjectTriangles)
            RasterizeTriangle(spans, walls, triangle, origin, cellSize, side, covered: null);
        foreach (NavCylinder cylinder in geometry.Cylinders)
            RasterizeCylinder(spans, walls, cylinder, origin, cellSize, side);
        foreach (NavTerrain terrain in geometry.Terrains)
            RasterizeTerrain(spans, terrain, origin, cellSize, side, coveredByCells);

        var columnFirstNode = new int[columns];
        var columnNodeCount = new int[columns];
        var nodeColumns = new List<int>();
        var nodeHeights = new List<float>();
        var nodeCeilings = new List<float>();
        for (int column = 0; column < columns; column++)
        {
            columnFirstNode[column] = nodeHeights.Count;
            for (int span = spans.Head[column]; span != -1; span = spans.Next[span])
            {
                if (!spans.Walkable[span])
                    continue;
                int above = spans.Next[span];
                float ceiling = above == -1 ? float.PositiveInfinity : spans.Min[above];
                if (ceiling - spans.Max[span] < body.Height)
                    continue;
                nodeColumns.Add(column);
                nodeHeights.Add(spans.Max[span]);
                nodeCeilings.Add(ceiling);
            }
            columnNodeCount[column] = nodeHeights.Count - columnFirstNode[column];
        }

        int[] nodeColumn = nodeColumns.ToArray();
        float[] heights = nodeHeights.ToArray();
        float[] ceilings = nodeCeilings.ToArray();
        int[] links = new int[heights.Length * DirectionCount];
        Array.Fill(links, -1);
        LinkOrthogonalNeighbours(side, body, columnFirstNode, columnNodeCount, nodeColumn, heights, ceilings, links);
        LinkDiagonalNeighbours(heights.Length, links);
        byte[] borderDistance = MeasureBorderDistance(links, heights.Length);
        float[] wallDistance = MeasureWallDistance(side, cellSize, body, nodeColumn, heights, walls);

        float nearestWall = NearestWallFor(body, cellSize);
        var clear = new bool[heights.Length];
        int clearCount = 0;
        for (int node = 0; node < heights.Length; node++)
        {
            clear[node] = borderDistance[node] >= EdgeMargin && wallDistance[node] >= nearestWall;
            if (clear[node])
                clearCount++;
        }

        var report = new NavGridBuildReport(
            geometry.CellTriangles.Count + geometry.ObjectTriangles.Count,
            geometry.Cylinders.Count,
            spans.Live,
            walls.Count,
            heights.Length,
            clearCount,
            clock.Elapsed.TotalMilliseconds);
        return new NavGrid(
            geometry,
            cellSize,
            side,
            body,
            columnFirstNode,
            columnNodeCount,
            nodeColumn,
            heights,
            ceilings,
            links,
            borderDistance,
            wallDistance,
            clear,
            walls,
            report);
    }

    private static float NearestWallFor(NavBody body, float cellSize) =>
        body.Radius - (cellSize * ClearanceToleranceColumns);

    /// <summary>The clear nodes within a horizontal radius and a height tolerance of a point, nearest first.</summary>
    private List<(int Node, float Score)> NodesNear(Vector3 position, float radius, float heightTolerance)
    {
        int centreX = (int)MathF.Floor((position.X - OriginX) / CellSize);
        int centreY = (int)MathF.Floor((position.Y - OriginY) / CellSize);
        int reach = (int)MathF.Ceiling(radius / CellSize);
        var near = new List<(int Node, float Score)>();
        for (int y = Math.Max(0, centreY - reach); y <= Math.Min(Side - 1, centreY + reach); y++)
        {
            for (int x = Math.Max(0, centreX - reach); x <= Math.Min(Side - 1, centreX + reach); x++)
            {
                (int first, int count) = NodesInColumn(x, y);
                for (int node = first; node < first + count; node++)
                {
                    if (!IsClear(node))
                        continue;
                    Vector3 at = Position(node);
                    float rise = MathF.Abs(at.Z - position.Z);
                    if (rise > heightTolerance)
                        continue;
                    float dx = at.X - position.X;
                    float dy = at.Y - position.Y;
                    float horizontal = (dx * dx) + (dy * dy);
                    if (horizontal > radius * radius)
                        continue;
                    near.Add((node, horizontal + (rise * rise)));
                }
            }
        }
        near.Sort(static (left, right) => left.Score.CompareTo(right.Score));
        return near;
    }

    /// <summary>
    /// Whether a wall piece comes within <paramref name="margin"/> of the straight
    /// line between two points. For a body the points are where it stands, and a
    /// piece counts when it rises into the body standing on the line where they
    /// are nearest. For a line of sight a piece counts when it spans the line's
    /// height there.
    /// </summary>
    private bool WallNear(Vector3 from, Vector3 to, float margin, bool sight)
    {
        var start = new Vector2(from.X - OriginX, from.Y - OriginY);
        var end = new Vector2(to.X - OriginX, to.Y - OriginY);
        float reach = margin / CellSize;
        float startX = start.X / CellSize;
        float startY = start.Y / CellSize;
        float endX = end.X / CellSize;
        float endY = end.Y / CellSize;
        int firstRow = Math.Max(0, (int)MathF.Floor(MathF.Min(startY, endY) - reach));
        int lastRow = Math.Min(Side - 1, (int)MathF.Floor(MathF.Max(startY, endY) + reach));
        for (int row = firstRow; row <= lastRow; row++)
        {
            float lowX;
            float highX;
            if (MathF.Abs(endY - startY) < 1e-6f)
            {
                lowX = MathF.Min(startX, endX);
                highX = MathF.Max(startX, endX);
            }
            else
            {
                float enter = Math.Clamp((row - reach - startY) / (endY - startY), 0f, 1f);
                float leave = Math.Clamp((row + 1 + reach - startY) / (endY - startY), 0f, 1f);
                float enterX = startX + ((endX - startX) * enter);
                float leaveX = startX + ((endX - startX) * leave);
                lowX = MathF.Min(enterX, leaveX);
                highX = MathF.Max(enterX, leaveX);
            }
            int firstColumn = Math.Max(0, (int)MathF.Floor(lowX - reach));
            int lastColumn = Math.Min(Side - 1, (int)MathF.Floor(highX + reach));
            for (int column = firstColumn; column <= lastColumn; column++)
            {
                for (int piece = _walls.Head[(row * Side) + column]; piece != -1; piece = _walls.Next[piece])
                {
                    (float distance, float along) = _walls.DistanceToSegment(piece, start, end);
                    if (distance >= margin)
                        continue;
                    float height = from.Z + ((to.Z - from.Z) * along);
                    bool counts = sight
                        ? _walls.Low[piece] <= height && _walls.High[piece] >= height
                        : _walls.High[piece] > height + Body.StepUpHeight && _walls.Low[piece] < height + Body.Height;
                    if (counts)
                        return true;
                }
            }
        }
        return false;
    }

    private static void LinkOrthogonalNeighbours(
        int side,
        NavBody body,
        int[] columnFirstNode,
        int[] columnNodeCount,
        int[] nodeColumn,
        float[] heights,
        float[] ceilings,
        int[] links)
    {
        for (int node = 0; node < heights.Length; node++)
        {
            int x = nodeColumn[node] % side;
            int y = nodeColumn[node] / side;
            for (int direction = 0; direction < 4; direction++)
            {
                int neighbourX = x + StepX[direction];
                int neighbourY = y + StepY[direction];
                if ((uint)neighbourX >= (uint)side || (uint)neighbourY >= (uint)side)
                    continue;
                int neighbourColumn = (neighbourY * side) + neighbourX;
                int best = -1;
                float bestRise = float.PositiveInfinity;
                int end = columnFirstNode[neighbourColumn] + columnNodeCount[neighbourColumn];
                for (int other = columnFirstNode[neighbourColumn]; other < end; other++)
                {
                    float rise = heights[other] - heights[node];
                    if (rise > body.StepUpHeight || rise < -body.StepDownHeight)
                        continue;
                    float headroom = MathF.Min(ceilings[node], ceilings[other])
                        - MathF.Max(heights[node], heights[other]);
                    if (headroom < body.Height)
                        continue;
                    if (MathF.Abs(rise) < bestRise)
                    {
                        bestRise = MathF.Abs(rise);
                        best = other;
                    }
                }
                links[(node * DirectionCount) + direction] = best;
            }
        }
    }

    /// <summary>A diagonal step needs both of the square steps around its corner.</summary>
    private static void LinkDiagonalNeighbours(int nodes, int[] links)
    {
        for (int node = 0; node < nodes; node++)
        {
            for (int direction = 4; direction < DirectionCount; direction++)
            {
                int alongX = StepX[direction] > 0 ? 0 : 2;
                int alongY = StepY[direction] > 0 ? 1 : 3;
                int viaX = links[(node * DirectionCount) + alongX];
                int viaY = links[(node * DirectionCount) + alongY];
                if (viaX == -1 || viaY == -1)
                    continue;
                int corner = links[(viaX * DirectionCount) + alongY];
                if (corner != -1 && corner == links[(viaY * DirectionCount) + alongX])
                    links[(node * DirectionCount) + direction] = corner;
            }
        }
    }

    private static byte[] MeasureBorderDistance(int[] links, int nodes)
    {
        var distance = new byte[nodes];
        Array.Fill(distance, UnboundedDistance);
        var frontier = new Queue<int>();
        for (int node = 0; node < nodes; node++)
        {
            for (int direction = 0; direction < DirectionCount; direction++)
            {
                if (links[(node * DirectionCount) + direction] == -1)
                {
                    distance[node] = 0;
                    frontier.Enqueue(node);
                    break;
                }
            }
        }
        while (frontier.TryDequeue(out int node))
        {
            int next = distance[node] + 1;
            if (next >= UnboundedDistance)
                continue;
            for (int direction = 0; direction < DirectionCount; direction++)
            {
                int other = links[(node * DirectionCount) + direction];
                if (other != -1 && distance[other] > next)
                {
                    distance[other] = (byte)next;
                    frontier.Enqueue(other);
                }
            }
        }
        return distance;
    }

    /// <summary>
    /// The horizontal distance from each node's centre to the nearest wall
    /// piece that rises into the body: above what the body steps over and below
    /// its head.
    /// </summary>
    private static float[] MeasureWallDistance(
        int side,
        float cellSize,
        NavBody body,
        int[] nodeColumn,
        float[] heights,
        WallPieces walls)
    {
        int reach = (int)MathF.Ceiling((body.Radius + cellSize) / cellSize);
        int stride = side + 1;
        var piecesBefore = new int[stride * stride];
        for (int y = 0; y < side; y++)
        {
            int row = 0;
            for (int x = 0; x < side; x++)
            {
                row += walls.ColumnCount[(y * side) + x];
                piecesBefore[((y + 1) * stride) + x + 1] = piecesBefore[(y * stride) + x + 1] + row;
            }
        }

        var distance = new float[heights.Length];
        for (int node = 0; node < heights.Length; node++)
        {
            int x = nodeColumn[node] % side;
            int y = nodeColumn[node] / side;
            int x0 = Math.Max(0, x - reach);
            int x1 = Math.Min(side - 1, x + reach);
            int y0 = Math.Max(0, y - reach);
            int y1 = Math.Min(side - 1, y + reach);
            int piecesNearby = piecesBefore[((y1 + 1) * stride) + x1 + 1]
                - piecesBefore[(y0 * stride) + x1 + 1]
                - piecesBefore[((y1 + 1) * stride) + x0]
                + piecesBefore[(y0 * stride) + x0];
            if (piecesNearby == 0)
            {
                distance[node] = float.PositiveInfinity;
                continue;
            }

            float centreX = (x + 0.5f) * cellSize;
            float centreY = (y + 0.5f) * cellSize;
            float low = heights[node] + body.StepUpHeight;
            float high = heights[node] + body.Height;
            float nearest = float.PositiveInfinity;
            for (int neighbourY = y0; neighbourY <= y1; neighbourY++)
            {
                for (int neighbourX = x0; neighbourX <= x1; neighbourX++)
                {
                    for (int piece = walls.Head[(neighbourY * side) + neighbourX]; piece != -1; piece = walls.Next[piece])
                    {
                        if (walls.High[piece] <= low || walls.Low[piece] >= high)
                            continue;
                        nearest = MathF.Min(nearest, walls.Distance(piece, centreX, centreY));
                    }
                }
            }
            distance[node] = nearest;
        }
        return distance;
    }

    /// <summary>
    /// Adds the part of a triangle inside each column it crosses as a solid
    /// span, and remembers the part of a wall as a wall piece. A column owns
    /// its west and south edges and not its east and north ones, so a wall
    /// lying on a column boundary fills exactly one column.
    /// </summary>
    private static void RasterizeTriangle(
        SpanColumns spans,
        WallPieces walls,
        NavTriangle triangle,
        Vector3 origin,
        float cellSize,
        int side,
        bool[]? covered)
    {
        Vector3 a = triangle.A - origin;
        Vector3 b = triangle.B - origin;
        Vector3 c = triangle.C - origin;
        Vector3 normal = Vector3.Cross(b - a, c - a);
        float length = normal.Length();
        if (!(length > 1e-6f))
            return;
        bool walkable = MathF.Abs(normal.Z) / length >= PhysicsGlobals.FloorZ;

        int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) / cellSize));
        int x1 = Math.Min(side - 1, (int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X)) / cellSize));
        int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) / cellSize));
        int y1 = Math.Min(side - 1, (int)MathF.Floor(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) / cellSize));
        if (x0 > x1 || y0 > y1)
            return;

        Span<Vector3> polygon = stackalloc Vector3[ClipCapacity];
        Span<Vector3> scratch = stackalloc Vector3[ClipCapacity];
        for (int y = y0; y <= y1; y++)
        {
            float bottom = y * cellSize;
            float top = bottom + cellSize;
            for (int x = x0; x <= x1; x++)
            {
                float left = x * cellSize;
                float right = left + cellSize;
                polygon[0] = a;
                polygon[1] = b;
                polygon[2] = c;
                int count = Clip(polygon, 3, scratch, alongX: true, left, keepAbove: true);
                count = Clip(scratch, count, polygon, alongX: true, right, keepAbove: false);
                count = Clip(polygon, count, scratch, alongX: false, bottom, keepAbove: true);
                count = Clip(scratch, count, polygon, alongX: false, top, keepAbove: false);
                if (count == 0)
                    continue;
                if (walkable && AreaXY(polygon, count, left, bottom) < MinimumFloorArea)
                    continue;

                float low = float.PositiveInfinity;
                float high = float.NegativeInfinity;
                for (int index = 0; index < count; index++)
                {
                    low = MathF.Min(low, polygon[index].Z);
                    high = MathF.Max(high, polygon[index].Z);
                }
                int column = (y * side) + x;
                spans.Add(column, low, high, walkable);
                if (!walkable)
                    walls.Add(column, polygon[..count], low, high);
                if (covered is not null)
                    covered[column] = true;
            }
        }
    }

    private static void RasterizeCylinder(
        SpanColumns spans,
        WallPieces walls,
        NavCylinder cylinder,
        Vector3 origin,
        float cellSize,
        int side)
    {
        if (!(cylinder.Radius > 0f) || !(cylinder.Height > 0f))
            return;
        Vector3 centre = cylinder.Base - origin;
        float reach = cylinder.Radius + (cellSize * 0.5f);
        int x0 = Math.Max(0, (int)MathF.Floor((centre.X - reach) / cellSize));
        int x1 = Math.Min(side - 1, (int)MathF.Floor((centre.X + reach) / cellSize));
        int y0 = Math.Max(0, (int)MathF.Floor((centre.Y - reach) / cellSize));
        int y1 = Math.Min(side - 1, (int)MathF.Floor((centre.Y + reach) / cellSize));
        Span<Vector3> square = stackalloc Vector3[4];
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                float dx = ((x + 0.5f) * cellSize) - centre.X;
                float dy = ((y + 0.5f) * cellSize) - centre.Y;
                if ((dx * dx) + (dy * dy) > reach * reach)
                    continue;
                int column = (y * side) + x;
                float top = centre.Z + cylinder.Height;
                spans.Add(column, centre.Z, top, walkable: false);

                float left = MathF.Max(x * cellSize, centre.X - cylinder.Radius);
                float right = MathF.Min((x + 1) * cellSize, centre.X + cylinder.Radius);
                float bottom = MathF.Max(y * cellSize, centre.Y - cylinder.Radius);
                float upper = MathF.Min((y + 1) * cellSize, centre.Y + cylinder.Radius);
                if (left > right || bottom > upper)
                    continue;
                square[0] = new Vector3(left, bottom, centre.Z);
                square[1] = new Vector3(right, bottom, centre.Z);
                square[2] = new Vector3(right, upper, centre.Z);
                square[3] = new Vector3(left, upper, centre.Z);
                walls.Add(column, square, centre.Z, top);
            }
        }
    }

    /// <summary>Fills each column whose centre lies in a landblock with that landblock's terrain, unless a cell covers it.</summary>
    private static void RasterizeTerrain(
        SpanColumns spans,
        NavTerrain terrain,
        Vector3 origin,
        float cellSize,
        int side,
        bool[] coveredByCells)
    {
        float cornerX = terrain.OriginX - origin.X;
        float cornerY = terrain.OriginY - origin.Y;
        int x0 = Math.Max(0, (int)MathF.Ceiling((cornerX / cellSize) - 0.5f));
        int x1 = Math.Min(side, (int)MathF.Ceiling(((cornerX + NavGeometry.LandblockSize) / cellSize) - 0.5f));
        int y0 = Math.Max(0, (int)MathF.Ceiling((cornerY / cellSize) - 0.5f));
        int y1 = Math.Min(side, (int)MathF.Ceiling(((cornerY + NavGeometry.LandblockSize) / cellSize) - 0.5f));
        for (int y = y0; y < y1; y++)
        {
            float localY = ((y + 0.5f) * cellSize) - cornerY;
            for (int x = x0; x < x1; x++)
            {
                int column = (y * side) + x;
                if (coveredByCells[column])
                    continue;
                TerrainSurfacePolygon surface =
                    terrain.Surface.SampleSurfacePolygon(((x + 0.5f) * cellSize) - cornerX, localY);
                spans.Add(
                    column,
                    surface.Z - TerrainThickness,
                    surface.Z,
                    surface.Normal.Z >= PhysicsGlobals.FloorZ);
            }
        }
    }

    /// <summary>Clips a polygon to one side of an axis-aligned line; the lower bound is kept, the upper is not.</summary>
    private static int Clip(
        ReadOnlySpan<Vector3> input,
        int count,
        Span<Vector3> output,
        bool alongX,
        float bound,
        bool keepAbove)
    {
        int written = 0;
        for (int index = 0; index < count && written + 2 <= output.Length; index++)
        {
            Vector3 current = input[index];
            Vector3 next = input[index + 1 == count ? 0 : index + 1];
            float currentCoordinate = alongX ? current.X : current.Y;
            float nextCoordinate = alongX ? next.X : next.Y;
            float currentSide = keepAbove ? currentCoordinate - bound : bound - currentCoordinate;
            float nextSide = keepAbove ? nextCoordinate - bound : bound - nextCoordinate;
            bool currentInside = keepAbove ? currentSide >= 0f : currentSide > 0f;
            bool nextInside = keepAbove ? nextSide >= 0f : nextSide > 0f;
            if (currentInside)
                output[written++] = current;
            if (currentInside != nextInside)
                output[written++] = Vector3.Lerp(current, next, currentSide / (currentSide - nextSide));
        }
        return written;
    }

    private static float AreaXY(ReadOnlySpan<Vector3> polygon, int count, float left, float bottom)
    {
        float twice = 0f;
        for (int index = 0; index < count; index++)
        {
            Vector3 a = polygon[index];
            Vector3 b = polygon[index + 1 == count ? 0 : index + 1];
            twice += ((a.X - left) * (b.Y - bottom)) - ((b.X - left) * (a.Y - bottom));
        }
        return MathF.Abs(twice) * 0.5f;
    }

    /// <summary>Each column's solid spans as a list sorted by height, merged where they overlap.</summary>
    private sealed class SpanColumns
    {
        private int _allocated;
        private int _free = -1;

        public SpanColumns(int columns)
        {
            const int initialCapacity = 1 << 16;
            Head = new int[columns];
            Array.Fill(Head, -1);
            Min = new float[initialCapacity];
            Max = new float[initialCapacity];
            Walkable = new bool[initialCapacity];
            Next = new int[initialCapacity];
        }

        public int[] Head { get; }

        public float[] Min;

        public float[] Max;

        public bool[] Walkable;

        public int[] Next;

        public int Live { get; private set; }

        public void Add(int column, float min, float max, bool walkable)
        {
            int previous = -1;
            int current = Head[column];
            while (current != -1 && Max[current] < min)
            {
                previous = current;
                current = Next[current];
            }
            while (current != -1 && Min[current] <= max)
            {
                if (MathF.Abs(Max[current] - max) <= FlagMergeHeight)
                    walkable |= Walkable[current];
                else if (Max[current] > max)
                    walkable = Walkable[current];
                min = MathF.Min(min, Min[current]);
                max = MathF.Max(max, Max[current]);
                int following = Next[current];
                Release(current);
                current = following;
            }

            int span = Allocate();
            Min[span] = min;
            Max[span] = max;
            Walkable[span] = walkable;
            Next[span] = current;
            if (previous == -1)
                Head[column] = span;
            else
                Next[previous] = span;
        }

        private int Allocate()
        {
            Live++;
            if (_free != -1)
            {
                int reused = _free;
                _free = Next[reused];
                return reused;
            }
            if (_allocated == Min.Length)
            {
                int capacity = _allocated * 2;
                Array.Resize(ref Min, capacity);
                Array.Resize(ref Max, capacity);
                Array.Resize(ref Walkable, capacity);
                Array.Resize(ref Next, capacity);
            }
            return _allocated++;
        }

        private void Release(int span)
        {
            Live--;
            Next[span] = _free;
            _free = span;
        }
    }

    /// <summary>The footprints of walls in each column: the part of each wall polygon inside it, and its height range.</summary>
    private sealed class WallPieces
    {
        private readonly List<float> _points = new(1 << 16);

        public WallPieces(int columns)
        {
            Head = new int[columns];
            Array.Fill(Head, -1);
            ColumnCount = new int[columns];
        }

        public int[] Head { get; }

        /// <summary>How many wall pieces each column holds.</summary>
        public int[] ColumnCount { get; }

        public List<int> Next { get; } = new(1 << 14);

        public List<float> Low { get; } = new(1 << 14);

        public List<float> High { get; } = new(1 << 14);

        private List<int> PointStart { get; } = new(1 << 14);

        private List<int> PointCount { get; } = new(1 << 14);

        public int Count => Next.Count;

        public void Add(int column, ReadOnlySpan<Vector3> polygon, float low, float high)
        {
            int piece = Next.Count;
            Next.Add(Head[column]);
            Low.Add(low);
            High.Add(high);
            PointStart.Add(_points.Count / 2);
            PointCount.Add(polygon.Length);
            foreach (Vector3 point in polygon)
            {
                _points.Add(point.X);
                _points.Add(point.Y);
            }
            Head[column] = piece;
            ColumnCount[column]++;
        }

        /// <summary>The horizontal distance from a point to a wall piece's footprint; zero inside it.</summary>
        public float Distance(int piece, float x, float y)
        {
            int start = PointStart[piece];
            int count = PointCount[piece];
            if (count >= 3 && Contains(start, count, x, y))
                return 0f;
            float nearest = float.PositiveInfinity;
            for (int index = 0; index < count; index++)
            {
                int next = index + 1 == count ? 0 : index + 1;
                nearest = MathF.Min(
                    nearest,
                    SegmentDistance(
                        x,
                        y,
                        _points[(start + index) * 2],
                        _points[((start + index) * 2) + 1],
                        _points[(start + next) * 2],
                        _points[((start + next) * 2) + 1]));
            }
            return nearest;
        }

        /// <summary>
        /// The horizontal distance between a segment and a wall piece's footprint,
        /// and how far along the segment, from 0 to 1, they come nearest.
        /// </summary>
        public (float Distance, float Along) DistanceToSegment(int piece, Vector2 start, Vector2 end)
        {
            int first = PointStart[piece];
            int count = PointCount[piece];
            if (count >= 3)
            {
                if (Contains(first, count, start.X, start.Y))
                    return (0f, 0f);
                if (Contains(first, count, end.X, end.Y))
                    return (0f, 1f);
            }

            float nearest = float.PositiveInfinity;
            float nearestAlong = 0f;
            for (int index = 0; index < count; index++)
            {
                int next = index + 1 == count ? 0 : index + 1;
                var edgeStart = new Vector2(_points[(first + index) * 2], _points[((first + index) * 2) + 1]);
                var edgeEnd = new Vector2(_points[(first + next) * 2], _points[((first + next) * 2) + 1]);
                if (Crosses(start, end, edgeStart, edgeEnd, out float crossing))
                    return (0f, crossing);
                (float fromCorner, float cornerAlong) = PointToSegment(edgeStart, start, end);
                if (fromCorner < nearest)
                {
                    nearest = fromCorner;
                    nearestAlong = cornerAlong;
                }
                float fromStart = PointToSegment(start, edgeStart, edgeEnd).Distance;
                if (fromStart < nearest)
                {
                    nearest = fromStart;
                    nearestAlong = 0f;
                }
                float fromEnd = PointToSegment(end, edgeStart, edgeEnd).Distance;
                if (fromEnd < nearest)
                {
                    nearest = fromEnd;
                    nearestAlong = 1f;
                }
            }
            return (nearest, nearestAlong);
        }

        private bool Contains(int start, int count, float x, float y)
        {
            bool positive = false;
            bool negative = false;
            for (int index = 0; index < count; index++)
            {
                int next = index + 1 == count ? 0 : index + 1;
                float ax = _points[(start + index) * 2];
                float ay = _points[((start + index) * 2) + 1];
                float bx = _points[(start + next) * 2];
                float by = _points[((start + next) * 2) + 1];
                float cross = ((bx - ax) * (y - ay)) - ((by - ay) * (x - ax));
                if (cross > 1e-6f)
                    positive = true;
                else if (cross < -1e-6f)
                    negative = true;
                if (positive && negative)
                    return false;
            }
            return positive != negative;
        }

        private static float SegmentDistance(float x, float y, float ax, float ay, float bx, float by)
        {
            float dx = bx - ax;
            float dy = by - ay;
            float lengthSquared = (dx * dx) + (dy * dy);
            float t = lengthSquared > 1e-12f
                ? Math.Clamp((((x - ax) * dx) + ((y - ay) * dy)) / lengthSquared, 0f, 1f)
                : 0f;
            float px = ax + (t * dx) - x;
            float py = ay + (t * dy) - y;
            return MathF.Sqrt((px * px) + (py * py));
        }

        private static (float Distance, float Along) PointToSegment(Vector2 point, Vector2 start, Vector2 end)
        {
            Vector2 along = end - start;
            float lengthSquared = along.LengthSquared();
            float t = lengthSquared > 1e-12f
                ? Math.Clamp(Vector2.Dot(point - start, along) / lengthSquared, 0f, 1f)
                : 0f;
            return (Vector2.Distance(point, start + (along * t)), t);
        }

        private static bool Crosses(Vector2 start, Vector2 end, Vector2 edgeStart, Vector2 edgeEnd, out float along)
        {
            Vector2 segment = end - start;
            Vector2 edge = edgeEnd - edgeStart;
            float denominator = (segment.X * edge.Y) - (segment.Y * edge.X);
            along = 0f;
            if (MathF.Abs(denominator) < 1e-9f)
                return false;
            Vector2 offset = edgeStart - start;
            along = ((offset.X * edge.Y) - (offset.Y * edge.X)) / denominator;
            float onEdge = ((offset.X * segment.Y) - (offset.Y * segment.X)) / denominator;
            return along >= 0f && along <= 1f && onEdge >= 0f && onEdge <= 1f;
        }
    }
}
