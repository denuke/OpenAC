using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using AcDream.App.Streaming;
using AcDream.Content;
using AcDream.Content.Pak;
using AcDream.Core.Navigation;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using Xunit.Abstractions;
using DatEnvCell = DatReaderWriter.DBObjs.EnvCell;
using DatEnvironment = DatReaderWriter.DBObjs.Environment;
using DatGfxObj = DatReaderWriter.DBObjs.GfxObj;
using DatSetup = DatReaderWriter.DBObjs.Setup;

namespace AcDream.App.Tests.Navigation;

/// <summary>
/// Builds navigation grids from real landblocks in the installed game files,
/// published into a physics world the way the client streams them, and reports
/// what they found as numbers and as top-down images.
/// </summary>
[Trait("Lane", "InstalledDat")]
[Trait("Purpose", "Diagnostic")]
public sealed class NavMeshInstalledDatDiagnosticTests
{
    private const uint Yaraq = 0x7D64FFFFu;
    private const uint Holtburg = 0xA9B4FFFFu;
    private const uint HoltburgEast = 0xAAB4FFFFu;
    private const uint HoltburgSouth = 0xA9B3FFFFu;
    private const uint HoltburgSouthEast = 0xAAB3FFFFu;
    private const uint HumanSetup = 0x0200004Eu;

    /// <summary>The cells a route from the archmage's room to the healer's deck passes, in order.</summary>
    private static readonly uint[] YaraqRouteCells =
        [0x7D64012Eu, 0x7D640131u, 0x7D640119u, 0x7D64011Au, 0x7D640110u, 0x7D64010Fu];

    private readonly ITestOutputHelper _output;

    public NavMeshInstalledDatDiagnosticTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void YaraqArchmageRoomRoutesOutUpAndOntoTheHealersDeck()
    {
        PublishedLandblock world = PublishedLandblock.Load(RequireDatDirectory(), Yaraq, YaraqRouteCells);
        NavGrid grid = BuildAndReport("yaraq", world);
        var from = new Vector3(83.96f, 90.86f, 15.205f);
        var to = new Vector3(85.92f, 138.564f, 15.605f);

        NavRoute route = NavRouter.Find(grid, from, to, arrivalRadius: 1f);

        Report("yaraq", grid, route, from, to);
        WriteImages("yaraq", grid, route);
        WriteReachability("yaraq", grid, world, from, to, minX: 55f, minY: 75f, maxX: 115f, maxY: 150f);
        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
    }

    [Fact]
    public void HoltburgIsBuiltAndDrawn()
    {
        PublishedLandblock world = PublishedLandblock.Load(RequireDatDirectory(), Holtburg, []);
        NavGrid grid = BuildAndReport("holtburg", world);

        WriteImages("holtburg", grid, route: null);
        Assert.True(grid.Report.ClearNodes > 0);
    }

    [Fact]
    public void HoltburgRoutesAcrossTheCornerWhereFourLandblocksMeet()
    {
        PublishedLandblock world = PublishedLandblock.Load(
            RequireDatDirectory(),
            [Holtburg, HoltburgEast, HoltburgSouth, HoltburgSouthEast],
            []);
        var clock = Stopwatch.StartNew();
        NavGeometry geometry = Assert.IsType<NavGeometry>(NavGeometry.Capture(world.Engine, 96f, -96f, 192f));
        double captureMilliseconds = clock.Elapsed.TotalMilliseconds;
        NavGrid grid = NavGrid.Build(geometry, world.Body);
        _output.WriteLine(
            $"holtburg-corner: published {geometry.LandblockIds.Count} landblocks in {world.Milliseconds:0} ms; captured "
            + $"{geometry.CellTriangles.Count} cell and {geometry.ObjectTriangles.Count} object triangles and "
            + $"{geometry.Cylinders.Count} cylinders in {captureMilliseconds:0} ms");
        _output.WriteLine(
            $"holtburg-corner: {grid.Report.Spans} spans, {grid.Report.Nodes} nodes, {grid.Report.ClearNodes} clear, "
            + $"built in {grid.Report.Milliseconds:0} ms");
        Vector3 from = OnTerrain(world.Engine, 150f, 40f);
        Vector3 to = OnTerrain(world.Engine, 240f, -40f);

        NavRoute route = NavRouter.Find(grid, from, to, arrivalRadius: 1f);

        Report("holtburg-corner", grid, route, from, to);
        WriteImages("holtburg-corner", grid, route);
        bool[] reached = Reachable(grid, grid.FindNode(from, NavRouter.StartRadius, NavRouter.StartHeightTolerance));
        int clearReached = reached.Count(value => value);
        _output.WriteLine(
            $"holtburg-corner: {clearReached} of {grid.Report.ClearNodes} clear nodes are reachable from the start");
        Assert.Equal(4, geometry.LandblockIds.Count);
        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        Assert.True(clearReached > grid.Report.ClearNodes / 2);
    }

    /// <summary>A point on the terrain of whichever published landblock holds it.</summary>
    private static Vector3 OnTerrain(PhysicsEngine engine, float x, float y)
    {
        foreach (uint landblockId in engine.LandblockIds)
        {
            Assert.True(engine.TryGetLandblockCollision(landblockId, out TerrainSurface terrain, out _, out Vector3 offset));
            float localX = x - offset.X;
            float localY = y - offset.Y;
            if (localX >= 0f && localX < NavGeometry.LandblockSize && localY >= 0f && localY < NavGeometry.LandblockSize)
                return new Vector3(x, y, terrain.SampleSurfacePolygon(localX, localY).Z);
        }
        throw new InvalidOperationException($"No published landblock holds ({x}, {y}).");
    }

    private NavGrid BuildAndReport(string name, PublishedLandblock world)
    {
        var clock = Stopwatch.StartNew();
        NavGeometry geometry = Assert.IsType<NavGeometry>(NavGeometry.CaptureLandblock(world.Engine, world.LandblockId));
        double captureMilliseconds = clock.Elapsed.TotalMilliseconds;
        NavGrid grid = NavGrid.Build(geometry, world.Body);
        NavGridBuildReport report = grid.Report;
        _output.WriteLine(
            $"{name}: published in {world.Milliseconds:0} ms; body radius {world.Body.Radius}, height {world.Body.Height}, "
            + $"step up {world.Body.StepUpHeight}, step down {world.Body.StepDownHeight}");
        _output.WriteLine(
            $"{name}: captured {geometry.CellTriangles.Count} cell triangles, {geometry.ObjectTriangles.Count} object "
            + $"triangles and {geometry.Cylinders.Count} cylinders in {captureMilliseconds:0} ms");
        _output.WriteLine(
            $"{name}: {report.Spans} spans, {report.Nodes} nodes, {report.ClearNodes} clear, built in {report.Milliseconds:0} ms");
        return grid;
    }

    private void Report(string name, NavGrid grid, NavRoute route, Vector3 from, Vector3 to)
    {
        _output.WriteLine(
            $"{name}: {route.Outcome} ({route.Reason}), {route.Path.Count} nodes, {Math.Max(0, route.Legs.Count - 1)} legs, "
            + $"{route.Length:0.0} m, {route.Expansions} expansions in {route.Milliseconds:0} ms");
        foreach (Vector3 leg in route.Legs)
            _output.WriteLine($"  leg ({leg.X:0.00}, {leg.Y:0.00}, {leg.Z:0.00})");
        if (route.Outcome == NavRouteOutcome.Routed)
            return;
        DescribeNear(grid, "start", from);
        DescribeNear(grid, "goal", to);
    }

    private void DescribeNear(NavGrid grid, string label, Vector3 point)
    {
        int centreX = (int)MathF.Floor((point.X - grid.OriginX) / grid.CellSize);
        int centreY = (int)MathF.Floor((point.Y - grid.OriginY) / grid.CellSize);
        for (int y = centreY - 2; y <= centreY + 2; y++)
        {
            for (int x = centreX - 2; x <= centreX + 2; x++)
            {
                (int first, int count) = grid.NodesInColumn(x, y);
                var levels = new StringBuilder();
                for (int node = first; node < first + count; node++)
                {
                    levels.Append(
                        $" {grid.Position(node).Z:0.00}{(grid.IsClear(node) ? "" : "!")}(b{grid.BorderDistance(node)})");
                }
                _output.WriteLine($"  {label} column ({x}, {y}):{(count == 0 ? " none" : levels.ToString())}");
            }
        }
    }

    private void WriteReachability(
        string name,
        NavGrid grid,
        PublishedLandblock world,
        Vector3 from,
        Vector3 to,
        float minX,
        float minY,
        float maxX,
        float maxY)
    {
        int start = grid.FindNode(from, NavRouter.StartRadius, NavRouter.StartHeightTolerance);
        int goal = grid.FindNode(to, 1f, NavRouter.GoalHeightTolerance);
        bool[] fromStart = Reachable(grid, start);
        bool[] fromGoal = Reachable(grid, goal);
        _output.WriteLine(
            $"{name}: {fromStart.Count(reached => reached)} nodes reachable from the start, "
            + $"{fromGoal.Count(reached => reached)} from the goal");
        foreach ((uint cell, Vector3 at) in world.CellOrigins)
            _output.WriteLine($"{name}: cell 0x{cell:X8} origin ({at.X:0.00}, {at.Y:0.00}, {at.Z:0.00})");
        foreach ((string band, float low, float high) in new[] { ("ground", 11f, 13.4f), ("middle", 12.2f, 15.4f), ("upper", 13.4f, 18f) })
        {
            string path = Path.Combine(ImageDirectory(), $"{name}-reach-{band}.png");
            NavGridImage.WriteReachability(
                path, grid, fromStart, fromGoal, minX, minY, maxX, maxY, low, high, world.CellOrigins.Values, scale: 4);
            _output.WriteLine($"{name}: image {path}");
        }
    }

    private static bool[] Reachable(NavGrid grid, int start)
    {
        var reached = new bool[grid.NodeCount];
        if (start < 0)
            return reached;
        var frontier = new Queue<int>();
        reached[start] = true;
        frontier.Enqueue(start);
        while (frontier.TryDequeue(out int node))
        {
            for (int direction = 0; direction < NavGrid.DirectionCount; direction++)
            {
                int next = grid.Link(node, direction);
                if (next < 0 || reached[next] || !grid.IsClear(next))
                    continue;
                reached[next] = true;
                frontier.Enqueue(next);
            }
        }
        return reached;
    }

    private static string ImageDirectory()
    {
        string directory = System.Environment.GetEnvironmentVariable("ACDREAM_NAV_IMAGE_DIR")
            ?? Path.Combine(Path.GetTempPath(), "acdream-navmesh");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private void WriteImages(string name, NavGrid grid, NavRoute? route)
    {
        string directory = ImageDirectory();
        string whole = Path.Combine(directory, $"{name}-landblock.png");
        NavGridImage.WriteLandblock(whole, grid, route);
        _output.WriteLine($"{name}: image {whole}");
        if (route is { Outcome: NavRouteOutcome.Routed })
        {
            string detail = Path.Combine(directory, $"{name}-route.png");
            NavGridImage.WriteRouteDetail(detail, grid, route, scale: 4);
            _output.WriteLine($"{name}: image {detail}");
        }
    }

    private static string RequireDatDirectory()
    {
        string? directory = System.Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        Assert.False(string.IsNullOrWhiteSpace(directory), "Set ACDREAM_DAT_DIR to the installed game files.");
        Assert.True(Directory.Exists(directory), $"The game files are missing: '{directory}'.");
        return directory!;
    }

    private sealed record PublishedLandblock(
        PhysicsEngine Engine,
        uint LandblockId,
        NavBody Body,
        IReadOnlyDictionary<uint, Vector3> CellOrigins,
        double Milliseconds)
    {
        public static PublishedLandblock Load(string datDirectory, uint landblockId, IReadOnlyList<uint> cellsToLocate) =>
            Load(datDirectory, [landblockId], cellsToLocate);

        /// <summary>
        /// Publishes landblocks into one physics world the way the client streams
        /// them, placed around the first, whose south-west corner is the origin.
        /// </summary>
        public static PublishedLandblock Load(
            string datDirectory,
            IReadOnlyList<uint> landblockIds,
            IReadOnlyList<uint> cellsToLocate)
        {
            var clock = Stopwatch.StartNew();
            using var dats = new BoundedTestDatCollection(datDirectory);
            var bounded = (IDatReaderWriter)dats;
            Region region = Assert.IsType<Region>(bounded.Get<Region>(0x13000000u));
            float[] heights = region.LandDefs.LandHeightTable;
            DatSetup human = Assert.IsType<DatSetup>(bounded.Get<DatSetup>(HumanSetup));

            using var collisionSource = new InstalledDatCollisionSource(bounded);
            var factory = new LandblockBuildFactory(bounded, collisionSource, new object(), heights);
            var cache = PhysicsDataCache.CreateProduction();
            var engine = new PhysicsEngine { DataCache = cache };
            int baseX = (int)((landblockIds[0] >> 24) & 0xFFu);
            int baseY = (int)((landblockIds[0] >> 16) & 0xFFu);
            var offsets = new Dictionary<uint, Vector3>();
            foreach (uint landblockId in landblockIds)
            {
                int blockX = (int)((landblockId >> 24) & 0xFFu);
                int blockY = (int)((landblockId >> 16) & 0xFFu);
                var request = new LandblockBuildRequest(
                    landblockId,
                    LandblockStreamJobKind.LoadNear,
                    Generation: 1,
                    new LandblockBuildOrigin(baseX, baseY));
                LandblockBuild build = Assert.IsType<LandblockBuild>(factory.Build(request));
                LandblockCollisionBuild collisions = Assert.IsType<LandblockCollisionBuild>(build.Collisions);

                var origin = new Vector3((blockX - baseX) * 192f, (blockY - baseY) * 192f, 0f);
                offsets[landblockId & 0xFFFF0000u] = origin;
                TerrainSurface terrain = LandblockPhysicsContentBuilder.BuildTerrainSurface(build.Landblock, heights);
                var cellSurfaces = new List<CellSurface>();
                var portalPlanes = new List<PortalPlane>();
                LandblockPhysicsContentBuilder.PublishPreparedCells(
                    cache,
                    build.Landblock,
                    collisions,
                    origin,
                    cellSurfaces,
                    portalPlanes);
                LandblockPhysicsContentBuilder.CacheBuildings(cache, build.Landblock, terrain, origin);
                LandblockPhysicsContentBuilder.CachePreparedObjects(cache, collisions);
                engine.AddLandblock(build.LandblockId, terrain, cellSurfaces, portalPlanes, origin.X, origin.Y);
                _ = LandblockPhysicsContentBuilder.PublishStaticCollision(engine, cache, build.Landblock, collisions, origin);
            }

            var cellOrigins = new Dictionary<uint, Vector3>();
            foreach (uint cellId in cellsToLocate)
            {
                if (bounded.Get<DatEnvCell>(cellId) is { } cell
                    && offsets.TryGetValue(cellId & 0xFFFF0000u, out Vector3 offset))
                {
                    cellOrigins[cellId] = cell.Position.Origin + offset;
                }
            }

            return new PublishedLandblock(
                engine,
                landblockIds[0],
                NavBody.Player(human.StepUpHeight, human.StepDownHeight),
                cellOrigins,
                clock.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>Top-down pictures of a grid, north up, a pixel per column.</summary>
    private static class NavGridImage
    {
        private static readonly uint[] CrcTable = BuildCrcTable();

        public static void WriteLandblock(string path, NavGrid grid, NavRoute? route)
        {
            byte[] pixels = Paint(grid, 0, 0, grid.Side, grid.Side);
            if (route is { Outcome: NavRouteOutcome.Routed })
                DrawRoute(pixels, grid.Side, grid.Side, grid, route, 0, 0, scale: 1);
            WritePng(path, grid.Side, grid.Side, pixels);
        }

        public static void WriteRouteDetail(string path, NavGrid grid, NavRoute route, int scale)
        {
            const int margin = 32;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (Vector3 point in route.Path)
            {
                int x = (int)MathF.Floor((point.X - grid.OriginX) / grid.CellSize);
                int y = (int)MathF.Floor((point.Y - grid.OriginY) / grid.CellSize);
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
            minX = Math.Max(0, minX - margin);
            minY = Math.Max(0, minY - margin);
            maxX = Math.Min(grid.Side - 1, maxX + margin);
            maxY = Math.Min(grid.Side - 1, maxY + margin);
            int width = maxX - minX + 1;
            int height = maxY - minY + 1;
            byte[] small = Paint(grid, minX, minY, width, height);
            byte[] large = new byte[width * scale * height * scale * 3];
            for (int row = 0; row < height * scale; row++)
            {
                for (int column = 0; column < width * scale; column++)
                {
                    int source = (((row / scale) * width) + (column / scale)) * 3;
                    int target = ((row * width * scale) + column) * 3;
                    large[target] = small[source];
                    large[target + 1] = small[source + 1];
                    large[target + 2] = small[source + 2];
                }
            }
            DrawRoute(large, width * scale, height * scale, grid, route, minX, minY, scale);
            WritePng(path, width * scale, height * scale, large);
        }

        public static void WriteReachability(
            string path,
            NavGrid grid,
            bool[] fromStart,
            bool[] fromGoal,
            float minX,
            float minY,
            float maxX,
            float maxY,
            float low,
            float high,
            IEnumerable<Vector3> markers,
            int scale)
        {
            int x0 = Math.Max(0, (int)MathF.Floor((minX - grid.OriginX) / grid.CellSize));
            int y0 = Math.Max(0, (int)MathF.Floor((minY - grid.OriginY) / grid.CellSize));
            int x1 = Math.Min(grid.Side - 1, (int)MathF.Floor((maxX - grid.OriginX) / grid.CellSize));
            int y1 = Math.Min(grid.Side - 1, (int)MathF.Floor((maxY - grid.OriginY) / grid.CellSize));
            int width = x1 - x0 + 1;
            int height = y1 - y0 + 1;
            var pixels = new byte[width * scale * height * scale * 3];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    (int first, int count) = grid.NodesInColumn(x0 + x, y0 + y);
                    int chosen = -1;
                    for (int node = first; node < first + count; node++)
                    {
                        float z = grid.Position(node).Z;
                        if (z >= low && z <= high)
                            chosen = node;
                    }
                    (byte red, byte green, byte blue) colour =
                        chosen < 0 ? (count == 0 ? ((byte)18, (byte)18, (byte)24) : ((byte)55, (byte)55, (byte)65))
                        : !grid.IsClear(chosen) ? ((byte)150, (byte)40, (byte)30)
                        : fromStart[chosen] && fromGoal[chosen] ? ((byte)255, (byte)255, (byte)255)
                        : fromStart[chosen] ? ((byte)240, (byte)200, (byte)40)
                        : fromGoal[chosen] ? ((byte)230, (byte)60, (byte)220)
                        : ((byte)60, (byte)150, (byte)80);
                    int top = (height - 1 - y) * scale;
                    for (int row = top; row < top + scale; row++)
                    {
                        for (int column = x * scale; column < (x + 1) * scale; column++)
                            Set(pixels, ((row * width * scale) + column) * 3, colour.red, colour.green, colour.blue);
                    }
                }
            }
            foreach (Vector3 marker in markers)
            {
                int centreX = (int)((((marker.X - grid.OriginX) / grid.CellSize) - x0) * scale);
                int centreY = (height * scale) - 1 - (int)((((marker.Y - grid.OriginY) / grid.CellSize) - y0) * scale);
                for (int dy = -3; dy <= 3; dy++)
                {
                    for (int dx = -3; dx <= 3; dx++)
                    {
                        int px = centreX + dx;
                        int py = centreY + dy;
                        if ((uint)px < (uint)(width * scale) && (uint)py < (uint)(height * scale))
                            Set(pixels, ((py * width * scale) + px) * 3, 0, 230, 255);
                    }
                }
            }
            WritePng(path, width * scale, height * scale, pixels);
        }

        private static byte[] Paint(NavGrid grid, int startX, int startY, int width, int height)
        {
            float low = float.PositiveInfinity;
            float high = float.NegativeInfinity;
            for (int node = 0; node < grid.NodeCount; node++)
            {
                float z = grid.Position(node).Z;
                low = MathF.Min(low, z);
                high = MathF.Max(high, z);
            }
            float span = MathF.Max(1f, high - low);

            var pixels = new byte[width * height * 3];
            for (int y = 0; y < height; y++)
            {
                int row = height - 1 - y;
                for (int x = 0; x < width; x++)
                {
                    int offset = ((row * width) + x) * 3;
                    (int first, int count) = grid.NodesInColumn(startX + x, startY + y);
                    if (count == 0)
                    {
                        Set(pixels, offset, 18, 18, 24);
                        continue;
                    }

                    int shown = first + count - 1;
                    int clearLevels = 0;
                    for (int node = first + count - 1; node >= first; node--)
                    {
                        if (!grid.IsClear(node))
                            continue;
                        if (clearLevels == 0)
                            shown = node;
                        clearLevels++;
                    }
                    float t = (grid.Position(shown).Z - low) / span;
                    if (clearLevels == 0)
                        Set(pixels, offset, 130, 40, 30);
                    else if (clearLevels > 1)
                        Set(pixels, offset, (byte)(60 + (100 * t)), (byte)(110 + (100 * t)), 240);
                    else
                        Set(pixels, offset, (byte)(40 + (140 * t)), (byte)(100 + (150 * t)), (byte)(50 + (90 * t)));
                }
            }
            return pixels;
        }

        private static void DrawRoute(
            byte[] pixels,
            int width,
            int height,
            NavGrid grid,
            NavRoute route,
            int startX,
            int startY,
            int scale)
        {
            foreach (Vector3 point in route.Path)
                Plot(pixels, width, height, grid, point, startX, startY, scale, 255, 220, 0, radius: scale / 2);
            foreach (Vector3 leg in route.Legs)
                Plot(pixels, width, height, grid, leg, startX, startY, scale, 255, 255, 255, radius: scale + 1);
        }

        private static void Plot(
            byte[] pixels,
            int width,
            int height,
            NavGrid grid,
            Vector3 point,
            int startX,
            int startY,
            int scale,
            byte red,
            byte green,
            byte blue,
            int radius)
        {
            int centreX = (int)((((point.X - grid.OriginX) / grid.CellSize) - startX) * scale);
            int centreY = height - 1 - (int)((((point.Y - grid.OriginY) / grid.CellSize) - startY) * scale);
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int x = centreX + dx;
                    int y = centreY + dy;
                    if ((uint)x < (uint)width && (uint)y < (uint)height)
                        Set(pixels, ((y * width) + x) * 3, red, green, blue);
                }
            }
        }

        private static void Set(byte[] pixels, int offset, byte red, byte green, byte blue)
        {
            pixels[offset] = red;
            pixels[offset + 1] = green;
            pixels[offset + 2] = blue;
        }

        private static void WritePng(string path, int width, int height, byte[] rgb)
        {
            using FileStream file = File.Create(path);
            file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            var header = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header, width);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
            header[8] = 8;
            header[9] = 2;
            WriteChunk(file, "IHDR", header);

            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                for (int row = 0; row < height; row++)
                {
                    zlib.WriteByte(0);
                    zlib.Write(rgb, row * width * 3, width * 3);
                }
            }
            WriteChunk(file, "IDAT", compressed.ToArray());
            WriteChunk(file, "IEND", []);
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            var scratch = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(scratch, data.Length);
            stream.Write(scratch);
            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            stream.Write(typeBytes);
            stream.Write(data);
            uint crc = 0xFFFFFFFFu;
            foreach (byte value in typeBytes)
                crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
            foreach (byte value in data)
                crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
            BinaryPrimitives.WriteUInt32BigEndian(scratch, crc ^ 0xFFFFFFFFu);
            stream.Write(scratch);
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < 256; index++)
            {
                uint value = index;
                for (int bit = 0; bit < 8; bit++)
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                table[index] = value;
            }
            return table;
        }
    }

    private sealed class InstalledDatCollisionSource : IPreparedCollisionSource
    {
        private readonly IDatReaderWriter _dats;
        private long _probes;
        private long _reads;
        private long _loaded;
        private long _missing;

        public InstalledDatCollisionSource(IDatReaderWriter dats) => _dats = dats;

        public PreparedCollisionSourceStats CollisionStats => new(_probes, _reads, _loaded, _missing, Corrupt: 0);

        public PreparedAssetPresence ProbeCollision(PakAssetType type, uint sourceFileId)
        {
            _probes++;
            bool available = type switch
            {
                PakAssetType.GfxObjCollision => _dats.Get<DatGfxObj>(sourceFileId) is not null,
                PakAssetType.SetupCollision => _dats.Get<DatSetup>(sourceFileId) is not null,
                PakAssetType.CellStructureCollision => TryResolveCell(sourceFileId, out _, out _),
                PakAssetType.EnvCellTopology => TryResolveCell(sourceFileId, out _, out _),
                _ => throw new ArgumentOutOfRangeException(nameof(type)),
            };
            return available ? PreparedAssetPresence.Available : PreparedAssetPresence.Missing;
        }

        public PreparedCollisionReadResult<FlatGfxObjCollisionAsset> ReadGfxObjCollision(
            uint sourceFileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _reads++;
            DatGfxObj? value = _dats.Get<DatGfxObj>(sourceFileId);
            return value is null
                ? Missing<FlatGfxObjCollisionAsset>()
                : Loaded(FlatCollisionAssetBuilder.FlattenGfxObj(value));
        }

        public PreparedCollisionReadResult<FlatSetupCollision> ReadSetupCollision(
            uint sourceFileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _reads++;
            DatSetup? value = _dats.Get<DatSetup>(sourceFileId);
            return value is null
                ? Missing<FlatSetupCollision>()
                : Loaded(FlatCollisionAssetBuilder.FlattenSetup(value));
        }

        public PreparedCollisionReadResult<FlatCellStructureCollisionAsset> ReadCellStructureCollision(
            uint sourceFileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _reads++;
            return TryResolveCell(sourceFileId, out _, out var structure)
                ? Loaded(FlatCollisionAssetBuilder.FlattenCellStructure(structure))
                : Missing<FlatCellStructureCollisionAsset>();
        }

        public PreparedCollisionReadResult<FlatEnvCellTopology> ReadEnvCellTopology(
            uint sourceFileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _reads++;
            if (!TryResolveCell(sourceFileId, out DatEnvCell? cell, out var structure))
                return Missing<FlatEnvCellTopology>();
            FlatCellStructureCollisionAsset flat = FlatCollisionAssetBuilder.FlattenCellStructure(structure);
            return Loaded(FlatCollisionAssetBuilder.FlattenEnvCellTopology(sourceFileId, cell!, flat.PortalPolygons));
        }

        public void Dispose()
        {
        }

        private bool TryResolveCell(
            uint sourceFileId,
            out DatEnvCell? cell,
            out DatReaderWriter.Types.CellStruct structure)
        {
            cell = _dats.Get<DatEnvCell>(sourceFileId);
            if (cell is not null
                && _dats.Get<DatEnvironment>(0x0D000000u | cell.EnvironmentId) is { } environment
                && environment.Cells.TryGetValue(cell.CellStructure, out var found)
                && found is not null)
            {
                structure = found;
                return true;
            }
            structure = null!;
            return false;
        }

        private PreparedCollisionReadResult<T> Loaded<T>(T value)
            where T : class
        {
            _loaded++;
            return PreparedCollisionReadResult<T>.Loaded(value);
        }

        private PreparedCollisionReadResult<T> Missing<T>()
            where T : class
        {
            _missing++;
            return PreparedCollisionReadResult<T>.Missing;
        }
    }
}
