using System.Numerics;
using System.Reflection;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Wb;
using AcDream.Core.Meshing;
using AcDream.Core.World;
using DatReaderWriter.Enums;

namespace AcDream.App.Tests.Rendering.Walk;

public sealed partial class WalkStaticStreamPopulatorTests
{
    private sealed class FixedCamera : ICamera
    {
        public Matrix4x4 View => Matrix4x4.Identity;
        public Matrix4x4 Projection => Matrix4x4.Identity;
        public float Aspect { get; set; } = 1f;
    }

    private const uint PartLitBlock = 0x8C040000u;
    private const uint PartLitMesh = 0x01000E99u;
    private const uint PartLitEntity = 0x00ABCDEFu;
    private const uint PartLitGuid = 0x50ABCDEFu;

    private static IReadOnlyList<(Vector2 Lighting, int Order)> DrawTwoPartEntity(
        DispatcherFixture fx)
    {
        var entity = new WorldEntity
        {
            Id = PartLitEntity,
            ServerGuid = PartLitGuid,
            SourceGfxObjOrSetupId = PartLitMesh,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs =
            [
                new MeshRef(PartLitMesh, Matrix4x4.Identity),   // part 0
                new MeshRef(PartLitMesh, Matrix4x4.Identity),   // part 1
            ],
            ParentCellId = null,
        };

        fx.Dispatcher.BeginFrame(frameSlot: 0);
        using DrawScope scope = fx.BeginDraw();
        fx.Dispatcher.Draw(
            new FixedCamera(),
            [(PartLitBlock, new Vector3(-1000f), new Vector3(1000f),
              (IReadOnlyList<WorldEntity>)[entity], null)],
            frustum: null,
            neverCullLandblockId: PartLitBlock);

        FieldInfo field = typeof(WbDrawDispatcher).GetField(
            "_groups", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "WbDrawDispatcher._groups not found - this test relies on that exact name.");
        var groups = (System.Collections.IDictionary)field.GetValue(fx.Dispatcher)!;

        var instances = new List<(Vector2, int)>();
        foreach (object? value in groups.Values)
        {
            var group = (WbDrawDispatcher.InstanceGroup)value!;
            for (int i = 0; i < group.SelectionLighting.Count; i++)
                instances.Add((group.SelectionLighting[i], group.SubmissionOrders[i]));
        }
        instances.Sort(static (a, b) => a.Item2.CompareTo(b.Item2));
        return instances;
    }

    [Fact]
    public void APartFlash_reachesOnlyTheLitPartsInstances()
    {
        var lighting = new RetainedLighting
        {
            Value = new(0f, 1f),                // nothing picked in the world
            PartLitEntityId = PartLitEntity,
            PartLitServerGuid = PartLitGuid,
            PartMask = 1u << 1,                 // part 1 only
            PartValue = new(0.99f, 1f),
        };
        using var fx = new DispatcherFixture(selectionSink: lighting);
        InjectRenderData(
            fx.Manager,
            PartLitMesh,
            MakeFlatMesh(MakeBatch(PartLitMesh, TranslucencyKind.Opaque, 3, 0, 3, 1)));

        IReadOnlyList<(Vector2 Lighting, int Order)> instances = DrawTwoPartEntity(fx);

        Assert.Equal(2, instances.Count);
        // Part 0 is dark and part 1 carries the flash: the value is chosen per
        // part, not once for the whole entity.
        Assert.Equal(new Vector2(0f, 1f), instances[0].Lighting);
        Assert.Equal(new Vector2(0.99f, 1f), instances[1].Lighting);
    }

    [Fact]
    public void WithNothingFlashing_everyPartCarriesTheEntityValue()
    {
        var lighting = new RetainedLighting
        {
            Value = new(0.25f, 0.75f),          // a whole-entity pick
            PartMask = 0u,                      // no part flash in flight
        };
        using var fx = new DispatcherFixture(selectionSink: lighting);
        InjectRenderData(
            fx.Manager,
            PartLitMesh,
            MakeFlatMesh(MakeBatch(PartLitMesh, TranslucencyKind.Opaque, 3, 0, 3, 1)));

        IReadOnlyList<(Vector2 Lighting, int Order)> instances = DrawTwoPartEntity(fx);

        Assert.Equal(2, instances.Count);
        Assert.All(instances, i => Assert.Equal(new Vector2(0.25f, 0.75f), i.Lighting));
    }
}
