using FarmEngine.Rendering;
using SkiaSharp;

namespace FarmingRpgMaker.App.Tests.Rendering;

/// <summary>Draws small scenes to an <see cref="SKBitmap"/> and checks pixels at known spots.</summary>
public sealed class SkiaWorldRendererTests
{
    private const int Ts = 32;

    private static WorldSnapshot Scene(params string[][] rows)
    {
        var snapshot = new WorldSnapshot
        {
            Width = rows[0].Length,
            Height = rows.Length,
            TileSize = Ts,
            Padding = 0,
            TileGap = 0,
            Player = new SnapshotPlayer { X = -10, Y = -10, Direction = "down" },
        };
        foreach (var row in rows)
        {
            snapshot.Tiles.Add(row.Select(type => new SnapshotTile { Background = type }).ToList());
        }

        return snapshot;
    }

    private static SKColor Pixel(SKBitmap bitmap, double x, double y) => bitmap.GetPixel((int)x, (int)y);

    private static void AssertColor(string expectedCss, SKColor actual, int tolerance = 3)
    {
        var expected = CssColor.Parse(expectedCss);
        Assert.True(
            Math.Abs(expected.Red - actual.Red) <= tolerance && Math.Abs(expected.Green - actual.Green) <= tolerance && Math.Abs(expected.Blue - actual.Blue) <= tolerance,
            $"expected {expected} got {actual}");
    }

    [Fact]
    public void DrawsTileBackgroundsInTheirPaletteColors()
    {
        using var renderer = new SkiaWorldRenderer();
        using var bitmap = renderer.RenderToBitmap(Scene(["grass", "water", "soil"], ["path", "wall", "unknown-type"]));

        Assert.Equal(3 * Ts, bitmap.Width);
        Assert.Equal(2 * Ts, bitmap.Height);
        AssertColor("#5b9a4a", Pixel(bitmap, 16, 16));
        AssertColor("#3075b0", Pixel(bitmap, 48, 16));
        AssertColor("#7a6545", Pixel(bitmap, 80, 16));
        AssertColor("#b5a48d", Pixel(bitmap, 16, 48));
        AssertColor("#5e5a68", Pixel(bitmap, 48, 48));
        AssertColor("#5b9a4a", Pixel(bitmap, 80, 48)); // unknown types fall back to grass
    }

    [Fact]
    public void DrawsOverlayAt70PercentAndObjectsOpaque()
    {
        var snapshot = Scene(["grass", "grass"]);
        snapshot.Tiles[0][0].Overlay = "path";
        snapshot.Tiles[0][1].Object = "wall";
        using var renderer = new SkiaWorldRenderer();
        using var bitmap = renderer.RenderToBitmap(snapshot);

        // path over grass at alpha 0.7: 0.7*path + 0.3*grass (on top of the faint tint).
        var grass = CssColor.Parse("#5b9a4a");
        var path = CssColor.Parse("#b5a48d");
        var blended = new SKColor((byte)Math.Round((0.7 * path.Red) + (0.3 * grass.Red)), (byte)Math.Round((0.7 * path.Green) + (0.3 * grass.Green)), (byte)Math.Round((0.7 * path.Blue) + (0.3 * grass.Blue)));
        AssertColor(blended.ToString().Replace("#ff", "#", StringComparison.Ordinal), Pixel(bitmap, 16, 16), 4);
        AssertColor("#5e5a68", Pixel(bitmap, 48, 16));
    }

    [Fact]
    public void DrawsCropStagesWitheredCropsNodesAndItems()
    {
        var snapshot = Scene(["soil", "soil", "grass", "grass"]);
        snapshot.Tiles[0][0].Crop = new SnapshotCrop { ColorIndex = 1 };
        snapshot.Tiles[0][1].Crop = new SnapshotCrop { ColorIndex = 9, Withered = true };
        snapshot.Tiles[0][2].Node = new SnapshotNode { Color = "#aa0000" };
        snapshot.Tiles[0][3].Item = new SnapshotItem();
        using var renderer = new SkiaWorldRenderer();
        using var bitmap = renderer.RenderToBitmap(snapshot);

        AssertColor(Canvas2d.CropStageColors[1], Pixel(bitmap, 16, 16));
        AssertColor(Canvas2d.WitheredCropColor, Pixel(bitmap, 48, 16));
        AssertColor("#aa0000", Pixel(bitmap, 80, 16));
        AssertColor(Canvas2d.ItemColor, Pixel(bitmap, 112, 16));
        // Crop squares are 35% of the tile: the tile corner stays soil.
        AssertColor("#7a6545", Pixel(bitmap, 3, 3));
    }

    [Fact]
    public void DrawsNpcsAndThePlayerWithAFacingDot()
    {
        var snapshot = Scene(["grass", "grass", "grass"], ["grass", "grass", "grass"]);
        snapshot.Npcs.Add(new SnapshotEntity { X = 0, Y = 0 });
        snapshot.Player = new SnapshotPlayer { X = 2.5, Y = 1.5, Direction = "up" };
        using var renderer = new SkiaWorldRenderer();
        using var bitmap = renderer.RenderToBitmap(snapshot);

        AssertColor(Canvas2d.NpcColor, Pixel(bitmap, 16, 16));
        AssertColor(Canvas2d.PlayerColor, Pixel(bitmap, (2 * Ts) + 16, Ts + 16));
        // Facing "up": the white dot sits near the top of the player body, not the bottom.
        var dotTop = Pixel(bitmap, (2 * Ts) + 16, Ts + 5);
        var dotBottom = Pixel(bitmap, (2 * Ts) + 16, Ts + 24);
        Assert.True(dotTop.Red > 200 && dotTop.Green > 200, $"expected the facing dot at the top, got {dotTop}");
        AssertColor(Canvas2d.PlayerColor, dotBottom);
    }

    [Fact]
    public void CameraTranslatesAndCullsTheWorld()
    {
        var snapshot = Scene(["water", "grass", "soil"]);
        snapshot.Camera = new SnapshotCamera(Ts, 0, Ts, Ts);
        using var renderer = new SkiaWorldRenderer();
        using var bitmap = renderer.RenderToBitmap(snapshot);

        Assert.Equal(Ts, bitmap.Width);
        AssertColor("#5b9a4a", Pixel(bitmap, 16, 16)); // the middle (grass) tile fills the viewport
    }

    [Fact]
    public void PixelArtUsesNearestNeighbourSampling()
    {
        // 2×1 image: red | blue, drawn over a 32px tile → a hard edge at x = 16.
        using var source = new SKBitmap(2, 1);
        source.SetPixel(0, 0, SKColors.Red);
        source.SetPixel(1, 0, SKColors.Blue);
        using var image = SKImage.FromBitmap(source);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var dataUrl = "data:image/png;base64," + Convert.ToBase64String(png.ToArray());

        var snapshot = Scene(["grass"]);
        snapshot.Tiles[0][0].ImageUrl = dataUrl;
        snapshot.PixelArt = true;
        using var renderer = new SkiaWorldRenderer();
        using var crisp = renderer.RenderToBitmap(snapshot);
        AssertColor("#ff0000", Pixel(crisp, 15, 16));
        AssertColor("#0000ff", Pixel(crisp, 16, 16));

        snapshot.PixelArt = false;
        using var smooth = renderer.RenderToBitmap(snapshot);
        var mid = Pixel(smooth, 15, 16);
        Assert.True(mid.Red < 250 && mid.Blue > 5, $"smooth sampling should blend at the seam, got {mid}");
        Assert.Equal(1, renderer.Images.Count);
    }

    [Fact]
    public void SpriteFramesAreCroppedFromTheSheet()
    {
        using var source = new SKBitmap(2, 1);
        source.SetPixel(0, 0, SKColors.Red);
        source.SetPixel(1, 0, SKColors.Lime);
        using var image = SKImage.FromBitmap(source);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var dataUrl = "data:image/png;base64," + Convert.ToBase64String(png.ToArray());

        var snapshot = Scene(["water"]);
        snapshot.Tiles[0][0].ArtLayers = [new SnapshotSprite { ImageUrl = dataUrl, FrameWidth = 1, FrameHeight = 1, Frame = 1, Row = 0 }, null, null];
        using var renderer = new SkiaWorldRenderer();
        using var bitmap = renderer.RenderToBitmap(snapshot);
        AssertColor("#00ff00", Pixel(bitmap, 16, 16));

        // Out-of-bounds frames fall back to the tile color.
        snapshot.Tiles[0][0].ArtLayers = [new SnapshotSprite { ImageUrl = dataUrl, FrameWidth = 1, FrameHeight = 1, Frame = 5, Row = 0 }, null, null];
        using var fallback = renderer.RenderToBitmap(snapshot);
        AssertColor("#3075b0", Pixel(fallback, 16, 16));
    }

    [Fact]
    public void EditGridUsesOnePixelSeams()
    {
        var snapshot = Scene(["water", "water"]);
        snapshot.TileGap = 1;
        snapshot.GridOverlay = true;
        using var renderer = new SkiaWorldRenderer();
        using var bitmap = renderer.RenderToBitmap(snapshot);

        Assert.Equal((2 * Ts) + 1, bitmap.Width);
        Assert.True(Pixel(bitmap, Ts, 16).Alpha < 40, "the seam column stays (nearly) transparent");
        AssertColor("#3075b0", Pixel(bitmap, Ts + 1 + 16, 16));
    }
}
