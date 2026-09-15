using System.Collections.Immutable;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu.Vk;
using AcDream.App.Rendering.Wb;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.Content;
using AcDream.Core.World;
using Microsoft.Extensions.Logging.Abstractions;

namespace AcDream.App.Tests.Rendering.Wb;

/// <summary>
/// A dungeon may place its cells hundreds of metres from the origin of the
/// landblock they are filed under. The indoor render window has to follow the
/// cells, or the block a player is standing in is dropped from preparation and
/// its walls never reach a draw call.
/// </summary>
public sealed class EnvCellRendererDistantCellWindowTests
{
    private const uint LandblockId = 0x0019FFFFu;
    private const uint CellId = 0x001901D0u;

    /// <summary>Five landblocks south of the origin of the block that files it.</summary>
    private static readonly Vector3 CellPosition = new(78f, -790f, 12f);

    private const int NearRadius = 4;

    [Fact]
    public void CellsFarFromTheirLandblockOriginArePreparedForTheCameraStandingInThem()
    {
        using var device = new RecordingGpuDevice();
        using var meshManager = CreateMeshManager(device);
        var frameLifetime = new GpuDeviceFrameLifetime(device);
        var frustum = new WbFrustum();
        frustum.Update(EverythingInView);
        using var renderer = new EnvCellRenderer(
            device,
            frameLifetime,
            new VulkanWorldPassScope(sampleCount: 1),
            meshManager,
            frustum);

        renderer.CommitLandblock(BuildOneDistantShell());

        Vector3 camera = CellPosition with { Z = 12.004999f };
        renderer.PrepareRenderBatches(
            EverythingInView,
            camera,
            filter: null,
            centerLbX: 0,
            centerLbY: LandblockGridY(camera.Y),
            renderRadius: NearRadius);
        renderer.Render(WbRenderPass.Opaque);

        Assert.Equal(1, renderer.Stats.CellsRendered);
    }

    [Fact]
    public void CellsBeyondTheWindowAreStillDropped()
    {
        using var device = new RecordingGpuDevice();
        using var meshManager = CreateMeshManager(device);
        var frameLifetime = new GpuDeviceFrameLifetime(device);
        var frustum = new WbFrustum();
        frustum.Update(EverythingInView);
        using var renderer = new EnvCellRenderer(
            device,
            frameLifetime,
            new VulkanWorldPassScope(sampleCount: 1),
            meshManager,
            frustum);

        renderer.CommitLandblock(BuildOneDistantShell());

        // Ten landblocks away from the cells, which is outside a radius of four.
        var camera = new Vector3(78f, -790f + (10f * EnvCellRenderWindow.LandblockSize), 12f);
        renderer.PrepareRenderBatches(
            EverythingInView,
            camera,
            filter: null,
            centerLbX: 0,
            centerLbY: LandblockGridY(camera.Y),
            renderRadius: NearRadius);
        renderer.Render(WbRenderPass.Opaque);

        Assert.Equal(0, renderer.Stats.CellsRendered);
    }

    private static ObjectMeshManager CreateMeshManager(RecordingGpuDevice device) =>
        new(
            new VulkanMeshPipelineDevice(device.Retirement),
            device,
            new NullPreparedAssetSource(),
            NullLogger<ObjectMeshManager>.Instance);

    private sealed class NullPreparedAssetSource : IPreparedAssetSource
    {
        public PreparedAssetSourceStats Stats => default;

        public CacheStats DecodedTextureCacheStats => default;

        public PreparedAssetPresence Probe(
            AcDream.Content.Pak.PakAssetType type,
            uint sourceFileId) =>
            PreparedAssetPresence.Missing;

        public PreparedAssetReadResult Read(
            in PreparedAssetRequest request,
            CancellationToken cancellationToken = default) =>
            PreparedAssetReadResult.Missing;

        public void Dispose()
        {
        }
    }

    private static int LandblockGridY(float worldY) =>
        (int)MathF.Floor(worldY / EnvCellRenderWindow.LandblockSize);

    private static Matrix4x4 EverythingInView =>
        Matrix4x4.CreateOrthographicOffCenter(-9000f, 9000f, -9000f, 9000f, -9000f, 9000f);

    private static EnvCellLandblockBuild BuildOneDistantShell()
    {
        var localBounds = new WbBoundingBox(new Vector3(-4f, -4f, 0f), new Vector3(4f, 4f, 5f));
        Matrix4x4 transform = Matrix4x4.CreateTranslation(CellPosition);
        var worldBounds = new WbBoundingBox(
            CellPosition + localBounds.Min,
            CellPosition + localBounds.Max);
        var shell = new EnvCellShellPlacement(
            CellId,
            GeometryId: 0xE6A3528BEA7C5EFCuL,
            EnvironmentId: 0x192u,
            CellStructure: 0,
            Surfaces: ImmutableArray.Create<ushort>(0x02DD, 0x02DB, 0x02DC, 0x0034),
            WorldPosition: CellPosition,
            Rotation: Quaternion.Identity,
            Transform: transform,
            LocalBounds: localBounds,
            WorldBounds: worldBounds);
        return new EnvCellLandblockBuild(
            LandblockId,
            Array.Empty<LoadedCell>(),
            new[] { shell });
    }
}
