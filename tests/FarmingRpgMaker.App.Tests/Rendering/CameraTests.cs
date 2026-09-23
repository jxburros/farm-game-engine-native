using FarmEngine.Rendering;

namespace FarmingRpgMaker.App.Tests.Rendering;

/// <summary>Port of packages/renderer-canvas2d/src/camera.test.ts.</summary>
public sealed class CameraTests
{
    [Fact]
    public void ComputeCamera_CentersTheTargetInsideALargerWorld()
    {
        var cam = Canvas2d.ComputeCamera(500, 400, 2000, 1500, 480, 320);
        Assert.Equal(500 - 240, cam.X);
        Assert.Equal(400 - 160, cam.Y);
        Assert.Equal(480, cam.Width);
        Assert.Equal(320, cam.Height);
    }

    [Fact]
    public void ComputeCamera_ClampsToWorldEdges()
    {
        var topLeft = Canvas2d.ComputeCamera(10, 10, 2000, 1500, 480, 320);
        Assert.Equal((0d, 0d), (topLeft.X, topLeft.Y));
        var bottomRight = Canvas2d.ComputeCamera(1999, 1499, 2000, 1500, 480, 320);
        Assert.Equal((2000d - 480, 1500d - 320), (bottomRight.X, bottomRight.Y));
    }

    [Fact]
    public void ComputeCamera_CentersAWorldSmallerThanTheViewport()
    {
        var cam = Canvas2d.ComputeCamera(100, 50, 200, 100, 480, 320);
        Assert.Equal(-(480 - 200) / 2d, cam.X);
        Assert.Equal(-(320 - 100) / 2d, cam.Y);
    }

    [Fact]
    public void EntityPixelOrigin_TreatsIntegersAsTileIndices() =>
        Assert.Equal(8 + (3 * 32), Canvas2d.EntityPixelOrigin(3, 8, 32));

    [Fact]
    public void EntityPixelOrigin_TreatsFractionsAsBoxCenters()
    {
        Assert.Equal(8 + (3 * 32), Canvas2d.EntityPixelOrigin(3.5, 8, 32));
        Assert.Equal(8 + (2.75 * 32), Canvas2d.EntityPixelOrigin(3.25, 8, 32));
    }

    [Theory]
    [InlineData("#5b9a4a", 0x5b, 0x9a, 0x4a, 0xff)]
    [InlineData("#ffffff80", 0xff, 0xff, 0xff, 0x80)]
    [InlineData("#fff", 0xff, 0xff, 0xff, 0xff)]
    [InlineData("#0008", 0x00, 0x00, 0x00, 0x88)]
    [InlineData("rgba(10, 20, 30, 0.5)", 10, 20, 30, 128)]
    public void CssColor_ParsesCanvasColors(string css, byte r, byte g, byte b, byte a)
    {
        var color = CssColor.Parse(css);
        Assert.Equal((r, g, b, a), (color.Red, color.Green, color.Blue, color.Alpha));
    }

    [Fact]
    public void CssColor_FallsBackForGarbage() => Assert.Equal(CssColor.Fallback, CssColor.Parse("not-a-color"));
}
