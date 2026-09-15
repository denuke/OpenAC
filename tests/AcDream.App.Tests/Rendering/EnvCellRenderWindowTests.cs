using System.Numerics;
using AcDream.App.Rendering.Wb;

namespace AcDream.App.Tests.Rendering;

/// <summary>
/// The indoor render window follows the geometry, not the landblock id it is
/// filed under. See the Gold Legion Keep report: its cells sit roughly five
/// landblocks south of their own block origin, so a window measured on the grid
/// index dropped the cells the player was standing in.
/// </summary>
public sealed class EnvCellRenderWindowTests
{
    private const float Block = EnvCellRenderWindow.LandblockSize;

    /// <summary>The window test as it was before the fix: the block index of the landblock's own origin.</summary>
    private static bool GridIndexWindow(Vector3 camera, int radius, int landblockGridX, int landblockGridY, int originCenterX, int originCenterY)
    {
        int centerX = originCenterX + (int)MathF.Floor(camera.X / Block);
        int centerY = originCenterY + (int)MathF.Floor(camera.Y / Block);
        return Math.Abs(landblockGridX - centerX) <= radius
            && Math.Abs(landblockGridY - centerY) <= radius;
    }

    [Fact]
    public void AxisWindowSpansTheCentreBlockAndRadiusBlocksEitherSide()
    {
        (float min, float max) = EnvCellRenderWindow.Axis(100f, 2);
        Assert.Equal(-2f * Block, min);
        Assert.Equal(3f * Block, max);

        (float negativeMin, float negativeMax) = EnvCellRenderWindow.Axis(-100f, 2);
        Assert.Equal(-3f * Block, negativeMin);
        Assert.Equal(2f * Block, negativeMax);
    }

    [Fact]
    public void BlockSizedBoundsMatchTheBlockIndexWindowTheyReplaced()
    {
        var camera = new Vector3(100f, 100f, 10f);
        const int radius = 1;
        for (int blockX = -4; blockX <= 4; blockX++)
        {
            for (int blockY = -4; blockY <= 4; blockY++)
            {
                var bounds = new WbBoundingBox(
                    new Vector3(blockX * Block, blockY * Block, -10f),
                    new Vector3((blockX + 1) * Block, (blockY + 1) * Block, 10f));
                bool expected = GridIndexWindow(camera, radius, blockX, blockY, 0, 0);
                Assert.Equal(
                    expected,
                    EnvCellRenderWindow.Intersects(camera, radius, bounds));
            }
        }
    }

    [Fact]
    public void CellsPlacedFarOutsideTheirOwnLandblockStayInTheWindow()
    {
        // The reported spot: the player stands roughly 790 m south of the block
        // origin its cells are filed under, with the cells spread over that run.
        var camera = new Vector3(78.81f, -791.77f, 12.0f);
        var bounds = new WbBoundingBox(
            new Vector3(-5f, -1015f, 0f),
            new Vector3(185f, 5f, 34f));
        const int radius = 4;
        const int landblockGridX = 0x00;
        const int landblockGridY = 0x19;

        // The block-index window drops it: the camera is five blocks south of
        // the landblock's own origin.
        Assert.False(GridIndexWindow(camera, radius, landblockGridX, landblockGridY, 0, landblockGridY));

        Assert.True(EnvCellRenderWindow.Intersects(camera, radius, bounds));
    }

    [Fact]
    public void BoundsBeyondTheWindowAreStillExcluded()
    {
        var camera = new Vector3(78.81f, -791.77f, 12.0f);
        var far = new WbBoundingBox(
            new Vector3(-5f, 2000f, 0f),
            new Vector3(185f, 2100f, 34f));
        Assert.False(EnvCellRenderWindow.Intersects(camera, 4, far));

        var farX = new WbBoundingBox(
            new Vector3(4000f, -1015f, 0f),
            new Vector3(4100f, 5f, 34f));
        Assert.False(EnvCellRenderWindow.Intersects(camera, 4, farX));
    }
}
