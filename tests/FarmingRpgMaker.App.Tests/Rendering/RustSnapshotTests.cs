using System.Text.Json;
using FarmEngine.Authoring;
using FarmEngine.Content;
using FarmEngine.Core;
using FarmEngine.Interop;
using FarmEngine.Json;
using FarmEngine.Rendering;
using FarmEngine.Schemas;
using FarmingRpgMaker.App.Game;

namespace FarmingRpgMaker.App.Tests.Rendering;

public sealed class RustSnapshotTests
{
    private static readonly ShellSnapshotOptions Options = new(32, 12, 50, 60, new SnapshotCamera(10, 20, 320, 240));

    public static TheoryData<string> Replays()
    {
        var result = new TheoryData<string>();
        foreach (var path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "RenderGoldens"), "*.json").Order(StringComparer.Ordinal))
            if (Path.GetFileNameWithoutExtension(path) != "index") result.Add(path);
        return result;
    }

    [Theory]
    [MemberData(nameof(Replays))]
    public void EveryRecordedSessionRendersLikeTheCSharpReference(string path)
    {
        if (!FarmFfi.IsAvailable) return;
        using var fixture = JsonDocument.Parse(File.ReadAllText(path));
        var root = fixture.RootElement;
        var project = root.GetProperty("project").Deserialize<GameProject>(JsonDefaults.Options)!;
        var content = ProjectContent.Compile(project);
        using var session = RustSession.Create(project, root.GetProperty("seed").GetString(), root.GetProperty("autoStartQuests").GetBoolean());
        // Snapshot every scene at both recorded endpoints, including mines and cross-scene NPCs.
        foreach (var name in new[] { "initialState", "finalState" })
        {
            var state = root.GetProperty(name).Deserialize<GameState>(JsonDefaults.Options)!;
            foreach (var scene in state.World.Scenes)
            {
                var viewState = state with { Player = state.Player with { SceneId = scene.Id } };
                session.SetState(viewState);
                AssertPlay(project, content, viewState, session);
                foreach (var modal in new[] { false, true })
                {
                    var actual = JsonSerializer.Deserialize<PlayRuntimeView>(session.RuntimeJson(modal), JsonDefaults.Options)!;
                    Assert.Equal(Hash.StableStringify(PlayRuntimeView.FromCSharp(project, content, viewState, modal)), Hash.StableStringify(actual));
                }
            }
        }
        foreach (var scene in project.Scenes) AssertEditor(project, content, scene);
    }

    [Fact]
    public void AuthoredArtAndAnimationBoundariesMatchWithoutMutatingTheState()
    {
        if (!FarmFfi.IsAvailable) return;
        var project = DefaultContent.CreateInitialProject(0);
        List<CustomAsset> assets =
        [
            new() { Id = "sheet", Type = "tile", TileType = "grass", DataUrl = "data:image/png;base64,a", Animations =
            [
                new() { Name = "idle", Frames = [new() { Width = 16, Height = 16, Ticks = 2 }, new() { AssetId = "other", X = 16, Width = 16, Height = 16, Ticks = 3 }] },
                new() { Name = "walk-left", Frames = [new() { X = 32, Width = 16, Height = 16, Ticks = 2 }] },
                new() { Name = "growth", Frames = [new() { Width = 16, Height = 16 }, new() { X = 16, Width = 16, Height = 16 }] },
            ] },
            new() { Id = "other", Type = "art", DataUrl = "data:image/png;base64,b", Width = 64, Height = 16 },
        ];
        var visual = new VisualRef { AssetId = "sheet" };
        var scene = project.Scenes[0];
        var tiles = scene.Tiles.Select(row => row.ToList()).ToList();
        tiles[1][1] = tiles[1][1] with
        {
            Background = "soil", SoilMoisture = 1, SoilFertility = 1,
            Crop = Crops.CreatePlantedCrop("custom", 1, false) with { Stage = 1, DaysGrown = 3 },
            Visuals = new() { Background = visual, Overlay = new() { AssetId = "missing" }, Object = new() { AssetId = "other" } },
            Item = project.Items[0] with { Visual = visual },
        };
        project = project with
        {
            CustomAssets = assets, PlayerVisual = visual, PlayerCustomImage = assets[0].DataUrl,
            Graphics = new() { PixelArt = false },
            Npcs = project.Npcs.Select(n => n with { Visual = visual }).ToList(),
            CustomCrops = [new() { Id = "custom", Name = "Custom", Stages = 2, GrowthDays = 2, Visual = visual }],
            Scenes = [scene with { Tiles = tiles }, .. project.Scenes.Skip(1)],
            Animals = [new() { Id = "cow-🌾", SpeciesId = "missing", SceneId = scene.Id, X = 3, Y = 4 }],
            GamePanels =
            [
                new() { Id = "status", Title = "Status", Entries =
                [
                    new() { Kind = "money", Label = "Gold" },
                    new() { Kind = "item", Label = "Items", Value = project.Items[0].Id },
                    new() { Kind = "flag", Label = "Ready", Value = "ready" },
                    new() { Kind = "action", Label = "Act", Value = "custom-action" },
                ] },
                new() { Id = "hidden", Title = "Hidden", VisibleFlag = "missing" },
            ],
        };
        var content = ProjectContent.Compile(project);
        using var session = RustSession.Create(project);
        foreach (var tick in new[] { 0, 1, 2, 4, 5, 100 })
        foreach (var moving in new[] { false, true })
        {
            var state = session.State();
            state = state with { Clock = state.Clock with { Tick = tick }, Player = state.Player with { Direction = "left", MoveIntent = new() { Dx = moving ? -1 : 0, Dy = 0 } } };
            session.SetState(state);
            var hash = session.StateHash();
            AssertPlay(project, content, state, session);
            Assert.Equal(hash, session.StateHash());
            foreach (var modal in new[] { false, true })
            {
                var runtime = JsonSerializer.Deserialize<PlayRuntimeView>(session.RuntimeJson(modal), JsonDefaults.Options)!;
                Assert.Equal(Hash.StableStringify(PlayRuntimeView.FromCSharp(project, content, state, modal)), Hash.StableStringify(runtime));
                Assert.Equal(!modal, runtime.Panels[0].Entries[^1].Enabled);
                Assert.True(runtime.Panels[1].Hidden);
            }
        }
        foreach (var map in project.Scenes) AssertEditor(project, content, map);
        Assert.Throws<FarmFfiException>(() => FarmFfi.EditorSnapshotJson(project, content, "missing", 28, 12));
        Assert.Throws<FarmFfiException>(() => session.SnapshotJson(new { invalid = true }));
        Assert.False(session.IsPoisoned);
        AssertPlay(project, content, session.State(), session);
    }

    [Fact]
    public void EditorPreviewPadsMissingTilesAndHidesOccupiedItems()
    {
        if (!FarmFfi.IsAvailable) return;
        var project = DefaultContent.CreateInitialProject(0);
        var scene = project.Scenes[0];
        scene = scene with { Width = 3, Height = 2, Tiles =
        [
            [new Tile { X = 0, Y = 0, Type = "soil", Background = "", Item = project.Items[0] },
             new Tile { X = 1, Y = 0, Type = "grass", Background = "grass", Item = project.Items[0] }],
        ] };
        project = project with
        {
            Scenes = [scene],
            Player = project.Player with { SceneId = scene.Id, X = 0.5, Y = 0.5 },
            Npcs = [project.Npcs[0] with { SceneId = scene.Id, X = 1, Y = 0 }],
        };
        var content = ProjectContent.Compile(project);
        AssertEditor(project, content, scene);
        var snapshot = JsonSerializer.Deserialize<WorldSnapshot>(FarmFfi.EditorSnapshotJson(project, content, scene.Id, 28, 12), JsonDefaults.Options)!;
        Assert.Null(snapshot.Tiles[0][0].Item);
        Assert.Null(snapshot.Tiles[0][1].Item);
        Assert.Equal("grass", snapshot.Tiles[1][2].Background);
        Assert.True(snapshot.Tiles[0][0].Tilled);
    }

    private static void AssertPlay(GameProject project, GameContent content, GameState state, RustSession session)
    {
        var scene = state.World.Scenes.First(s => s.Id == state.Player.SceneId);
        var expected = ShellSnapshot.BuildShellSnapshot(content, state, scene, Options);
        Graphics.ApplyGraphics(expected, GraphicsSource.FromState(project, content, state), scene, state.Clock.Tick,
            state.Player.MoveIntent.Dx != 0 || state.Player.MoveIntent.Dy != 0);
        var actual = JsonSerializer.Deserialize<WorldSnapshot>(session.SnapshotJson(Options), JsonDefaults.Options)!;
        Assert.Equal(Hash.StableStringify(expected), Hash.StableStringify(actual));
    }

    private static void AssertEditor(GameProject project, GameContent content, Scene scene)
    {
        var expected = ShellSnapshot.BuildEditorSnapshot(project, content, scene, 28, 12);
        Graphics.ApplyGraphics(expected, GraphicsSource.FromProject(project), scene, 0, false);
        var actual = JsonSerializer.Deserialize<WorldSnapshot>(FarmFfi.EditorSnapshotJson(project, content, scene.Id, 28, 12), JsonDefaults.Options)!;
        Assert.Equal(Hash.StableStringify(expected), Hash.StableStringify(actual));
    }
}
