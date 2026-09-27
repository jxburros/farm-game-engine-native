using FarmEngine.Content;
using FarmEngine.Core;
using FarmEngine.Rendering;
using SkiaSharp;

namespace FarmingRpgMaker.App.Tests.Rendering;

/// <summary>
/// Draws small scenes to an <see cref="SKBitmap"/> and checks pixels at known spots. The
/// color-block tests switch the built-in art off (<see cref="SkiaWorldRenderer.Art"/> = null)
/// to exercise the last-resort fallback path; the art tests keep it on.
/// </summary>
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

    /// <summary>A renderer drawing colored shapes only (no built-in art).</summary>
    private static SkiaWorldRenderer ColorBlocks() => new() { Art = null };

    private static SKColor Pixel(SKBitmap bitmap, double x, double y) => bitmap.GetPixel((int)x, (int)y);

    private static void AssertColor(string expectedCss, SKColor actual, int tolerance = 3)
    {
        var expected = CssColor.Parse(expectedCss);
        Assert.True(
            Math.Abs(expected.Red - actual.Red) <= tolerance && Math.Abs(expected.Green - actual.Green) <= tolerance && Math.Abs(expected.Blue - actual.Blue) <= tolerance,
            $"expected {expected} got {actual}");
    }

    private static int DistinctColors(SKBitmap bitmap, int x0 = 0, int y0 = 0, int? width = null, int? height = null)
    {
        var colors = new HashSet<SKColor>();
        for (var y = y0; y < y0 + (height ?? bitmap.Height); y++)
        {
            for (var x = x0; x < x0 + (width ?? bitmap.Width); x++)
            {
                colors.Add(bitmap.GetPixel(x, y));
            }
        }

        return colors.Count;
    }

    private static int Difference(SKColor a, SKColor b) =>
        Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue);

    private static string DataUrl(SKBitmap source)
    {
        using var image = SKImage.FromBitmap(source);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return "data:image/png;base64," + Convert.ToBase64String(png.ToArray());
    }

    [Fact]
    public void DrawsTileBackgroundsInTheirPaletteColors()
    {
        using var renderer = ColorBlocks();
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
        using var renderer = ColorBlocks();
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
        using var renderer = ColorBlocks();
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
        using var renderer = ColorBlocks();
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
        using var renderer = ColorBlocks();
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
        var dataUrl = DataUrl(source);

        var snapshot = Scene(["grass"]);
        snapshot.Tiles[0][0].ImageUrl = dataUrl;
        snapshot.PixelArt = true;
        using var renderer = ColorBlocks(); // only the creator image should enter the cache
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
        var dataUrl = DataUrl(source);

        var snapshot = Scene(["water"]);
        snapshot.Tiles[0][0].ArtLayers = [new SnapshotSprite { ImageUrl = dataUrl, FrameWidth = 1, FrameHeight = 1, Frame = 1, Row = 0 }, null, null];
        using var renderer = ColorBlocks();
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
        using var renderer = ColorBlocks();
        using var bitmap = renderer.RenderToBitmap(snapshot);

        Assert.Equal((2 * Ts) + 1, bitmap.Width);
        Assert.True(Pixel(bitmap, Ts, 16).Alpha < 40, "the seam column stays (nearly) transparent");
        AssertColor("#3075b0", Pixel(bitmap, Ts + 1 + 16, 16));
    }

    // ---- built-in art -----------------------------------------------------------------

    [Fact]
    public void StarterFarmRendersWithBuiltInArt()
    {
        var project = DefaultContent.CreateInitialProject(0);
        var content = EngineState.CreateContentFromProject(project);
        var state = EngineState.CreateGameState(project);
        var scene = state.World.Scenes.First(s => s.Id == state.Player.SceneId);
        var snapshot = ShellSnapshot.BuildShellSnapshot(content, state, scene, new ShellSnapshotOptions(Ts, 0));
        Assert.All(snapshot.Tiles.SelectMany(row => row), tile => Assert.Null(tile.ArtLayers));

        using var renderer = new SkiaWorldRenderer();
        Assert.NotNull(renderer.Art);
        using var bitmap = renderer.RenderToBitmap(snapshot);

        // Textured tiles, not flat fills: a single grass tile has several shades.
        Assert.True(DistinctColors(bitmap, Ts * 5, Ts * 1, Ts, Ts) >= 3, "grass should be textured");
        Assert.True(DistinctColors(bitmap) > 40, "the farm should not be a handful of flat colors");

        // The tree at (2, 2) is drawn from the pack: its trunk pixel differs from plain grass next to it.
        var treeTile = scene.Tiles.SelectMany(row => row).First(t => t.Node?.TypeId == "node-tree");
        var trunk = Pixel(bitmap, (treeTile.X * Ts) + 16, (treeTile.Y * Ts) + 29);
        var grass = Pixel(bitmap, ((treeTile.X + 2) * Ts) + 16, (treeTile.Y * Ts) + 29);
        Assert.True(Difference(trunk, grass) > 40, $"tree {trunk} should differ from grass {grass}");
        // …and its canopy overhangs the tile above (two tiles tall).
        var canopy = Pixel(bitmap, (treeTile.X * Ts) + 16, ((treeTile.Y - 1) * Ts) + 20);
        var plainGrass = Pixel(bitmap, ((treeTile.X + 2) * Ts) + 16, ((treeTile.Y - 1) * Ts) + 20);
        Assert.True(Difference(canopy, plainGrass) > 40, $"canopy {canopy} should cover the tile above, got grass {plainGrass}");

        // Walls, soil and the door come from the pack too (distinct from the flat palette fills).
        var wall = Pixel(bitmap, 16, 16);
        Assert.True(Difference(wall, CssColor.Parse(Canvas2d.TileColors["wall"])) > 10, "wall tiles should be textured stone");
    }

    [Fact]
    public void SoilStatesAndCropStagesChangeTheTile()
    {
        var snapshot = Scene(["soil", "soil", "soil", "soil"]);
        snapshot.Tiles[0][1].Watered = true;
        snapshot.Tiles[0][2].Fertilized = true;
        snapshot.Tiles[0][3].Crop = new SnapshotCrop { CropId = "wheat", ColorIndex = 3, Stages = 4, Mature = true };
        using var renderer = new SkiaWorldRenderer();
        using var bitmap = renderer.RenderToBitmap(snapshot);

        int Sum(int tile, Func<SKColor, int> channel)
        {
            var total = 0;
            for (var y = 0; y < Ts; y++)
            {
                for (var x = 0; x < Ts; x++)
                {
                    total += channel(Pixel(bitmap, (tile * Ts) + x, y));
                }
            }

            return total;
        }

        var dry = Sum(0, c => c.Red + c.Green + c.Blue);
        var wet = Sum(1, c => c.Red + c.Green + c.Blue);
        Assert.True(wet < dry * 0.85, $"watered soil should be darker: dry {dry} wet {wet}");

        // Fertilizer adds bright speckles; a ripe crop adds green/yellow over the brown.
        Assert.True(DistinctColors(bitmap, 2 * Ts, 0, Ts, Ts) > DistinctColors(bitmap, 0, 0, Ts, Ts), "fertilizer speckles add colors");
        var greenish = 0;
        for (var y = 0; y < Ts; y++)
        {
            for (var x = 0; x < Ts; x++)
            {
                var p = Pixel(bitmap, (3 * Ts) + x, y);
                if (p.Green > p.Red + 10 || (p.Red > 200 && p.Green > 180))
                {
                    greenish++;
                }
            }
        }

        Assert.True(greenish > 15, $"a ripe wheat plant should show green/yellow pixels, found {greenish}");
    }

    [Fact]
    public void CreatorArtWinsOverBuiltInArt()
    {
        using var source = new SKBitmap(1, 1);
        source.SetPixel(0, 0, SKColors.Magenta);
        var dataUrl = DataUrl(source);

        var snapshot = Scene(["grass", "grass"]);
        snapshot.Tiles[0][0].ArtLayers = [new SnapshotSprite { ImageUrl = dataUrl, FrameWidth = 1, FrameHeight = 1 }, null, null];
        snapshot.Tiles[0][1].Node = new SnapshotNode { TypeId = "node-rock", Sprite = new SnapshotSprite { ImageUrl = dataUrl, FrameWidth = 1, FrameHeight = 1 } };
        snapshot.Npcs.Add(new SnapshotEntity { X = 1, Y = 0, Appearance = "farmer", Sprite = new SnapshotSprite { ImageUrl = dataUrl, FrameWidth = 1, FrameHeight = 1 } });
        using var renderer = new SkiaWorldRenderer();
        using var bitmap = renderer.RenderToBitmap(snapshot);
        AssertColor("#ff00ff", Pixel(bitmap, 16, 16));
        AssertColor("#ff00ff", Pixel(bitmap, Ts + 16, 16));
    }

    [Fact]
    public void AtmosphereTintChangesPixelsBetweenNoonAndMidnight()
    {
        var snapshot = Scene(["grass", "grass"], ["grass", "grass"]);
        snapshot.Atmosphere = new SnapshotAtmosphere(12 * 60, "sun", "spring");
        using var renderer = new SkiaWorldRenderer();
        using var noon = renderer.RenderToBitmap(snapshot);
        snapshot.Atmosphere = new SnapshotAtmosphere(0, "sun", "spring");
        using var midnight = renderer.RenderToBitmap(snapshot);
        snapshot.Atmosphere = null;
        using var none = renderer.RenderToBitmap(snapshot);

        var day = Pixel(noon, 16, 16);
        var night = Pixel(midnight, 16, 16);
        Assert.Equal(Pixel(none, 16, 16), day); // midday: no tint
        Assert.True(night.Red < day.Red && night.Green < day.Green, $"night {night} should be darker than day {day}");
        Assert.True(night.Blue > night.Red, $"night should be cool blue, got {night}");
        // Dusk is warm: red stays high while blue drops.
        snapshot.Atmosphere = new SnapshotAtmosphere((18 * 60) + 40, "sun", "spring");
        using var dusk = renderer.RenderToBitmap(snapshot);
        var evening = Pixel(dusk, 16, 16);
        Assert.True(evening.Blue < day.Blue && evening.Red >= evening.Blue, $"dusk should be warm, got {evening}");
        Assert.Equal(SKColors.White, Atmosphere.DaylightTint(13 * 60));
    }

    [Fact]
    public void SeasonsAndWeatherChangeTheFrame()
    {
        var snapshot = Scene(["grass", "grass"], ["grass", "grass"]);
        snapshot.Tiles[1][1].Node = new SnapshotNode { TypeId = "node-tree" };
        using var renderer = new SkiaWorldRenderer();
        snapshot.Atmosphere = new SnapshotAtmosphere(12 * 60, "sun", "spring");
        using var spring = renderer.RenderToBitmap(snapshot);
        snapshot.Atmosphere = new SnapshotAtmosphere(12 * 60, "sun", "fall");
        using var fall = renderer.RenderToBitmap(snapshot);
        snapshot.Atmosphere = new SnapshotAtmosphere(12 * 60, "sun", "winter");
        using var winter = renderer.RenderToBitmap(snapshot);
        Assert.True(Difference(Pixel(spring, 16, 16), Pixel(fall, 16, 16)) > 30, "autumn grass should be ochre");
        Assert.True(Difference(Pixel(spring, 16, 16), Pixel(winter, 16, 16)) > 30, "winter grass should be frosty");

        // Rain streaks and snow flakes are deterministic: same tick ⇒ identical frames.
        snapshot.Atmosphere = new SnapshotAtmosphere(12 * 60, "rain", "spring");
        snapshot.Tick = 40;
        using var rainA = renderer.RenderToBitmap(snapshot);
        using var rainB = renderer.RenderToBitmap(snapshot);
        Assert.True(rainA.Bytes.AsSpan().SequenceEqual(rainB.Bytes), "weather must be a pure function of the tick");
        snapshot.Tick = 41;
        using var rainC = renderer.RenderToBitmap(snapshot);
        Assert.False(rainA.Bytes.AsSpan().SequenceEqual(rainC.Bytes), "rain should move between ticks");
        snapshot.Atmosphere = new SnapshotAtmosphere(12 * 60, "snow", "winter");
        using var snow = renderer.RenderToBitmap(snapshot);
        Assert.False(snow.Bytes.AsSpan().SequenceEqual(winter.Bytes), "snow flakes should be drawn");
    }

    [Fact]
    public void PlayerBehindATreeIsPartlyCoveredAndInFrontCoversIt()
    {
        // 1 column × 4 rows; the tree stands on row 2 and overhangs row 1.
        WorldSnapshot Make(double playerY)
        {
            var s = Scene(["grass"], ["grass"], ["grass"], ["grass"]);
            s.Tiles[2][0].Node = new SnapshotNode { TypeId = "node-tree" };
            s.Player = new SnapshotPlayer { X = 0.5, Y = playerY, Direction = "down" };
            return s;
        }

        var empty = Scene(["grass"], ["grass"], ["grass"], ["grass"]);
        empty.Player = new SnapshotPlayer { X = 0.5, Y = 1.5, Direction = "down" };
        using var renderer = new SkiaWorldRenderer();
        using var behind = renderer.RenderToBitmap(Make(1.5));
        using var noTree = renderer.RenderToBitmap(empty);
        using var treeOnly = renderer.RenderToBitmap(Make(-10));

        // Behind the tree (row 1): where the canopy overhangs, the frame matches the tree-only
        // frame (tree drawn over the player), yet the player's head still differs from an empty scene.
        int coveredByTree = 0, playerVisible = 0;
        for (var y = Ts; y < 2 * Ts; y++)
        {
            for (var x = 0; x < Ts; x++)
            {
                var canopy = treeOnly.GetPixel(x, y);
                var withPlayer = behind.GetPixel(x, y);
                var grassOnly = noTree.GetPixel(x, y);
                if (Difference(canopy, grassOnly) > 40 && withPlayer == canopy)
                {
                    coveredByTree++;
                }

                if (withPlayer != canopy)
                {
                    playerVisible++;
                }
            }
        }

        Assert.True(coveredByTree > 50, $"the canopy should cover part of the player ({coveredByTree} px)");
        Assert.True(playerVisible > 20, $"the player should still peek out ({playerVisible} px)");

        // In front of the tree (row 3): the player is drawn over the trunk.
        using var front = renderer.RenderToBitmap(Make(2.5));
        var overlapsTrunk = 0;
        for (var y = 2 * Ts; y < 3 * Ts; y++)
        {
            for (var x = 0; x < Ts; x++)
            {
                if (front.GetPixel(x, y) != treeOnly.GetPixel(x, y))
                {
                    overlapsTrunk++;
                }
            }
        }

        Assert.True(overlapsTrunk > 50, $"the player in front should cover the trunk ({overlapsTrunk} px)");
    }

    [Fact]
    public void WalkingPlayerAnimatesWithTheTickAndIdlesWhenStill()
    {
        var snapshot = Scene(["grass", "grass"], ["grass", "grass"]);
        snapshot.Player = new SnapshotPlayer { X = 1, Y = 1, Direction = "right", Moving = true };
        using var renderer = new SkiaWorldRenderer();
        var ticks = renderer.Art!.Entries["char-player"].TicksPerFrame!.Value;
        snapshot.Tick = ticks;
        using var frame1 = renderer.RenderToBitmap(snapshot);
        snapshot.Tick = ticks * 2;
        using var frame2 = renderer.RenderToBitmap(snapshot);
        Assert.False(frame1.Bytes.AsSpan().SequenceEqual(frame2.Bytes), "walk frames should differ");

        snapshot.Player.Moving = false;
        using var idleA = renderer.RenderToBitmap(snapshot);
        snapshot.Tick = ticks * 3;
        using var idleB = renderer.RenderToBitmap(snapshot);
        Assert.True(idleA.Bytes.AsSpan().SequenceEqual(idleB.Bytes), "standing still shows one idle frame");
    }

    [Fact]
    public void EditModeTilesAt28PixelsStayConsistent()
    {
        var project = DefaultContent.CreateInitialProject(0);
        var content = EngineState.CreateContentFromProject(project);
        var scene = project.Scenes[0];
        var snapshot = ShellSnapshot.BuildEditorSnapshot(project, content, scene);
        Assert.Null(snapshot.Atmosphere);
        using var renderer = new SkiaWorldRenderer();
        using var bitmap = renderer.RenderToBitmap(snapshot);
        var (w, h) = snapshot.WorldPixelSize();
        Assert.Equal(((int)Math.Ceiling(w), (int)Math.Ceiling(h)), (bitmap.Width, bitmap.Height));
        // Seams stay (nearly) transparent between textured tiles.
        Assert.True(bitmap.GetPixel((int)(snapshot.Padding + 28), (int)(snapshot.Padding + 14)).Alpha < 40);
        Assert.True(DistinctColors(bitmap, (int)snapshot.Padding + 29, (int)snapshot.Padding + 29, 28, 28) >= 3, "28-px tiles are still textured");
    }
}
