using System.Numerics;
using System.Runtime.CompilerServices;
using AcDream.Core.Physics;

namespace AcDream.Core.Navigation;

/// <summary>The shape a navigation grid is built for.</summary>
public readonly record struct NavBody(
    float Radius,
    float Height,
    float StepUpHeight,
    float StepDownHeight)
{
    /// <summary>The player's body as the movement controller sweeps it: spheres of 0.48 reaching 1.835 high.</summary>
    public static NavBody Player(float stepUpHeight, float stepDownHeight) =>
        new(0.48f, 1.835f, stepUpHeight, stepDownHeight);
}

public readonly record struct NavTriangle(Vector3 A, Vector3 B, Vector3 C);

/// <summary>A cylinder or sphere obstacle, as the vertical extent it fills.</summary>
public readonly record struct NavCylinder(Vector3 Base, float Radius, float Height);

/// <summary>One landblock's terrain, and the world position of its south-west corner.</summary>
public readonly record struct NavTerrain(TerrainSurface Surface, float OriginX, float OriginY);

/// <summary>
/// The fixed collision geometry of a square region of the world, copied out of
/// the physics world so a grid can be built from it on another thread: the
/// terrain, interior cell polygons, building shells and placed objects of every
/// resident landblock the region overlaps. Objects the server places, such as
/// doors and creatures, are not included.
/// </summary>
public sealed class NavGeometry
{
    public const float LandblockSize = 192f;

    public NavGeometry(
        float originX,
        float originY,
        float size,
        IReadOnlyList<NavTerrain> terrains,
        IReadOnlyList<NavTriangle> cellTriangles,
        IReadOnlyList<NavTriangle> objectTriangles,
        IReadOnlyList<NavCylinder> cylinders)
    {
        if (!(size > 0f) || !float.IsFinite(size))
            throw new ArgumentOutOfRangeException(nameof(size));
        ArgumentNullException.ThrowIfNull(terrains);
        ArgumentNullException.ThrowIfNull(cellTriangles);
        ArgumentNullException.ThrowIfNull(objectTriangles);
        ArgumentNullException.ThrowIfNull(cylinders);
        OriginX = originX;
        OriginY = originY;
        Size = size;
        Terrains = terrains;
        CellTriangles = cellTriangles;
        ObjectTriangles = objectTriangles;
        Cylinders = cylinders;
    }

    /// <summary>The world position of the region's south-west corner.</summary>
    public float OriginX { get; }

    public float OriginY { get; }

    /// <summary>The length of the region's sides in meters.</summary>
    public float Size { get; }

    public IReadOnlyList<NavTerrain> Terrains { get; }

    /// <summary>Interior cell polygons. Terrain is left out of every column they cover.</summary>
    public IReadOnlyList<NavTriangle> CellTriangles { get; }

    /// <summary>Building shells and placed objects.</summary>
    public IReadOnlyList<NavTriangle> ObjectTriangles { get; }

    public IReadOnlyList<NavCylinder> Cylinders { get; }

    /// <summary>The resident landblocks the region overlaps.</summary>
    public IReadOnlyList<uint> LandblockIds { get; init; } = [];

    /// <summary>
    /// Copies one resident landblock's fixed collision geometry, or returns null
    /// when the landblock has no collision in the physics world.
    /// </summary>
    public static NavGeometry? CaptureLandblock(PhysicsEngine engine, uint landblockId)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return engine.TryGetLandblockCollision(landblockId, out _, out _, out Vector3 offset)
            ? Capture(engine, offset.X, offset.Y, LandblockSize)
            : null;
    }

    /// <summary>
    /// Copies the fixed collision geometry of a square region, or returns null
    /// when no resident landblock overlaps it. Call it on the thread that owns
    /// the physics world.
    /// </summary>
    public static NavGeometry? Capture(PhysicsEngine engine, float originX, float originY, float size)
    {
        ArgumentNullException.ThrowIfNull(engine);
        IReadOnlyList<uint> landblocks = OverlappingLandblocks(engine, originX, originY, size);
        if (landblocks.Count == 0)
            return null;

        var terrains = new List<NavTerrain>();
        var cellTriangles = new List<NavTriangle>();
        var objectTriangles = new List<NavTriangle>();
        var owners = new HashSet<uint>();
        foreach (uint landblockId in landblocks)
        {
            engine.TryGetLandblockCollision(
                landblockId,
                out TerrainSurface terrain,
                out IReadOnlyList<CellSurface> cells,
                out Vector3 offset);
            terrains.Add(new NavTerrain(terrain, offset.X, offset.Y));
            foreach (CellSurface cell in cells)
            {
                foreach ((Vector3 a, Vector3 b, Vector3 c) in cell.Triangles)
                    cellTriangles.Add(new NavTriangle(a, b, c));
            }
            AddBuildingShells(engine.DataCache, landblockId, objectTriangles);
            owners.UnionWith(engine.ShadowObjects.CaptureStaticOwnersForLandblock(landblockId));
        }
        if (landblocks.Count == 0)
            return null;

        var cylinders = new List<NavCylinder>();
        foreach (ShadowEntry entry in engine.ShadowObjects.AllEntriesForDebug())
        {
            if (!owners.Contains(entry.EntityId)
                || ((PhysicsStateFlags)entry.State).HasFlag(PhysicsStateFlags.Ethereal))
            {
                continue;
            }
            switch (entry.CollisionType)
            {
                case ShadowCollisionType.Cylinder:
                    cylinders.Add(new NavCylinder(
                        entry.Position,
                        entry.Radius,
                        entry.CylHeight > 0f ? entry.CylHeight : entry.Radius * 2f));
                    break;
                case ShadowCollisionType.Sphere:
                    cylinders.Add(new NavCylinder(
                        entry.Position - new Vector3(0f, 0f, entry.Radius),
                        entry.Radius,
                        entry.Radius * 2f));
                    break;
                default:
                    AddObjectTriangles(
                        engine.DataCache?.GetGfxObj(entry.GfxObjId),
                        Matrix4x4.CreateScale(entry.Scale)
                            * Matrix4x4.CreateFromQuaternion(entry.Rotation)
                            * Matrix4x4.CreateTranslation(entry.Position),
                        objectTriangles);
                    break;
            }
        }

        return new NavGeometry(originX, originY, size, terrains, cellTriangles, objectTriangles, cylinders)
        {
            LandblockIds = landblocks,
        };
    }

    /// <summary>
    /// The resident landblocks a square region overlaps, by their terrain's square
    /// or by the extent of their interior cells, which in a dungeon can lie far
    /// outside that square.
    /// </summary>
    public static IReadOnlyList<uint> OverlappingLandblocks(PhysicsEngine engine, float originX, float originY, float size)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var overlapping = new List<uint>();
        foreach (uint landblockId in engine.LandblockIds)
        {
            if (!engine.TryGetLandblockCollision(landblockId, out _, out IReadOnlyList<CellSurface> cells, out Vector3 offset))
                continue;
            bool terrain = offset.X < originX + size
                && offset.X + LandblockSize > originX
                && offset.Y < originY + size
                && offset.Y + LandblockSize > originY;
            if (terrain || ExtentOf(cells).Overlaps(originX, originY, size))
                overlapping.Add(landblockId);
        }
        return overlapping;
    }

    /// <summary>
    /// The horizontal extent of a resident landblock's interior cells, or false
    /// when the physics world holds none for it.
    /// </summary>
    public static bool TryMeasureCells(PhysicsEngine engine, uint landblockId, out Vector2 minimum, out Vector2 maximum)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (!engine.TryGetLandblockCollision(landblockId, out _, out IReadOnlyList<CellSurface> cells, out _))
        {
            minimum = new Vector2(float.PositiveInfinity);
            maximum = new Vector2(float.NegativeInfinity);
            return false;
        }
        CellExtent extent = ExtentOf(cells);
        minimum = extent.Minimum;
        maximum = extent.Maximum;
        return !extent.IsEmpty;
    }

    /// <summary>A landblock's cell list is replaced, never edited, so each list's extent is measured once.</summary>
    private static readonly ConditionalWeakTable<IReadOnlyList<CellSurface>, CellExtent> CellExtents = new();

    private static CellExtent ExtentOf(IReadOnlyList<CellSurface> cells) =>
        CellExtents.GetValue(cells, static list => new CellExtent(list));

    private sealed class CellExtent
    {
        public CellExtent(IReadOnlyList<CellSurface> cells)
        {
            var low = new Vector2(float.PositiveInfinity);
            var high = new Vector2(float.NegativeInfinity);
            foreach (CellSurface cell in cells)
            {
                foreach ((Vector3 a, Vector3 b, Vector3 c) in cell.Triangles)
                {
                    low = Vector2.Min(low, Vector2.Min(Flat(a), Vector2.Min(Flat(b), Flat(c))));
                    high = Vector2.Max(high, Vector2.Max(Flat(a), Vector2.Max(Flat(b), Flat(c))));
                }
            }
            Minimum = low;
            Maximum = high;
        }

        public Vector2 Minimum { get; }

        public Vector2 Maximum { get; }

        public bool IsEmpty => Minimum.X > Maximum.X;

        public bool Overlaps(float originX, float originY, float size) =>
            !IsEmpty
            && Minimum.X < originX + size
            && Maximum.X > originX
            && Minimum.Y < originY + size
            && Maximum.Y > originY;
    }

    private static Vector2 Flat(Vector3 point) => new(point.X, point.Y);

    private static void AddBuildingShells(PhysicsDataCache? cache, uint landblockId, List<NavTriangle> triangles)
    {
        if (cache is null)
            return;
        uint prefix = landblockId & 0xFFFF0000u;
        foreach (uint landcellId in cache.BuildingIds)
        {
            if ((landcellId & 0xFFFF0000u) != prefix
                || cache.GetBuilding(landcellId) is not { ModelId: not 0u } building)
            {
                continue;
            }
            AddObjectTriangles(cache.GetGfxObj(building.ModelId), building.WorldTransform, triangles);
        }
    }

    private static void AddObjectTriangles(
        GfxObjPhysics? physics,
        Matrix4x4 placement,
        List<NavTriangle> triangles)
    {
        if (physics?.FlatPhysicsBsp?.PolygonTable is { } table)
        {
            foreach (FlatCollisionPolygon polygon in table.Polygons)
            {
                FlatIndexRange range = polygon.VertexRange;
                if (range.Count < 3)
                    continue;
                Vector3 first = Vector3.Transform(table.Vertices[range.Start], placement);
                Vector3 previous = Vector3.Transform(table.Vertices[range.Start + 1], placement);
                for (int offset = 2; offset < range.Count; offset++)
                {
                    Vector3 current = Vector3.Transform(table.Vertices[range.Start + offset], placement);
                    triangles.Add(new NavTriangle(first, previous, current));
                    previous = current;
                }
            }
            return;
        }

        if (physics?.PhysicsPolygons is not { } polygons || physics.Vertices is not { } vertexArray)
            return;
        var corners = new List<Vector3>(8);
        foreach (var polygon in polygons.Values)
        {
            corners.Clear();
            foreach (var vertexId in polygon.VertexIds)
            {
                if (!vertexArray.Vertices.TryGetValue((ushort)vertexId, out var vertex))
                {
                    corners.Clear();
                    break;
                }
                corners.Add(Vector3.Transform(vertex.Origin, placement));
            }
            for (int index = 2; index < corners.Count; index++)
                triangles.Add(new NavTriangle(corners[0], corners[index - 1], corners[index]));
        }
    }
}
