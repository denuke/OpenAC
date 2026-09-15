using System.Numerics;
using AcDream.App.Rendering.Wb;
using AcDream.Content;
using AcDream.Core.Meshing;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;
using AcDream.App.Tests.UI.Layout;
using DatEnvironment = DatReaderWriter.DBObjs.Environment;

namespace AcDream.App.Tests.Rendering;

/// <summary>
/// The reported dungeon (landblock 0x0019) places its cells far from the origin
/// of the landblock they are filed under. Its surfaces are ordinary
/// block-compressed images, so nothing in the content explains the blank walls
/// that were reported; the indoor render window did, by dropping the landblock
/// the player was standing in.
/// </summary>
[Trait("Lane", "InstalledDat")]
public sealed class GoldLegionKeepIndoorRenderInstalledDatTests
{
    private const uint LandblockId = 0x0019FFFFu;

    /// <summary>Cells the frame walk draws at the reported spot.</summary>
    private static readonly uint[] ReportedCells =
    {
        0x001901D0u, 0x001901D1u, 0x0019020Cu, 0x0019020Du, 0x0019020Eu, 0x0019020Fu,
    };

    /// <summary>The reported standing position, in the frame of the landblock.</summary>
    private static readonly Vector3 ReportedCamera = new(78.812553f, -791.770569f, 12.004999f);

    private const int NearRadius = 4;

    private readonly ITestOutputHelper _output;

    public GoldLegionKeepIndoorRenderInstalledDatTests(ITestOutputHelper output) =>
        _output = output;

    private static string DatDirectory =>
        System.Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
        ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "Documents",
            "Asheron's Call");

    [InstalledDatFact]
    public void EveryReportedCellSurfaceResolvesToAnAuthoredTexture()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        var extractor = new MeshExtractor(
            new DatCollectionAdapter(dats),
            NullLogger.Instance,
            sideStagedSink: null);

        foreach (uint cellId in ReportedCells)
        {
            EnvCell envCell = Assert.IsType<EnvCell>(dats.Get<EnvCell>(cellId));
            Assert.NotEqual(0u, envCell.EnvironmentId);

            // Every surface the cell names must be an authored image surface,
            // not the untextured solid-colour branch and not a missing record.
            var authoredImageSurfaces = new HashSet<uint>();
            foreach (ushort raw in envCell.Surfaces)
            {
                uint surfaceId = 0x08000000u | raw;
                Assert.True(
                    dats.TryGet<Surface>(surfaceId, out Surface? surface) && surface is not null,
                    $"cell 0x{cellId:X8} names surface 0x{surfaceId:X8}, which is not in the DAT set.");
                if (RetailUntexturedSurfacePolicy.IsUntextured(surface!.Type))
                    continue;

                Assert.True(
                    dats.TryGet<SurfaceTexture>(surface.OrigTextureId, out SurfaceTexture? surfaceTexture)
                    && surfaceTexture is not null
                    && surfaceTexture.Textures.Count != 0,
                    $"surface 0x{surfaceId:X8} has no texture record.");
                uint renderSurfaceId = surfaceTexture!.Textures[0];
                Assert.True(
                    dats.TryGet<RenderSurface>(renderSurfaceId, out RenderSurface? renderSurface)
                    && renderSurface is not null,
                    $"surface 0x{surfaceId:X8} points at missing image 0x{renderSurfaceId:X8}.");
                Assert.NotNull(renderSurface!.SourceData);
                Assert.NotEmpty(renderSurface.SourceData!);
                authoredImageSurfaces.Add(surfaceId);
            }

            // And the production extraction must hand every drawn subset a real
            // payload, never the blank one-colour stand-in.
            ObjectMeshData? mesh = extractor.PrepareMeshData(
                0x1_0000_0000uL | cellId,
                isSetup: false);
            Assert.NotNull(mesh);

            var drawnSurfaces = new HashSet<uint>();
            foreach ((_, List<TextureBatchData> batches) in mesh!.TextureBatches)
            {
                foreach (TextureBatchData batch in batches)
                {
                    if (batch.Indices.Count == 0)
                        continue;
                    Assert.False(
                        batch.Key.IsSolid,
                        $"cell 0x{cellId:X8} surface 0x{batch.Key.SurfaceId:X8} fell back to a solid colour.");
                    Assert.NotNull(batch.TextureData);
                    Assert.NotEmpty(batch.TextureData!);
                    Assert.False(
                        IsUniform(batch.TextureData!),
                        $"cell 0x{cellId:X8} surface 0x{batch.Key.SurfaceId:X8} decoded to one flat value.");
                    drawnSurfaces.Add(batch.Key.SurfaceId);
                }
            }

            Assert.NotEmpty(drawnSurfaces);
            _output.WriteLine(
                $"cell 0x{cellId:X8}: {drawnSurfaces.Count} textured subset(s) of "
                + $"{authoredImageSurfaces.Count} authored image surface(s)");
            Assert.Subset(authoredImageSurfaces, drawnSurfaces);
        }
    }

    [InstalledDatFact]
    public void TheLandblockTheReportedSpotStandsInStaysInsideTheIndoorRenderWindow()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        EnvCellLandblockBuild build = BuildLandblock(dats);
        Assert.NotEmpty(build.Shells);

        var bounds = new WbBoundingBox(new Vector3(float.MaxValue), new Vector3(float.MinValue));
        foreach (EnvCellShellPlacement shell in build.Shells)
            bounds = WbBoundingBox.Union(bounds, shell.WorldBounds);
        _output.WriteLine(
            $"landblock 0x{LandblockId:X8}: {build.Shells.Length} shell(s), "
            + $"bounds {bounds.Min}..{bounds.Max}, camera {ReportedCamera}");

        // The cells run far enough from the landblock origin that the camera
        // sits several block widths outside it.
        int cameraBlockY = (int)MathF.Floor(ReportedCamera.Y / EnvCellRenderWindow.LandblockSize);
        Assert.True(
            Math.Abs(cameraBlockY) > NearRadius,
            "This dungeon no longer reaches outside its own landblock; the regression this pins is gone.");

        Assert.True(
            EnvCellRenderWindow.Intersects(ReportedCamera, NearRadius, bounds),
            "The landblock holding the reported spot must stay inside the indoor render window.");

        foreach (uint cellId in ReportedCells)
        {
            EnvCellShellPlacement shell = Assert.Single(
                build.Shells.Where(candidate => candidate.CellId == cellId));
            Assert.True(
                EnvCellRenderWindow.Intersects(ReportedCamera, NearRadius, shell.WorldBounds),
                $"cell 0x{cellId:X8} must stay inside the indoor render window.");
        }
    }

    private static EnvCellLandblockBuild BuildLandblock(DatCollection dats)
    {
        var lbInfo = dats.Get<LandBlockInfo>((LandblockId & 0xFFFF0000u) | 0xFFFEu);
        Assert.NotNull(lbInfo);
        Assert.NotEqual(0u, lbInfo!.NumCells);

        var builder = new EnvCellLandblockBuildBuilder(LandblockId);
        var reader = new DatCollectionAdapter(dats);
        uint firstCellId = (LandblockId & 0xFFFF0000u) | 0x0100u;
        for (uint offset = 0; offset < lbInfo.NumCells; offset++)
        {
            uint envCellId = firstCellId + offset;
            var envCell = dats.Get<EnvCell>(envCellId);
            if (envCell is null || envCell.EnvironmentId == 0)
                continue;
            var environment = dats.Get<DatEnvironment>(0x0D000000u | envCell.EnvironmentId);
            if (environment is null
                || !environment.Cells.TryGetValue(envCell.CellStructure, out var cellStruct))
            {
                continue;
            }

            // Build in the frame of this landblock: the render origin is here.
            Vector3 cellOrigin = envCell.Position.Origin;
            Matrix4x4 cellTransform =
                Matrix4x4.CreateFromQuaternion(envCell.Position.Orientation)
                * Matrix4x4.CreateTranslation(cellOrigin);
            builder.AddCell(
                envCellId,
                envCell,
                cellStruct,
                cellOrigin,
                cellTransform,
                cellOrigin,
                cellTransform,
                hasDrawableGeometry: CellMesh.HasDrawableGeometry(envCell, cellStruct, reader));
        }

        return builder.Build();
    }

    private static bool IsUniform(byte[] data)
    {
        for (int i = 1; i < data.Length; i++)
        {
            if (data[i] != data[0])
                return false;
        }
        return true;
    }
}
