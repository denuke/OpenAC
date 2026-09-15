using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Numerics;
using System.Reflection;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;
using AcDream.App.Rendering.Wb;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.Content;
using AcDream.Core.World;
using Microsoft.Extensions.Logging.Abstractions;
using CullMode = DatReaderWriter.Enums.CullMode;

namespace AcDream.App.Tests.Rendering.Wb;

/// <summary>
/// A cell subset that reaches the draw list with no texture binding carries the
/// unassigned sentinel, not a blank texture, so drawing it would be an
/// out-of-range descriptor read. It has to be dropped, and said out loud.
/// </summary>
public sealed class EnvCellUnboundSurfaceGuardTests
{
    private const uint LandblockId = 0x0019FFFFu;
    private const uint CellId = 0x001901D0u;
    private const ulong GeometryId = 0xE6A3528BEA7C5EFCuL;
    private const uint UnboundSurfaceId = 0x080002DBu;

    private static readonly Vector3 CellPosition = new(78f, -790f, 12f);

    [Fact]
    public void ASubsetWithNoTextureBindingIsDroppedAndReportedOncePerSurface()
    {
        using var device = new RecordingGpuDevice();
        using ObjectMeshManager meshManager = BuildModernMeshManager(device);
        var frameLifetime = new GpuDeviceFrameLifetime(device);
        var frustum = new WbFrustum();
        frustum.Update(EverythingInView);
        using var renderer = new EnvCellRenderer(
            device,
            frameLifetime,
            new VulkanWorldPassScope(sampleCount: 1),
            meshManager,
            frustum);

        PublishRenderData(meshManager, GeometryId, UnboundBatch());
        renderer.CommitLandblock(BuildOneShell());

        Assert.Equal(0, renderer.UnresolvedSurfaceReportCount);

        for (int pass = 0; pass < 3; pass++)
        {
            renderer.PrepareRenderBatches(
                EverythingInView,
                CellPosition with { Z = CellPosition.Z + pass },
                filter: null,
                centerLbX: 0,
                centerLbY: 0,
                renderRadius: 4);
            renderer.Render(WbRenderPass.Opaque);
        }

        // The landblock was prepared - the cell is in the window and in view.
        Assert.Equal(1, renderer.Stats.CellsRendered);
        // The unbound subset never reached a draw, and said so exactly once.
        Assert.Equal(1, renderer.UnresolvedSurfaceReportCount);
        Assert.Empty(device.Calls.OfType<GpuRecordedMultiDrawIndirect>());
    }

    private static ObjectRenderBatch UnboundBatch() => new()
    {
        IndexCount = 6,
        TextureIndex = 0,
        TextureSize = (256, 512),
        TextureFormat = Chorizite.Core.Render.Enums.TextureFormat.DXT1,
        Key = new TextureKey { SurfaceId = UnboundSurfaceId },
        CullMode = CullMode.Clockwise,
        // TextureSlot is left at its default, which is GpuTextureSlot.Unassigned.
    };

    private static void PublishRenderData(
        ObjectMeshManager meshManager,
        ulong id,
        ObjectRenderBatch batch)
    {
        var data = new ObjectRenderData
        {
            VertexCount = 4,
            Batches = new List<ObjectRenderBatch> { batch },
            CPUPositions = new Vector3[4],
            CPUIndices = new ushort[6],
        };
        FieldInfo field = typeof(ObjectMeshManager).GetField(
            "_renderData",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var map = (ConcurrentDictionary<ulong, ObjectRenderData>)field.GetValue(meshManager)!;
        map[id] = data;
    }

    private static ObjectMeshManager BuildModernMeshManager(RecordingGpuDevice device) =>
        new(
            new ModernMeshPipelineDevice(device.Retirement),
            device,
            new NullPreparedAssetSource(),
            NullLogger<ObjectMeshManager>.Instance);

    private static Matrix4x4 EverythingInView =>
        Matrix4x4.CreateOrthographicOffCenter(-9000f, 9000f, -9000f, 9000f, -9000f, 9000f);

    private static EnvCellLandblockBuild BuildOneShell()
    {
        var localBounds = new WbBoundingBox(new Vector3(-4f, -4f, 0f), new Vector3(4f, 4f, 5f));
        var shell = new EnvCellShellPlacement(
            CellId,
            GeometryId,
            EnvironmentId: 0x192u,
            CellStructure: 0,
            Surfaces: ImmutableArray.Create<ushort>(0x02DB),
            WorldPosition: CellPosition,
            Rotation: Quaternion.Identity,
            Transform: Matrix4x4.CreateTranslation(CellPosition),
            LocalBounds: localBounds,
            WorldBounds: new WbBoundingBox(
                CellPosition + localBounds.Min,
                CellPosition + localBounds.Max));
        return new EnvCellLandblockBuild(
            LandblockId,
            Array.Empty<LoadedCell>(),
            new[] { shell });
    }

    /// <summary>A mesh-pipeline device with the modern arena and no GL behind it.</summary>
    private sealed class ModernMeshPipelineDevice(IGpuResourceRetirementQueue retirement)
        : IMeshPipelineDevice
    {
        public IGpuResourceRetirementQueue ResourceRetirement { get; } = retirement;

        public uint InstanceVBO => 0;

        public bool HasBindless => true;

        public bool HasOpenGL43 => true;

        public bool HasPendingWork => false;

        public void ProcessQueue()
        {
        }

        public void Dispose()
        {
        }
    }

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
}
