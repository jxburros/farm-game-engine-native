using FarmEngine.Content;
using FarmEngine.Core;
using FarmEngine.Rendering;

namespace FarmingRpgMaker.App.Tests.Rendering;

/// <summary>packages/game-shell/src/snapshot.ts port: state → plain-data world snapshot.</summary>
public sealed class ShellSnapshotTests
{
    [Fact]
    public void BuildsTilesEntitiesAndPlayerFromLiveState()
    {
        var project = DefaultContent.CreateInitialProject(0);
        var ctx = new EngineContext(EngineState.CreateContentFromProject(project));
        var state = EngineState.CreateGameState(project);
        var scene = state.World.Scenes.First(s => s.Id == state.Player.SceneId);
        var camera = Canvas2d.ComputeCamera(100, 100, 600, 400, 320, 240);

        var snapshot = ShellSnapshot.BuildShellSnapshot(ctx.Content, state, scene, new ShellSnapshotOptions(32, 12, 50, 60, camera));

        Assert.Equal((int)scene.Width, snapshot.Width);
        Assert.Equal((int)scene.Height, snapshot.Height);
        Assert.Equal((int)scene.Height, snapshot.Tiles.Count);
        Assert.All(snapshot.Tiles, row => Assert.Equal((int)scene.Width, row.Count));
        Assert.Equal(0, snapshot.TileGap);
        Assert.False(snapshot.GridOverlay);
        Assert.Same(camera, snapshot.Camera);
        Assert.Equal(ctx.Content.Npcs.Count(n => n.SceneId == scene.Id) + state.Animals.Count(a => a.SceneId == scene.Id), snapshot.Npcs.Count);
        Assert.Equal((state.Player.X, state.Player.Y, state.Player.Direction), (snapshot.Player.X, snapshot.Player.Y, snapshot.Player.Direction));
        Assert.Equal((50d, 60d), (snapshot.Player.PixelX!.Value, snapshot.Player.PixelY!.Value));

        // Layers map straight through: background falls back to the tile type.
        var tile = scene.Tiles[0][0];
        Assert.Equal(string.IsNullOrEmpty(tile.Background) ? tile.Type : tile.Background, snapshot.Tiles[0][0].Background);
        Assert.Equal(tile.Object, snapshot.Tiles[0][0].Object);

        // Gathering nodes carry their type color and depletion.
        var nodeTile = scene.Tiles.SelectMany(row => row).First(t => t.Node is not null);
        var snapNode = snapshot.Tiles[(int)nodeTile.Y][(int)nodeTile.X].Node!;
        Assert.Equal(ctx.Content.NodeTypes.Last(n => n.Id == nodeTile.Node!.TypeId).Color, snapNode.Color);
        Assert.False(snapNode.Depleted);
    }

    [Fact]
    public void CropsReportStageColorAndMaturity()
    {
        var project = DefaultContent.CreateInitialProject(0);
        var state = EngineState.CreateGameState(project);
        var scene = state.World.Scenes[0];
        var content = EngineState.CreateContentFromProject(project);
        var tiles = scene.Tiles.Select(row => row.ToList()).ToList();
        tiles[1][1] = tiles[1][1] with { Crop = Crops.CreatePlantedCrop("wheat", 1, false) with { Stage = 3, DaysGrown = 99 } };
        var withCrop = scene with { Tiles = tiles };

        var snapshot = ShellSnapshot.BuildShellSnapshot(content, state, withCrop, new ShellSnapshotOptions(32, 0));

        var crop = snapshot.Tiles[1][1].Crop!;
        Assert.Equal(3, crop.ColorIndex);
        Assert.True(crop.Mature);
        Assert.False(crop.Withered);
    }

    [Fact]
    public void EditorSnapshotUsesGridSeamsAndHidesItemsUnderThePlayer()
    {
        var project = DefaultContent.CreateInitialProject(0);
        var content = EngineState.CreateContentFromProject(project);
        var scene = project.Scenes.First(s => s.Id == project.Player.SceneId);
        var tiles = scene.Tiles.Select(row => row.ToList()).ToList();
        var px = (int)Math.Floor(project.Player.X);
        var py = (int)Math.Floor(project.Player.Y);
        tiles[py][px] = tiles[py][px] with { Item = project.Items[0] };
        tiles[0][1] = tiles[0][1] with { Item = project.Items[0] };
        scene = scene with { Tiles = tiles };

        var snapshot = ShellSnapshot.BuildEditorSnapshot(project, content, scene);

        Assert.Equal(1, snapshot.TileGap);
        Assert.True(snapshot.GridOverlay);
        Assert.Equal(28, snapshot.TileSize);
        Assert.Null(snapshot.Tiles[py][px].Item);
        Assert.NotNull(snapshot.Tiles[0][1].Item);
    }
}
