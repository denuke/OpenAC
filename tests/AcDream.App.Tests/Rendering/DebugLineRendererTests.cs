using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;

namespace AcDream.App.Tests.Rendering;

public sealed class DebugLineRendererTests
{
    [Fact]
    public void LinesDrawIntoTheOpenWorldPassInsteadOfBeginningAnother()
    {
        var device = new RecordingGpuDevice();
        IGpuFrame frame = device.BeginFrame();
        var scope = new OpenableWorldPass(sampleCount: 4);
        using var lines = new DebugLineRenderer(device, new FixedFrame(frame), "shaders", scope);
        using IGpuPassEncoder world = frame.BeginPass(WorldPass(sampleCount: 4));
        using IDisposable published = scope.Publish(world);

        lines.Begin();
        lines.AddLine(Vector3.Zero, Vector3.UnitX, new Vector3(1f, 0f, 1f));
        lines.Flush(Matrix4x4.Identity, Matrix4x4.Identity);

        Assert.Equal(["vk-world"], device.Calls.OfType<GpuRecordedPassBegin>().Select(pass => pass.Name));
        Assert.Contains(new GpuRecordedPipelineBind("debug-line-world"), device.Calls);
        Assert.Contains(new GpuRecordedDraw(2, 1, 0, 0), device.Calls);
    }

    [Fact]
    public void WithNoWorldPassOpenLinesDrawInAPassOfTheirOwn()
    {
        var device = new RecordingGpuDevice();
        IGpuFrame frame = device.BeginFrame();
        using var lines = new DebugLineRenderer(device, new FixedFrame(frame), "shaders", new OpenableWorldPass(sampleCount: 4));

        lines.Begin();
        lines.AddLine(Vector3.Zero, Vector3.UnitY, new Vector3(1f, 0f, 1f));
        lines.Flush(Matrix4x4.Identity, Matrix4x4.Identity);

        Assert.Equal([new GpuRecordedPassBegin("debug-line", 1)], device.Calls.OfType<GpuRecordedPassBegin>());
        Assert.Contains(new GpuRecordedPipelineBind("debug-line"), device.Calls);
        Assert.Contains(new GpuRecordedDraw(2, 1, 0, 0), device.Calls);
    }

    private static GpuPassDescription WorldPass(int sampleCount) => new()
    {
        Name = "vk-world",
        Color = new GpuColorAttachment(
            Target: null,
            Load: GpuLoadOp.Clear,
            Store: GpuStoreOp.Store,
            ClearColor: default),
        Depth = new GpuDepthAttachment(
            Load: GpuLoadOp.Clear,
            Store: GpuStoreOp.DontCare,
            ClearDepth: 1f,
            ClearStencil: 0),
        SampleCount = sampleCount,
    };

    private sealed class FixedFrame(IGpuFrame frame) : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => frame;
    }

    private sealed class OpenableWorldPass(int sampleCount) : IWorldPassScope
    {
        private IGpuPassEncoder? _encoder;

        public int SampleCount => sampleCount;

        public IGpuPassEncoder? CurrentEncoder => _encoder;

        public int AttachmentWidth => 1024;

        public int AttachmentHeight => 720;

        public WorldFrameSections Sections { get; } = new();

        public IGpuPassEncoder RequireEncoder() =>
            _encoder ?? throw new InvalidOperationException("No world pass is open.");

        public void ClearInteriorDepth()
        {
        }

        public IDisposable Publish(IGpuPassEncoder encoder)
        {
            _encoder = encoder;
            return new Publication(this);
        }

        private sealed class Publication(OpenableWorldPass owner) : IDisposable
        {
            public void Dispose() => owner._encoder = null;
        }
    }
}
