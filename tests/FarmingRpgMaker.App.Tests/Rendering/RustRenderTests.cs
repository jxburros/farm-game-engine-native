using System.Text.Json;
using FarmEngine.Authoring;
using FarmEngine.Core;
using FarmEngine.Interop;
using FarmEngine.Json;
using FarmEngine.Rendering;
using FarmEngine.Schemas;
using SkiaSharp;
using Xunit.Abstractions;

namespace FarmingRpgMaker.App.Tests.Rendering;

/// <summary>
/// The Rust renderer (<c>crates/farm-render</c>) against the C# reference: decorated Edit Mode
/// snapshots must be equal, and the Rust CPU raster must look like <see cref="SkiaWorldRenderer"/>.
/// Sprites pick the same source pixels as Skia, so Edit Mode frames match almost exactly;
/// anti-aliased edges, blurred shadows and glyph hinting differ slightly in play frames, so pixels
/// are compared with a tolerance. These run when the Rust library was built.
/// </summary>
public sealed class RustRenderTests(ITestOutputHelper output)
{
    /// <summary>Share of pixels whose channels must each be within <see cref="ChannelTolerance"/> (play frames).</summary>
    private const double MinWithinTolerance = 0.98;
    /// <summary>The same for Edit Mode frames: tiles and sprites only, no text, blur or weather.</summary>
    private const double MinWithinToleranceSprites = 0.999;
    private const int ChannelTolerance = 12;
    private const double MaxMeanAbsDiff = 0.5;

    private static readonly string RepoRoot = FindRoot();

    private static readonly Lazy<SKTypeface> InterBold = new(() =>
        SKTypeface.FromFile(Path.Combine(RepoRoot, "assets", "fonts", "Inter-Bold.ttf")));

    public static TheoryData<string> ProjectIds()
    {
        var ids = new TheoryData<string>();
        foreach (var template in ProjectCatalog.All)
        {
            ids.Add($"template:{template}");
        }

        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot, "fixtures", "golden", "content"), "*.json").Order(StringComparer.Ordinal))
        {
            ids.Add($"fixture:{Path.GetFileNameWithoutExtension(file)}");
        }

        ids.Add("custom-art");
        ids.Add("custom-art-smooth");
        return ids;
    }

    public static TheoryData<string> RasterScenarios() =>
    [
        "editor:template:starter",
        "editor:template:blank",
        "editor:template:cozy",
        "editor:template:quest",
        "editor:custom-art",
        "editor:custom-art-smooth",
        "play:template:starter:noon",
        "play:template:starter:dawn",
        "play:template:cozy:dusk-rain",
        "play:template:quest:night-snow",
        "play:custom-art:fall-pops",
    ];

    [Theory]
    [MemberData(nameof(ProjectIds))]
    public void DecoratedEditorSnapshotsMatchTheCSharpReference(string id)
    {
        if (!FarmFfi.IsAvailable)
        {
            return;
        }

        var project = Load(id);
        var content = ProjectContent.Compile(project);
        foreach (var scene in project.Scenes)
        {
            foreach (var (tileSize, padding) in new[] { (28.0, 12.0), (15.0, 5.0) })
            {
                var expected = Graphics.ApplyGraphics(
                    ShellSnapshot.BuildEditorSnapshot(project, content, scene, tileSize, padding),
                    GraphicsSource.FromProject(project),
                    scene,
                    0,
                    false);
                var json = RustRender.EditorSnapshotJson(project, scene.Id, tileSize, padding);
                var actual = JsonSerializer.Deserialize<WorldSnapshot>(json, JsonDefaults.Options)!;
                Assert.Equal(Hash.StableStringify(expected), Hash.StableStringify(actual));
            }
        }
    }

    [Theory]
    [MemberData(nameof(RasterScenarios))]
    public void RustRasterMatchesSkia(string id)
    {
        if (!FarmFfi.IsAvailable)
        {
            return;
        }

        var snapshot = Scenario(id);
        using var renderer = new SkiaWorldRenderer { PopTypeface = InterBold.Value };
        using var expected = renderer.RenderToBitmap(snapshot);
        var png = RustRender.RasterizePng(JsonSerializer.Serialize(snapshot, JsonDefaults.Options));
        using var actual = DecodePremultiplied(png);
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));

        var stats = Compare(expected, Pixels(actual));
        output.WriteLine($"{id} ({expected.Width}×{expected.Height}): {stats}");
        var minimum = id.StartsWith("editor:", StringComparison.Ordinal) ? MinWithinToleranceSprites : MinWithinTolerance;
        Assert.True(stats.WithinTolerance >= minimum, $"{id}: {stats}");
        Assert.True(stats.MeanAbsDiff <= MaxMeanAbsDiff, $"{id}: {stats}");
    }

    [Fact]
    public void PreviewRendersTheRequestedViewport()
    {
        if (!FarmFfi.IsAvailable)
        {
            return;
        }

        var project = Load("custom-art");
        var scene = project.Scenes.First(s => s.Id == project.Player.SceneId);
        using var preview = RustPreview.Create(project);
        var camera = new PreviewCamera(40, 30, 200.5, 120);
        var frame = preview.Render(scene.Id, 28, 12, camera, 2);
        Assert.Equal((401, 240), (frame.Width, frame.Height));
        Assert.Equal(401 * 240 * 4, frame.Pixels.Length);

        // The same viewport drawn by the C# renderer.
        var snapshot = EditorSnapshot(project, scene, 28, 12);
        snapshot.Camera = new SnapshotCamera(camera.X, camera.Y, camera.Width, camera.Height);
        using var renderer = new SkiaWorldRenderer();
        using var expected = renderer.RenderToBitmap(snapshot, 2);
        var stats = Compare(expected, frame.Pixels);
        output.WriteLine($"preview viewport ×2: {stats}");
        Assert.True(stats.WithinTolerance >= MinWithinToleranceSprites, stats.ToString());

        // Without a camera the whole scene is drawn.
        var whole = preview.Render(scene.Id, 28, 12);
        snapshot.Camera = null;
        var (width, height) = snapshot.WorldPixelSize();
        Assert.Equal(((int)Math.Ceiling(width), (int)Math.Ceiling(height)), (whole.Width, whole.Height));

        // A new project replaces the old one.
        var flooded = scene with
        {
            Tiles = scene.Tiles.Select(row => row.Select(t => t with { Type = "water", Background = "water", Overlay = null, Object = null, Visuals = null }).ToList()).ToList(),
        };
        preview.SetProject(project with { Scenes = [.. project.Scenes.Select(s => s.Id == scene.Id ? flooded : s)] });
        var water = preview.Render(scene.Id, 28, 12, camera, 2);
        Assert.Equal((frame.Width, frame.Height), (water.Width, water.Height));
        Assert.False(frame.Pixels.AsSpan().SequenceEqual(water.Pixels));
        Assert.Throws<FarmFfiException>(() => preview.Render("missing-scene"));
        Assert.False(preview.IsPoisoned);
    }

    [Fact]
    public void BadRequestsThrowWithTheRustMessage()
    {
        if (!FarmFfi.IsAvailable)
        {
            return;
        }

        var error = Assert.Throws<FarmFfiException>(() => RustRender.EditorSnapshotJson(Load("template:blank"), "missing-scene"));
        Assert.Contains("Scene missing-scene not found.", error.Message, StringComparison.Ordinal);
        Assert.Throws<FarmFfiException>(() => RustRender.RasterizePng("{}", 0));
    }

    // ---- scenarios -----------------------------------------------------------------------------

    private static WorldSnapshot EditorSnapshot(GameProject project, Scene scene, double tileSize, double padding) =>
        Graphics.ApplyGraphics(
            ShellSnapshot.BuildEditorSnapshot(project, ProjectContent.Compile(project), scene, tileSize, padding),
            GraphicsSource.FromProject(project),
            scene,
            0,
            false);

    private static WorldSnapshot Scenario(string id)
    {
        var parts = id.Split(':');
        if (parts[0] == "editor")
        {
            var project = Load(string.Join(':', parts.Skip(1)));
            var scene = project.Scenes.First(s => s.Id == project.Player.SceneId);
            return EditorSnapshot(project, scene, 28, 12);
        }

        var variant = parts[^1];
        var played = Load(string.Join(':', parts.Skip(1).Take(parts.Length - 2)));
        var content = EngineState.CreateContentFromProject(played);
        var state = EngineState.CreateGameState(played, "raster");
        var clock = variant switch
        {
            "noon" => state.Clock with { TimeMinutes = 12 * 60, WeatherId = "sun", Tick = 5 },
            "dawn" => state.Clock with { TimeMinutes = (6 * 60) + 10, WeatherId = "sun", Tick = 90 },
            "dusk-rain" => state.Clock with { TimeMinutes = (18 * 60) + 40, WeatherId = "rain", Tick = 437 },
            "night-snow" => state.Clock with { TimeMinutes = 23 * 60, WeatherId = "snow", Season = "winter", Tick = 1201 },
            _ => state.Clock with { TimeMinutes = 15 * 60, Season = "fall", Tick = 64 },
        };
        state = state with { Clock = clock };
        var playScene = state.World.Scenes.First(s => s.Id == state.Player.SceneId);
        const double ts = 32;
        const double padding = 12;
        var worldWidth = (playScene.Width * ts) + (padding * 2);
        var worldHeight = (playScene.Height * ts) + (padding * 2);
        // A viewport smaller than the scene, so the camera follows (with fractions) and culls.
        var pixelX = padding + ((state.Player.X - 0.37) * ts);
        var pixelY = padding + ((state.Player.Y - 0.5) * ts);
        var camera = Canvas2d.ComputeCamera(pixelX + (ts / 2), pixelY + (ts / 2), worldWidth, worldHeight, 300.5, 250);
        var snapshot = ShellSnapshot.BuildShellSnapshot(content, state, playScene, new ShellSnapshotOptions(ts, padding, pixelX, pixelY, camera));
        if (variant == "fall-pops")
        {
            snapshot.Pops =
            [
                new SnapshotPop { X = state.Player.X, Y = state.Player.Y, Text = "+15g", Age = 0.2 },
                new SnapshotPop { X = state.Player.X + 2, Y = state.Player.Y + 1, Text = "Wheat ×3", Color = "#7fd6cc", Age = 0.6 },
            ];
        }

        snapshot.Player.Moving = true;
        return Graphics.ApplyGraphics(snapshot, GraphicsSource.FromState(played, content, state), playScene, clock.Tick, true);
    }

    private static GameProject Load(string id)
    {
        if (id.StartsWith("template:", StringComparison.Ordinal))
        {
            return ProjectCatalog.CreateProjectForTemplate(id["template:".Length..], 0)!;
        }

        if (id.StartsWith("fixture:", StringComparison.Ordinal))
        {
            var path = Path.Combine(RepoRoot, "fixtures", "golden", "content", id["fixture:".Length..] + ".json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return JsonDefaults.Deserialize<GameProject>(document.RootElement.GetProperty("project").GetRawText())!;
        }

        return CustomArtProject(pixelArt: id != "custom-art-smooth");
    }

    /// <summary>
    /// The starter farm with creator art bound everywhere the pipeline reads it: player, NPCs
    /// (visual and legacy image), an animal species, node and machine types, a custom crop with a
    /// growth clip, placed items, tile-type art and per-tile layer overrides.
    /// </summary>
    private static GameProject CustomArtProject(bool pixelArt)
    {
        var project = ProjectCatalog.CreateInitialProject(0);
        var sheet = Png(64, 128, (x, y) => (x / 8 + y / 8) % 2 == 0 ? new SKColor((byte)(40 + (x * 3)), 90, (byte)(200 - y), 255) : SKColors.Transparent);
        var clips = Png(48, 16, (x, y) => (x / 16) switch
        {
            0 => new SKColor(220, 60, 60),
            1 => new SKColor(60, 200, 90),
            _ => (x + y) % 3 == 0 ? SKColors.Transparent : new SKColor(240, 220, 40),
        });
        var grass = Png(16, 16, (x, y) => (x * y) % 5 == 0 ? new SKColor(30, 120, 40) : new SKColor(80, 170, 60));
        var legacy = Png(20, 20, (x, y) => x < 10 == y < 10 ? new SKColor(250, 120, 20) : new SKColor(20, 20, 20, 160));
        var odd = Png(7, 5, (x, y) => new SKColor((byte)(x * 36), (byte)(y * 50), 180));
        List<CustomAsset> assets =
        [
            new() { Id = "sheet", Name = "Sheet", Type = "player", DataUrl = sheet, Width = 64, Height = 128, Sheet = new SpriteSheet { FrameWidth = 32, FrameHeight = 32, Frames = 2, TicksPerFrame = 6, Directional = true } },
            new()
            {
                Id = "clips", Name = "Clips", Type = "art", DataUrl = clips, Width = 48, Height = 16,
                Animations =
                [
                    new AnimationClip { Name = "idle", Frames = [Frame(0, 4), Frame(16, 4)] },
                    new AnimationClip { Name = "growth", Loop = false, Frames = [Frame(0, 1), Frame(16, 1), Frame(32, 1)] },
                ],
            },
            new() { Id = "grass", Name = "Grass", Type = "tile", TileType = "grass", DataUrl = grass, Width = 16, Height = 16 },
            new() { Id = "legacy", Name = "Legacy", Type = "npc", DataUrl = legacy, Width = 20, Height = 20 },
            new() { Id = "odd", Name = "Odd", Type = "art", DataUrl = odd, Width = 7, Height = 5 },
        ];
        VisualRef Visual(string assetId) => new() { AssetId = assetId };

        var scene = project.Scenes.First(s => s.Id == project.Player.SceneId);
        var tiles = scene.Tiles.Select(row => row.ToList()).ToList();
        tiles[2][3] = tiles[2][3] with { Item = project.Items[0] with { Visual = Visual("odd") } };
        tiles[2][4] = tiles[2][4] with { Item = project.Items[0] with { CustomImage = legacy } };
        tiles[3][3] = tiles[3][3] with { Object = "wall", Visuals = new TileVisuals { Object = Visual("odd") } };
        tiles[3][4] = tiles[3][4] with { Overlay = "path", Visuals = new TileVisuals { Overlay = Visual("clips") } };
        var soil = tiles.SelectMany(row => row).First(t => (string.IsNullOrEmpty(t.Background) ? t.Type : t.Background) == "soil");
        tiles[(int)soil.Y][(int)soil.X] = soil with { Crop = Crops.CreatePlantedCrop("moonberry", 1, false) with { Stage = 1 } };
        var npcs = project.Npcs.ToList();
        npcs[0] = npcs[0] with { Visual = new VisualRef { AssetId = "clips", Animation = "idle" } };
        if (npcs.Count > 1)
        {
            npcs[1] = npcs[1] with { CustomImage = legacy };
        }

        return project with
        {
            CustomAssets = assets,
            Graphics = new GraphicsSettings { PixelArt = pixelArt },
            PlayerVisual = Visual("sheet"),
            Npcs = npcs,
            NodeTypes = [.. project.NodeTypes.Select((node, i) => i == 0 ? node with { Visual = Visual("odd") } : node)],
            MachineTypes = [.. project.MachineTypes.Select((machine, i) => i == 0 ? machine with { Visual = new VisualRef { AssetId = "clips", Frame = Frame(16, 1) } } : machine)],
            AnimalSpecies = [.. project.AnimalSpecies.Select((species, i) => i == 0 ? species with { Visual = Visual("sheet") } : species)],
            Animals = [new AnimalState { Id = "hen-1", SpeciesId = project.AnimalSpecies[0].Id, Name = "Hen", SceneId = scene.Id, X = 6, Y = 7 }],
            CustomCrops =
            [
                new CustomCropDefinition { Id = "moonberry", Name = "Moonberry", Visual = Visual("clips"), Stages = 3, GrowthDays = 3, Seasons = ["spring", "summer", "fall"], YieldMin = 1, YieldMax = 2 },
            ],
            Scenes = [.. project.Scenes.Select(s => s.Id == scene.Id ? scene with { Tiles = tiles } : s)],
        };

        static ArtFrame Frame(double x, double ticks) => new() { X = x, Y = 0, Width = 16, Height = 16, Ticks = ticks };
    }

    private static string Png(int width, int height, Func<int, int, SKColor> color)
    {
        using var bitmap = new SKBitmap(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, color(x, y));
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return "data:image/png;base64," + Convert.ToBase64String(png.ToArray());
    }

    // ---- pixels ---------------------------------------------------------------------------------

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FarmingRpgMaker.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (FarmingRpgMaker.sln) was not found.");
    }

    private static SKBitmap DecodePremultiplied(byte[] png)
    {
        using var stream = new SKMemoryStream(png);
        using var codec = SKCodec.Create(stream);
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        return SKBitmap.Decode(codec, info);
    }

    /// <summary>Premultiplied RGBA8 rows without padding.</summary>
    private static byte[] Pixels(SKBitmap bitmap)
    {
        Assert.Equal(SKColorType.Rgba8888, bitmap.ColorType);
        var span = bitmap.GetPixelSpan();
        var pixels = new byte[bitmap.Width * bitmap.Height * 4];
        for (var y = 0; y < bitmap.Height; y++)
        {
            span.Slice(y * bitmap.RowBytes, bitmap.Width * 4).CopyTo(pixels.AsSpan(y * bitmap.Width * 4));
        }

        return pixels;
    }

    private static PixelStats Compare(SKBitmap expected, byte[] actual)
    {
        var reference = Pixels(expected);
        Assert.Equal(reference.Length, actual.Length);
        var pixels = reference.Length / 4;
        long within = 0;
        long absSum = 0;
        var maxDiff = 0;
        for (var i = 0; i < reference.Length; i += 4)
        {
            var pixelMax = 0;
            for (var c = 0; c < 4; c++)
            {
                var diff = Math.Abs(reference[i + c] - actual[i + c]);
                absSum += diff;
                pixelMax = Math.Max(pixelMax, diff);
            }

            maxDiff = Math.Max(maxDiff, pixelMax);
            if (pixelMax <= ChannelTolerance)
            {
                within++;
            }
        }

        return new PixelStats((double)within / pixels, (double)absSum / (pixels * 4), maxDiff);
    }

    private sealed record PixelStats(double WithinTolerance, double MeanAbsDiff, int MaxDiff)
    {
        public override string ToString() =>
            $"{WithinTolerance * 100:F2}% of pixels within ±{ChannelTolerance}, mean |Δ| {MeanAbsDiff:F3}/255 per channel, max |Δ| {MaxDiff}";
    }
}
