using System.Text.Json;
using FarmEngine.Content;
using FarmEngine.Json;
using FarmEngine.Schemas;
using static FarmEngine.Core.Tests.Core.CoreTestHelpers;

namespace FarmEngine.Core.Tests.Content;

/// <summary>
/// Port of tests/unit/game-helpers.characterization.test.ts — pins CURRENT
/// behavior of the game-helpers surface (Core tile/movement rules re-exported
/// there, <see cref="GameHelpers"/>, and the <see cref="DefaultContent"/>
/// factories), quirks and all. JS reference-identity assertions
/// (<c>toBe</c> on nested objects shared between lists) become structural
/// equality: C# records are immutable, so sharing is unobservable.
/// </summary>
public class GameHelpersCharacterizationTests
{
    /// <summary>Builds a "legacy" tile that predates the background/overlay/object layer fields.</summary>
    private static Tile LegacyTile(string type, double x = 0, double y = 0, bool collision = false, double soilMoisture = 0, double soilFertility = 0) =>
        new() { X = x, Y = y, Type = type, Collision = collision, SoilMoisture = soilMoisture, SoilFertility = soilFertility };

    private static void AssertJson(string expectedJson, object? actual) =>
        Assert.Equal(StableJson.Stringify(JsonDocument.Parse(expectedJson).RootElement), StableJson.Stringify(actual));

    // --- classifyTileType ---

    [Theory]
    [InlineData("grass", "background")]
    [InlineData("soil", "background")]
    [InlineData("water", "background")]
    [InlineData("floor", "background")]
    [InlineData("path", "overlay")]
    [InlineData("wall", "object")]
    [InlineData("door", "object")]
    public void ClassifiesTileTypes(string type, string layer) => Assert.Equal(layer, Tiles.ClassifyTileType(type));

    // --- createEmptyTile ---

    [Fact]
    public void CreateEmptyTileDefaultsToGrassOnTheBackgroundLayerWithNoCollision() =>
        AssertJson("""{"x":3,"y":7,"type":"grass","background":"grass","overlay":null,"object":null,"collision":false,"soilMoisture":0,"soilFertility":0}""",
            Tiles.CreateEmptyTile(3, 7));

    [Fact]
    public void RoutesBackgroundTypesToBackgroundKeepingOtherLayersEmpty()
    {
        foreach (var type in new[] { "soil", "water", "floor" })
        {
            var tile = Tiles.CreateEmptyTile(0, 0, type);
            Assert.Equal(type, tile.Type);
            Assert.Equal(type, tile.Background);
            Assert.Null(tile.Overlay);
            Assert.Null(tile.Object);
            Assert.False(tile.Collision);
        }
    }

    [Fact]
    public void RoutesPathToTheOverlayLayerWithAGrassBackgroundFill()
    {
        var tile = Tiles.CreateEmptyTile(1, 2, "path");
        Assert.Equal(("path", "grass", "path", (string?)null, false), (tile.Type, tile.Background, tile.Overlay, tile.Object, tile.Collision));
    }

    [Fact]
    public void RoutesWallToTheObjectLayerAndItIsTheOnlyTypeWithCollision()
    {
        var tile = Tiles.CreateEmptyTile(1, 2, "wall");
        Assert.Equal(("grass", (string?)null, "wall", true), (tile.Background, tile.Overlay, tile.Object, tile.Collision));
    }

    [Fact]
    public void RoutesDoorToTheObjectLayerWithoutCollisionQuirkDoorsAreWalkable()
    {
        var tile = Tiles.CreateEmptyTile(1, 2, "door");
        Assert.Equal(("grass", (string?)null, "door", false), (tile.Background, tile.Overlay, tile.Object, tile.Collision));
    }

    // --- migrateTile ---

    [Fact]
    public void ReturnsTheSameReferenceForAnAlreadyMigratedTile()
    {
        var tile = Tiles.CreateEmptyTile(0, 0, "soil");
        Assert.Same(tile, GameHelpers.MigrateTile(tile));
    }

    [Fact]
    public void PopulatesLayerFieldsOnALegacyTileWithoutBackgroundPreservingOtherData()
    {
        var legacy = LegacyTile("soil", x: 4, y: 5, soilMoisture: 3, soilFertility: 2);
        var migrated = GameHelpers.MigrateTile(legacy);
        Assert.NotSame(legacy, migrated);
        AssertJson("""{"x":4,"y":5,"type":"soil","background":"soil","overlay":null,"object":null,"collision":false,"soilMoisture":3,"soilFertility":2}""", migrated);
    }

    [Fact]
    public void TreatsAMissingBackgroundAsNeedingMigrationToo()
    {
        var tile = Tiles.CreateEmptyTile(0, 0, "path") with { Background = "" };
        var migrated = GameHelpers.MigrateTile(tile);
        Assert.NotSame(tile, migrated);
        Assert.Equal("grass", migrated.Background);
        Assert.Equal("path", migrated.Overlay);
        Assert.Null(migrated.Object);
    }

    [Fact]
    public void RoutesLegacyObjectTypesToObjectWithAGrassBackgroundAndDoesNotTouchCollision()
    {
        // A legacy door with collision:true keeps collision:true after migration
        // (unlike createEmptyTile, which would give a door collision:false).
        var migrated = GameHelpers.MigrateTile(LegacyTile("door", collision: true));
        Assert.Equal(("grass", (string?)null, "door", true), (migrated.Background, migrated.Overlay, migrated.Object, migrated.Collision));
    }

    [Fact]
    public void RoutesALegacyPathTileToTheOverlayLayer()
    {
        var migrated = GameHelpers.MigrateTile(LegacyTile("path"));
        Assert.Equal(("grass", "path", (string?)null), (migrated.Background, migrated.Overlay, migrated.Object));
    }

    // --- migrateSceneTiles ---

    [Fact]
    public void ReturnsTheSameSceneReferenceWhenNoTileNeedsMigration()
    {
        var scene = Tiles.CreateEmptyScene("s1", "Scene 1", 3, 2);
        Assert.Same(scene, GameHelpers.MigrateSceneTiles(scene));
    }

    [Fact]
    public void ReturnsANewSceneWithMigratedTilesWhenAnyTileIsLegacy()
    {
        var scene = Tiles.CreateEmptyScene("s1", "Scene 1", 3, 2);
        scene.Tiles[1][2] = LegacyTile("wall", x: 2, y: 1, collision: true);
        var migrated = GameHelpers.MigrateSceneTiles(scene);

        Assert.NotSame(scene, migrated);
        Assert.NotSame(scene.Tiles, migrated.Tiles);
        // The legacy tile got layer fields
        Assert.Equal("grass", migrated.Tiles[1][2].Background);
        Assert.Equal("wall", migrated.Tiles[1][2].Object);
        Assert.True(migrated.Tiles[1][2].Collision);
        // Already-migrated tiles keep their identity (migrateTile short-circuits)
        Assert.Same(scene.Tiles[0][0], migrated.Tiles[0][0]);
        // Non-tile scene fields are carried over (same references)
        Assert.Same(scene.Transitions, migrated.Transitions);
        Assert.Same(scene.Npcs, migrated.Npcs);
        Assert.Equal("s1", migrated.Id);
    }

    // --- setTileLayer ---

    [Fact]
    public void SetTileLayerReturnsANewObjectAndDoesNotMutateTheInput()
    {
        var tile = Tiles.CreateEmptyTile(0, 0, "grass");
        var before = StableJson.Stringify(tile);
        var updated = Tiles.SetTileLayer(tile, "soil");
        Assert.NotSame(tile, updated);
        Assert.Equal(before, StableJson.Stringify(tile));
    }

    [Fact]
    public void SetsABackgroundTypeLeavingOtherLayersAndCollisionAlone()
    {
        // Quirk: painting a background under a wall keeps the wall object AND its collision
        var updated = Tiles.SetTileLayer(Tiles.CreateEmptyTile(0, 0, "wall"), "soil");
        Assert.Equal(("soil", "soil", "wall", true), (updated.Type, updated.Background, updated.Object, updated.Collision));
    }

    [Fact]
    public void SetsAnOverlayTypeCollisionUntouched()
    {
        var updated = Tiles.SetTileLayer(Tiles.CreateEmptyTile(0, 0, "grass"), "path");
        Assert.Equal(("path", "grass", "path", (string?)null, false), (updated.Type, updated.Background, updated.Overlay, updated.Object, updated.Collision));
    }

    [Fact]
    public void SetsWallOnTheObjectLayerAndTurnsCollisionOn()
    {
        var updated = Tiles.SetTileLayer(Tiles.CreateEmptyTile(0, 0, "soil"), "wall");
        Assert.Equal(("wall", "soil", "wall", true), (updated.Type, updated.Background, updated.Object, updated.Collision));
    }

    [Fact]
    public void SetsDoorOnTheObjectLayerAndTurnsCollisionOffEvenOnAFormerWall()
    {
        var updated = Tiles.SetTileLayer(Tiles.CreateEmptyTile(0, 0, "wall"), "door");
        Assert.Equal(("door", "door", false), (updated.Type, updated.Object, updated.Collision));
    }

    // --- MOVEMENT_SPEED / interpolateToward / gridToPixel ---

    [Fact]
    public void MovementSpeedIs120() => Assert.Equal(120, GameHelpers.MovementSpeed);

    [Fact]
    public void InterpolateTowardPinsItsBehavior()
    {
        Assert.Equal(7, GameHelpers.InterpolateToward(7, 7, 999, 1));
        // steps toward the target by speed * deltaTime without overshooting
        Assert.Equal(2, GameHelpers.InterpolateToward(0, 10, 2, 1));
        Assert.Equal(5, GameHelpers.InterpolateToward(0, 10, 100, 0.05));
        // arrives exactly at the target when the step equals the remaining distance
        Assert.Equal(10, GameHelpers.InterpolateToward(0, 10, 10, 1));
        // clamps to the target when the step would overshoot
        Assert.Equal(10, GameHelpers.InterpolateToward(0, 10, 1000, 1));
        Assert.Equal(10, GameHelpers.InterpolateToward(9.5, 10, 100, 1));
        // moves in the negative direction when target < current
        Assert.Equal(8, GameHelpers.InterpolateToward(10, 0, 2, 1));
        Assert.Equal(3.5, GameHelpers.InterpolateToward(5, -5, 3, 0.5));
        // makes no progress with a zero step (quirk: returns current, not target)
        Assert.Equal(0, GameHelpers.InterpolateToward(0, 10, 0, 1));
        Assert.Equal(0, GameHelpers.InterpolateToward(0, 10, 120, 0));
    }

    [Fact]
    public void GridToPixelComputesPaddingPlusGridPosTimesTileSizePlusOne()
    {
        Assert.Equal(8, GameHelpers.GridToPixel(0, 32, 8));
        Assert.Equal(8 + 3 * 33, GameHelpers.GridToPixel(3, 32, 8)); // 107
        Assert.Equal(34, GameHelpers.GridToPixel(2, 16, 0));
        Assert.Equal(5, GameHelpers.GridToPixel(5, 0, 0)); // tileSize 0 still yields the +1 gap
    }

    // --- createEmptyScene ---

    [Fact]
    public void CreateEmptySceneDefaultsTo12x12()
    {
        var scene = Tiles.CreateEmptyScene("s", "S");
        Assert.Equal(12, scene.Width);
        Assert.Equal(12, scene.Height);
        Assert.Equal(12, scene.Tiles.Count);
        Assert.Equal(12, scene.Tiles[0].Count);
    }

    [Fact]
    public void CreatesARowsOfColumnsGridOfAllGrassTilesWithCorrectCoordinates()
    {
        var scene = Tiles.CreateEmptyScene("scene-x", "X", 4, 3);
        Assert.Equal(("scene-x", "X", 4.0, 3.0), (scene.Id, scene.Name, scene.Width, scene.Height));
        Assert.Equal(3, scene.Tiles.Count); // indexed tiles[y][x]
        for (var y = 0; y < 3; y++)
        {
            Assert.Equal(4, scene.Tiles[y].Count);
            for (var x = 0; x < 4; x++) AssertDeepEqual(Tiles.CreateEmptyTile(x, y, "grass"), scene.Tiles[y][x]);
        }
    }

    [Fact]
    public void StartsWithEmptyTransitionsNpcsAndEvents()
    {
        var scene = Tiles.CreateEmptyScene("s", "S", 2, 2);
        Assert.Empty(scene.Transitions);
        Assert.Empty(scene.Npcs);
        Assert.Empty(scene.Events);
    }

    // --- createDefaultPlayer ---

    [Fact]
    public void CreatesTheExactDefaultPlayer() =>
        AssertJson("""{"x":5,"y":5,"direction":"down","sceneId":"scene-farm","inventory":[],"maxInventorySize":20,"money":100,"activeQuests":[],"completedQuests":[],"pixelX":0,"pixelY":0,"targetX":0,"targetY":0}""",
            DefaultContent.CreateDefaultPlayer("scene-farm"));

    // --- createDefaultItems ---

    private static readonly List<Item> Items = DefaultContent.CreateDefaultItems();

    [Fact]
    public void ReturnsExactly49Items()
    {
        Assert.Equal(9, ContentBuiltin.CropDefinitions.Count);
        Assert.Equal(49, Items.Count);
    }

    [Fact]
    public void CreatesASeedAndACropItemForEveryCropDefinition()
    {
        foreach (var crop in ContentBuiltin.CropDefinitions.Values)
        {
            AssertDeepEqual(
                new Item
                {
                    Id = $"seed-{crop.Id}", Name = $"{crop.Name} Seeds", Description = $"Plant these to grow {crop.Name.ToLowerInvariant()}",
                    Type = "seed", Stackable = true, MaxStack = 99, Value = crop.SeedCost, CropType = crop.Id,
                },
                Items.First(i => i.Id == $"seed-{crop.Id}"));
            AssertDeepEqual(
                new Item
                {
                    Id = $"crop-{crop.Id}", Name = crop.Name, Description = $"Fresh {crop.Name.ToLowerInvariant()}",
                    Type = "crop", Stackable = true, MaxStack = 99, Value = crop.BaseHarvestValue, CropType = crop.Id,
                },
                Items.First(i => i.Id == $"crop-{crop.Id}"));
        }
    }

    [Fact]
    public void InterleavesSeedCropPairsFirstThenToolsFertilizersAndTheGiftInOrder()
    {
        Assert.Equal(["seed-wheat", "crop-wheat", "seed-corn", "crop-corn"], Items.Take(4).Select(i => i.Id));
        Assert.Equal(
            [
                "tool-hoe", "tool-watering-can", "tool-axe", "tool-pickaxe", "tool-scythe",
                "tool-hoe-2", "tool-watering-can-2", "tool-axe-2", "tool-pickaxe-2",
                "material-wood", "material-stone", "material-fiber",
                "fertilizer-basic", "fertilizer-quality", "gift-flower",
            ],
            Items.Skip(18).Take(15).Select(i => i.Id));
        // M4 additions follow the classic catalog
        Assert.Equal(
            [
                "tool-fishing-rod",
                "fish-carp", "fish-perch", "fish-catfish", "junk-boot",
                "feed-hay", "product-egg", "product-milk",
                "ore-copper", "ore-iron", "gem-quartz", "bar-copper", "bar-iron",
                "machine-furnace", "machine-preserves", "food-preserves",
            ],
            Items.Skip(33).Select(i => i.Id));
    }

    [Fact]
    public void Creates10ToolsTier1AndTier2()
    {
        var tools = Items.Where(i => i.Type == "tool").ToList();
        Assert.Equal(10, tools.Count);
        foreach (var tool in tools)
        {
            Assert.False(tool.Stackable);
            Assert.Equal(1, tool.MaxStack);
            var tier2 = tool.ToolTier == 2;
            Assert.Equal(tier2 ? 2 : 1, tool.ToolPower);
            Assert.Equal(tier2 ? 200 : 100, tool.Durability);
            Assert.Equal(tier2 ? 200 : 100, tool.MaxDurability);
        }
        Assert.Equal(
            [
                ("tool-hoe", "hoe", 50.0), ("tool-watering-can", "watering-can", 50.0), ("tool-axe", "axe", 100.0),
                ("tool-pickaxe", "pickaxe", 150.0), ("tool-scythe", "scythe", 200.0), ("tool-hoe-2", "hoe", 250.0),
                ("tool-watering-can-2", "watering-can", 250.0), ("tool-axe-2", "axe", 400.0), ("tool-pickaxe-2", "pickaxe", 500.0),
                ("tool-fishing-rod", "fishing-rod", 120.0),
            ],
            tools.Select(t => (t.Id, t.ToolType, t.Value)));
    }

    [Fact]
    public void Creates2FertilizersAnd1GiftWithPinnedValues()
    {
        Assert.Equal(
            [("fertilizer-basic", 10.0, true, 99.0), ("fertilizer-quality", 25.0, true, 99.0)],
            Items.Where(i => i.Type == "fertilizer").Select(f => (f.Id, f.Value, f.Stackable, f.MaxStack)));
        var gift = Assert.Single(Items, i => i.Type == "gift");
        Assert.Equal(("gift-flower", 20.0, true, 99.0), (gift.Id, gift.Value, gift.Stackable, gift.MaxStack));
    }

    // --- createInitialProject ---

    private const double FrozenNow = 1_700_000_000_000;

    private static GameProject BuildProject() => DefaultContent.CreateInitialProject(FrozenNow);

    [Fact]
    public void HasASingle16x12FarmSceneAndPinnedTopLevelFields()
    {
        var project = BuildProject();
        Assert.Equal(("project-1", "My Farming Game", "2.0"), (project.Id, project.Name, project.Version));
        var scene = Assert.Single(project.Scenes);
        Assert.Equal(("scene-farm", "Farm", 16.0, 12.0), (scene.Id, scene.Name, scene.Width, scene.Height));
        Assert.Equal("scene-farm", project.StartSceneId);
        Assert.Equal("play", project.Mode);
        Assert.Equal("grass", project.SelectedTileType);
        Assert.Null(project.SelectedNpcId);
        Assert.Null(project.SelectedItemId);
        Assert.Empty(project.EventFlags);
        Assert.Empty(project.Events);
        Assert.Empty(project.CustomAssets);
        Assert.Null(project.PlayerCustomImage);
        Assert.Equal("spring", project.CurrentSeason);
        Assert.Equal(1, project.CurrentDay);
    }

    [Fact]
    public void UsesDateNowForCurrentTimeAndGameStartTime()
    {
        var project = BuildProject();
        Assert.Equal(FrozenNow, project.CurrentTime);
        Assert.Equal(FrozenNow, project.GameStartTime);
    }

    [Fact]
    public void BuildsAWallBorderWithCollisionExceptAWalkableDoorAtTheBottomCenter()
    {
        var scene = BuildProject().Scenes[0];
        for (var y = 0; y < 12; y++)
        {
            foreach (var x in new[] { 0, 15 })
            {
                var tile = scene.Tiles[y][x];
                Assert.Equal(("wall", "wall", true, "grass"), (tile.Type, tile.Object, tile.Collision, tile.Background));
            }
        }
        for (var x = 0; x < 16; x++)
        {
            Assert.Equal(("wall", true), (scene.Tiles[0][x].Type, scene.Tiles[0][x].Collision));
            if (x != 8) Assert.Equal(("wall", true), (scene.Tiles[11][x].Type, scene.Tiles[11][x].Collision));
        }
        // Door replaces the wall at (8, 11): object layer swapped, collision cleared
        var door = scene.Tiles[11][8];
        Assert.Equal(("door", "door", (string?)null, "grass", false), (door.Type, door.Object, door.Overlay, door.Background, door.Collision));
    }

    [Fact]
    public void PlacesA7x5SoilPatchCenteredAt8x6GrassEverywhereElseInsideTheWalls()
    {
        var scene = BuildProject().Scenes[0];
        for (var y = 1; y <= 10; y++)
        {
            for (var x = 1; x <= 14; x++)
            {
                var tile = scene.Tiles[y][x];
                var inPatch = x >= 5 && x <= 11 && y >= 4 && y <= 8;
                Assert.Equal(inPatch ? "soil" : "grass", tile.Type);
                Assert.Equal(inPatch ? "soil" : "grass", tile.Background);
                Assert.Null(tile.Object);
                Assert.False(tile.Collision);
            }
        }
    }

    [Fact]
    public void PositionsThePlayerBelowTheSoilPatchWithThePinnedStarterInventory()
    {
        var project = BuildProject();
        Assert.Equal((8.0, 9.0, "scene-farm", 100.0), (project.Player.X, project.Player.Y, project.Player.SceneId, project.Player.Money));
        Assert.Equal(
            [("seed-wheat", 10.0), ("seed-tomato", 5.0), ("tool-hoe", 1.0), ("tool-watering-can", 1.0), ("fertilizer-basic", 10.0), ("snack-trail-mix", 2.0)],
            project.Player.Inventory.Select(slot => (slot.Item.Id, slot.Quantity)));
        // Inventory items are the catalog entries from project.items
        foreach (var slot in project.Player.Inventory) AssertDeepEqual(project.Items.First(i => i.Id == slot.Item.Id), slot.Item);
    }

    [Fact]
    public void IncludesTheFullDefaultItemCatalog()
    {
        var project = BuildProject();
        // Built-in items first, then the crafting showcase (kitchen/workbench + recipes) appended.
        var defaults = DefaultContent.CreateDefaultItems();
        AssertDeepEqual(defaults, project.Items.Take(defaults.Count).ToList());
        Assert.Equal(defaults.Count + 9, project.Items.Count);
    }

    [Fact]
    public void CreatesTheStationaryFarmerNpcWith2DialoguesMirroredInProjectDialogues()
    {
        var project = BuildProject();
        // M2: farmer + Merchant Mia (shop counter)
        Assert.Equal(2, project.Npcs.Count);
        var npc = project.Npcs[0];
        Assert.Equal(("npc-farmer", "Old Farmer", 3.0, 6.0, "scene-farm", false, "stationary", "farmer"),
            (npc.Id, npc.Name, npc.X, npc.Y, npc.SceneId, npc.CanMove, npc.MovePattern, npc.Appearance));
        Assert.Equal(2, npc.Dialogue.Count);
        Assert.Equal("dialogue-farmer-greeting", npc.Dialogue[0].Id);
        Assert.Equal(2, npc.Dialogue[0].Options.Count);
        Assert.Equal("dialogue-farmer-crops", npc.Dialogue[0].Options[1].NextDialogueId);
        Assert.Equal("dialogue-farmer-crops", npc.Dialogue[1].Id);
        Assert.Single(npc.Dialogue[1].Options);
        // project.dialogues mirrors the NPC dialogues (+ merchant's)
        Assert.Equal(3, project.Dialogues.Count);
        AssertDeepEqual(npc.Dialogue[0], project.Dialogues[0]);
        AssertDeepEqual(npc.Dialogue[1], project.Dialogues[1]);
        Assert.Equal("npc-merchant", project.Npcs[1].Id);
        AssertDeepEqual(project.Npcs[1].Dialogue[0], project.Dialogues[2]);
        Assert.Equal("shop-general", project.Npcs[1].Dialogue[0].Options[0].OpenShopId);
        // Quirk: scene.npcs (string id list) stays empty; the NPC only lives in project.npcs
        Assert.Empty(project.Scenes[0].Npcs);
    }

    [Fact]
    public void IncludesTheAutoStartFirstHarvestQuestWithPinnedObjectiveAndRewards()
    {
        var project = BuildProject();
        // M2: First Harvest + Supply Run chain
        Assert.Equal(2, project.Quests.Count);
        Assert.Equal("quest-go-shopping", project.Quests[1].Id);
        Assert.Equal(["quest-first-harvest"], project.Quests[1].Prerequisites!);
        AssertJson("""
            {"id":"quest-first-harvest","name":"First Harvest","description":"Plant and harvest your first crop to learn the basics of farming.",
             "giver":"npc-farmer","status":"not-started",
             "objectives":[{"id":"obj-harvest-wheat","type":"harvest","description":"Harvest 3 wheat","targetCropType":"wheat","targetCropQuantity":3,"completed":false,"progress":0}],
             "rewards":{"money":100,"items":[{"itemId":"seed-tomato","quantity":5}]},"autoStart":true,"repeatable":false}
            """, project.Quests[0]);
        // Player starts with no active quests despite autoStart (activation happens elsewhere)
        Assert.Empty(project.Player.ActiveQuests);
    }

    // --- canMoveTo ---

    private static Scene MakeScene()
    {
        var scene = Tiles.CreateEmptyScene("scene-a", "A", 4, 3);
        scene.Tiles[1][2] = Tiles.SetTileLayer(scene.Tiles[1][2], "wall");
        return scene;
    }

    private static readonly Npc Blocker = new()
    {
        Id = "npc-1", Name = "Blocker", X = 1, Y = 1, SceneId = "scene-a", Dialogue = [],
        CanMove = false, MovePattern = "stationary", Appearance = "farmer",
    };

    [Fact]
    public void CanMoveToReturnsFalseForOutOfBoundsCoordinates()
    {
        var scene = MakeScene();
        Assert.False(GameHelpers.CanMoveTo(scene, -1, 0, []));
        Assert.False(GameHelpers.CanMoveTo(scene, 4, 0, []));
        Assert.False(GameHelpers.CanMoveTo(scene, 0, -1, []));
        Assert.False(GameHelpers.CanMoveTo(scene, 0, 3, []));
    }

    [Fact]
    public void CanMoveToReturnsTrueForAnInBoundsOpenTile() => Assert.True(GameHelpers.CanMoveTo(MakeScene(), 0, 0, []));

    [Fact]
    public void CanMoveToReturnsFalseForACollisionTile() => Assert.False(GameHelpers.CanMoveTo(MakeScene(), 2, 1, []));

    [Fact]
    public void CanMoveToReturnsFalseWhenAnNpcInTheSameSceneOccupiesTheTile() =>
        Assert.False(GameHelpers.CanMoveTo(MakeScene(), 1, 1, [Blocker]));

    [Fact]
    public void CanMoveToExemptsTheNpcMatchingCurrentNpcIdFromBlocking() =>
        Assert.True(GameHelpers.CanMoveTo(MakeScene(), 1, 1, [Blocker], "npc-1"));

    [Fact]
    public void CanMoveToIgnoresAnNpcAtTheSameCoordinatesButInADifferentScene() =>
        Assert.True(GameHelpers.CanMoveTo(MakeScene(), 1, 1, [Blocker with { Id = "npc-2", SceneId = "scene-b" }]));

    // --- getDirectionVector ---

    [Theory]
    [InlineData("up", 0, -1)]
    [InlineData("down", 0, 1)]
    [InlineData("left", -1, 0)]
    [InlineData("right", 1, 0)]
    [InlineData("diagonal", 0, 0)]
    [InlineData("", 0, 0)]
    public void MapsDirectionsToVectors(string direction, double dx, double dy)
    {
        var vector = WorldMovement.GetDirectionVector(direction);
        Assert.Equal((dx, dy), (vector.Dx, vector.Dy));
    }
}
