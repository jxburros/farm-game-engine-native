using FarmEngine.Authoring;
using FarmEngine.Core;
using FarmEngine.Interop;
using FarmEngine.Rendering;
using FarmEngine.Schemas;
using FarmingRpgMaker.App.Game;

namespace FarmingRpgMaker.App.Tests.Rendering;

public sealed class RustSnapshotTests
{
    [Theory]
    [InlineData("starter")]
    [InlineData("blank")]
    [InlineData("cozy")]
    [InlineData("quest")]
    public void EveryTemplateSceneMatchesTheReferenceSnapshot(string template)
    {
        if (!FarmFfi.IsAvailable) return;
        var project = ProjectCatalog.CreateProjectForTemplate(template, 0);
        using var managed = new PlaySession(project, PlayEngineKind.CSharp);
        using var native = new PlaySession(project, PlayEngineKind.Rust);
        Assert.Equal(PlayEngineKind.Rust, native.EngineKind);
        var before = Hash.HashState(native.State);
        foreach (var scene in native.State.World.Scenes)
        {
            var options = new ShellSnapshotOptions(32, 12, 15.5, 26.7, new(2, 3, 640, 416));
            Assert.Equal(Hash.StableStringify(managed.Engine.Snapshot(scene, options)),
                Hash.StableStringify(native.Engine.Snapshot(scene, options)));
        }
        Assert.Equal(before, Hash.HashState(native.State));
    }

    [Fact]
    public void LiveCropsMachinesMissingTilesAndMovingEntitiesMatch()
    {
        if (!FarmFfi.IsAvailable) return;
        var project = ProjectCatalog.CreateInitialProject(0);
        using var native = new PlaySession(project, PlayEngineKind.Rust);
        var state = native.State;
        var scene = state.World.Scenes[0];
        var tiles = scene.Tiles.Select(row => row.ToList()).ToList();
        tiles[0][0] = tiles[0][0] with
        {
            Background = "", Type = "soil", SoilMoisture = 1, SoilFertility = 1,
            Crop = Crops.CreatePlantedCrop("wheat", 1, false) with { Stage = 3, DaysGrown = 99 },
            Node = new() { TypeId = "missing-node", RemainingHealth = 0 },
            Machine = new() { TypeId = "missing-machine", Processing = new(), Output = [new() { ItemId = "wheat", Quantity = 1 }] },
            Item = project.Items[0], LadderDown = true,
        };
        tiles[0][1] = tiles[0][1] with { Crop = tiles[0][0].Crop! with { Withered = true } };
        tiles[0][2] = tiles[0][2] with { Crop = tiles[0][0].Crop! with { Type = "missing-crop" } };
        tiles[^1] = []; // missing cells use the same grass fallback
        scene = scene with { Tiles = tiles };
        var npc = native.Content.Npcs.First();
        var updated = state with
        {
            World = state.World with { Scenes = [scene, .. state.World.Scenes.Skip(1)] },
            Animals = [new() { Id = "cow-🐄", SpeciesId = "missing-species", SceneId = scene.Id, X = 2, Y = 3 }],
            Npcs = new(state.Npcs) { [npc.Id] = new() { SceneId = scene.Id, X = 3, Y = 4, Path = [new() { X = 4, Y = 4 }] } },
        };
        native.DebugMutate((_, _) => updated);
        var options = new ShellSnapshotOptions(32, 12);
        var expected = ShellSnapshot.BuildShellSnapshot(native.Content, updated, scene, options);
        var actual = native.Engine.Snapshot(scene, options);
        Assert.Equal(Hash.StableStringify(expected), Hash.StableStringify(actual));
        Assert.True(actual.Tiles[0][0].Crop!.Mature);
        Assert.False(actual.Tiles[0][1].Crop!.Mature);
        Assert.Null(actual.Tiles[0][2].Crop);
        Assert.True(actual.Npcs[0].Moving);
        Assert.Equal("right", actual.Npcs[0].Direction);
        Assert.Equal("grass", actual.Tiles[^1][0].Background);
    }
}
