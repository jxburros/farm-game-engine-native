using FarmEngine.Content;
using FarmEngine.Core;
using FarmEngine.Rendering;
using FarmEngine.Schemas;

namespace FarmingRpgMaker.App.Tests.Rendering;

/// <summary>Port of packages/renderer-canvas2d/src/graphics.test.ts (graphics pipeline).</summary>
public sealed class GraphicsTests
{
    private static List<CustomAsset> Assets() =>
    [
        new CustomAsset
        {
            Id = "a",
            Name = "Sheet",
            Type = "art",
            DataUrl = "data:image/png;base64,a",
            Width = 32,
            Height = 16,
            Animations =
            [
                new AnimationClip
                {
                    Name = "idle",
                    Loop = true,
                    Frames =
                    [
                        new ArtFrame { X = 0, Y = 0, Width = 16, Height = 16, Ticks = 2 },
                        new ArtFrame { AssetId = "b", X = 0, Y = 0, Width = 16, Height = 16, Ticks = 3 },
                    ],
                },
            ],
        },
        new CustomAsset { Id = "b", Name = "Frame", Type = "art", DataUrl = "data:image/png;base64,b", Width = 16, Height = 16 },
    ];

    [Fact]
    public void SelectsExactFrameBoundariesAndResolvesSeparateImageFrames()
    {
        var assets = Assets();
        var reference = new VisualRef { AssetId = "a" };
        Assert.Equal(assets[0].DataUrl, Graphics.ResolveVisual(assets, reference, 1)?.ImageUrl);
        Assert.Equal(assets[1].DataUrl, Graphics.ResolveVisual(assets, reference, 2)?.ImageUrl);
        Assert.Equal(assets[1].DataUrl, Graphics.ResolveVisual(assets, reference, 4)?.ImageUrl);
        Assert.Equal(assets[0].DataUrl, Graphics.ResolveVisual(assets, reference, 5)?.ImageUrl);

        var once = Assets();
        once[0] = once[0] with { Animations = [once[0].Animations![0] with { Loop = false }] };
        Assert.Equal(assets[1].DataUrl, Graphics.ResolveVisual(once, reference, 100)?.ImageUrl);
        Assert.Null(Graphics.ResolveVisual(assets, new VisualRef { AssetId = "missing" }, 0));
    }

    [Fact]
    public void PrefersDirectionSpecificPlayerClipsOverGenericClips()
    {
        var assets = Assets();
        assets[0] = assets[0] with
        {
            Animations = [.. assets[0].Animations!, new AnimationClip { Name = "walk-left", Loop = true, Frames = [new ArtFrame { X = 16, Y = 0, Width = 16, Height = 16, Ticks = 1 }] }],
        };
        Assert.Equal(16, Graphics.ResolveVisual(assets, new VisualRef { AssetId = "a" }, 0, null, "left", true)?.SourceX);
        Assert.Equal(0, Graphics.ResolveVisual(assets, new VisualRef { AssetId = "a" }, 0, null, "left", false)?.SourceX);
    }

    [Fact]
    public void KeepsLegacyDirectionalSheetsWorking()
    {
        var sheet = Assets()[0] with
        {
            Animations = [],
            Sheet = new SpriteSheet { FrameWidth = 8, FrameHeight = 4, Frames = 4, Directional = true, TicksPerFrame = 2 },
        };
        var moving = Graphics.ResolveVisual([sheet], new VisualRef { AssetId = "a" }, 5, null, "up", true)!;
        Assert.Equal((2d, 3d), (moving.Frame, moving.Row));
        var idle = Graphics.ResolveVisual([sheet], new VisualRef { AssetId = "a" }, 5, null, "up", false)!;
        Assert.Equal((0d, 3d), (idle.Frame, idle.Row));
    }

    [Fact]
    public void PreservesPlayerAndTileArtThroughTheShellSnapshot()
    {
        var p = DefaultContent.CreateInitialProject(0);
        var tiles = p.Scenes[0].Tiles.Select(row => row.ToList()).ToList();
        tiles[0][0] = tiles[0][0] with { Visuals = new TileVisuals { Background = new VisualRef { AssetId = "b" } } };
        p = p with { CustomAssets = Assets(), PlayerVisual = new VisualRef { AssetId = "a" }, Scenes = [p.Scenes[0] with { Tiles = tiles }, .. p.Scenes.Skip(1)] };

        var state = EngineState.CreateGameState(p);
        var scene = state.World.Scenes[0];
        var content = EngineState.CreateContentFromProject(p);
        var snapshot = ShellSnapshot.BuildShellSnapshot(content, state, scene, new ShellSnapshotOptions(32, 0));
        Graphics.ApplyGraphics(snapshot, GraphicsSource.FromState(p, content, state), scene, 2, false);

        Assert.Equal(Assets()[1].DataUrl, snapshot.Player.Sprite?.ImageUrl);
        Assert.Equal(Assets()[1].DataUrl, snapshot.Tiles[0][0].ArtLayers?[0]?.ImageUrl);
    }
}
