using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core.Tests.Core;

/// <summary>
/// Port of tests/unit/tools.characterization.test.ts (src/lib/tools.ts
/// re-exports the Core tool rules). Pins current behavior, quirks included:
/// durability 0 / maxDurability 0 are falsy no-ops for damage and repair,
/// repair amount 0 is a FULL repair, negative damage heals without a clamp,
/// and canUseTool only inspects tile.type.
/// </summary>
public class ToolsCharacterizationTests
{
    private static readonly string[] AllToolTypes = ["watering-can", "hoe", "axe", "pickaxe", "scythe", "fishing-rod"];

    private static Item MakeItem(string type = "tool", string? toolType = null, double? durability = null, double? maxDurability = null) => new()
    {
        Id = "item-1", Name = "Test Item", Description = "A test item", Type = type, Stackable = false, MaxStack = 1, Value = 10,
        ToolType = toolType, Durability = durability, MaxDurability = maxDurability,
    };

    private static Item MakeTool(string toolType, double? durability = null, double? maxDurability = null) =>
        MakeItem(toolType: toolType, durability: durability, maxDurability: maxDurability);

    private static Tile MakeTile(string type, string background = "grass", string? overlay = null, string? @object = null) => new()
    {
        X = 0, Y = 0, Type = type, Background = background, Overlay = overlay, Object = @object,
        Collision = false, SoilMoisture = 0, SoilFertility = 0,
    };

    // --- TOOL_DEFINITIONS content ---

    [Fact]
    public void ContainsExactlyTheSixKnownToolTypes() =>
        Assert.Equal(AllToolTypes.Order(StringComparer.Ordinal), ContentBuiltin.ToolDefinitions.Keys.Order(StringComparer.Ordinal));

    [Fact]
    public void PinsTheFullDefinitionOfEveryTool() =>
        Assert.Equal(
            """{"axe":{"action":"chop","description":"Chop down trees and wooden obstacles","energyCost":6,"name":"Axe","powerLevel":1,"type":"axe","validTargets":["wall"]},"fishing-rod":{"action":"fish","description":"Catch fish from water tiles","energyCost":3,"name":"Fishing Rod","powerLevel":1,"type":"fishing-rod","validTargets":["water"]},"hoe":{"action":"till","description":"Till grass into farmable soil","energyCost":4,"name":"Hoe","powerLevel":1,"type":"hoe","validTargets":["grass","path"]},"pickaxe":{"action":"mine","description":"Break rocks and mine for ore","energyCost":8,"name":"Pickaxe","powerLevel":1,"type":"pickaxe","validTargets":["wall"]},"scythe":{"action":"harvest","description":"Harvest crops in a large area","energyCost":5,"name":"Scythe","powerLevel":2,"type":"scythe","validTargets":["soil"]},"watering-can":{"action":"water","description":"Water crops to help them grow faster","energyCost":2,"name":"Watering Can","powerLevel":1,"type":"watering-can","validTargets":["soil"]}}""",
            StableJson.Stringify(ContentBuiltin.ToolDefinitions));

    [Fact]
    public void OnlyTheScytheHasPowerLevel2()
    {
        foreach (var t in AllToolTypes) Assert.Equal(t == "scythe" ? 2 : 1, ContentBuiltin.ToolDefinitions[t].PowerLevel);
    }

    // --- getToolDefinition ---

    [Fact]
    public void ReturnsTheToolDefinitionsEntrySameReference()
    {
        foreach (var toolType in AllToolTypes)
        {
            var definition = Tools.GetToolDefinition(toolType);
            Assert.Same(ContentBuiltin.ToolDefinitions[toolType], definition);
            Assert.Equal(toolType, definition.Type);
        }
    }

    [Fact]
    public void ReturnsUndefinedForAnUnknownToolTypeNoGuard() => Assert.Null(Tools.GetToolDefinition("chainsaw"));

    // --- canUseTool ---

    [Fact]
    public void ReturnsFalseForNonToolsAndUnknownToolTypes()
    {
        Assert.False(Tools.CanUseTool(MakeItem(type: "seed"), MakeTile("soil")));
        Assert.False(Tools.CanUseTool(MakeItem(toolType: "chainsaw"), MakeTile("grass")));
    }

    [Theory]
    [InlineData("watering-can", "soil", true)]
    [InlineData("watering-can", "grass", false)]
    [InlineData("watering-can", "water", false)]
    [InlineData("hoe", "grass", true)]
    [InlineData("hoe", "path", true)]
    [InlineData("hoe", "soil", false)]
    [InlineData("axe", "wall", true)]
    [InlineData("axe", "grass", false)]
    [InlineData("pickaxe", "wall", true)]
    [InlineData("pickaxe", "water", false)]
    [InlineData("scythe", "soil", true)]
    [InlineData("scythe", "grass", false)]
    [InlineData("fishing-rod", "water", true)]
    [InlineData("fishing-rod", "soil", false)]
    public void ToolOnTile(string toolType, string tileType, bool expected) =>
        Assert.Equal(expected, Tools.CanUseTool(MakeTool(toolType), MakeTile(tileType)));

    [Fact]
    public void NoToolIsUsableOnDoorOrFloorTiles()
    {
        foreach (var toolType in AllToolTypes)
        {
            Assert.False(Tools.CanUseTool(MakeTool(toolType), MakeTile("door")));
            Assert.False(Tools.CanUseTool(MakeTool(toolType), MakeTile("floor")));
        }
    }

    [Fact]
    public void OnlyInspectsTileTypeIgnoringLayers()
    {
        // Tile whose layers say "soil object on water background" but type says grass
        var layered = MakeTile("grass", background: "water", overlay: "path", @object: "soil");
        Assert.True(Tools.CanUseTool(MakeTool("hoe"), layered));
        Assert.False(Tools.CanUseTool(MakeTool("watering-can"), layered));
        Assert.False(Tools.CanUseTool(MakeTool("fishing-rod"), layered));
    }

    [Fact]
    public void IgnoresDurabilityEntirely() =>
        Assert.True(Tools.CanUseTool(MakeTool("hoe", durability: 0, maxDurability: 100), MakeTile("grass")));

    // --- getToolFromItem ---

    [Fact]
    public void GetToolFromItemReturnsTheDefinitionOrNull()
    {
        foreach (var toolType in AllToolTypes) Assert.Same(ContentBuiltin.ToolDefinitions[toolType], Tools.GetToolFromItem(MakeTool(toolType)));
        Assert.Null(Tools.GetToolFromItem(MakeItem(type: "crop")));
        Assert.Null(Tools.GetToolFromItem(MakeItem(toolType: "laser")));
    }

    // --- damageToolDurability ---

    [Fact]
    public void SubtractsTheGivenAmountAndReturnsANewObject()
    {
        var tool = MakeTool("axe", 50, 100);
        var result = Tools.DamageToolDurability(tool, 10);
        Assert.NotSame(tool, result);
        Assert.Equal(40, result.Durability);
        Assert.Equal(100, result.MaxDurability);
        Assert.Equal(50, tool.Durability);
    }

    [Fact]
    public void DamagePinsItsBehavior()
    {
        Assert.Equal(49, Tools.DamageToolDurability(MakeTool("axe", 50, 100)).Durability); // defaults the amount to 1
        Assert.Equal(0, Tools.DamageToolDurability(MakeTool("axe", 3, 100), 10).Durability); // clamps at 0
        var noDurability = MakeTool("axe", maxDurability: 100);
        Assert.Same(noDurability, Tools.DamageToolDurability(noDurability, 5));
        var noMax = MakeTool("axe", durability: 50);
        Assert.Same(noMax, Tools.DamageToolDurability(noMax, 5));
        // QUIRK: durability 0 is falsy, so a fully depleted tool is a no-op
        var depleted = MakeTool("axe", 0, 100);
        Assert.Same(depleted, Tools.DamageToolDurability(depleted, 5));
        // QUIRK: maxDurability 0 is falsy
        var zeroMax = MakeTool("axe", 50, 0);
        Assert.Same(zeroMax, Tools.DamageToolDurability(zeroMax, 5));
        // QUIRK: a negative amount heals the tool with no upper clamp
        Assert.Equal(105, Tools.DamageToolDurability(MakeTool("axe", 95, 100), -10).Durability);
    }

    // --- isToolBroken ---

    [Fact]
    public void IsToolBrokenPinsItsBehavior()
    {
        Assert.False(Tools.IsToolBroken(MakeTool("hoe")));
        Assert.True(Tools.IsToolBroken(MakeTool("hoe", 0, 100))); // M2 fix: exactly 0 IS broken
        Assert.True(Tools.IsToolBroken(MakeTool("hoe", -1, 100)));
        Assert.True(Tools.IsToolBroken(MakeTool("hoe", -100, 100)));
        Assert.False(Tools.IsToolBroken(MakeTool("hoe", 1, 100)));
        Assert.False(Tools.IsToolBroken(MakeTool("hoe", 100, 100)));
        Assert.False(Tools.IsToolBroken(MakeTool("hoe", durability: -5))); // requires maxDurability
    }

    // --- repairTool ---

    [Fact]
    public void PartialRepairAddsTheGivenAmountAndReturnsANewObject()
    {
        var tool = MakeTool("pickaxe", 40, 100);
        var result = Tools.RepairTool(tool, 25);
        Assert.NotSame(tool, result);
        Assert.Equal(65, result.Durability);
        Assert.Equal(40, tool.Durability);
    }

    [Fact]
    public void RepairPinsItsBehavior()
    {
        Assert.Equal(100, Tools.RepairTool(MakeTool("pickaxe", 7, 100)).Durability); // full repair
        Assert.Equal(100, Tools.RepairTool(MakeTool("pickaxe", 90, 100), 50).Durability); // clamps
        var noDurability = MakeTool("pickaxe", maxDurability: 100);
        Assert.Same(noDurability, Tools.RepairTool(noDurability, 10));
        var noMax = MakeTool("pickaxe", durability: 40);
        Assert.Same(noMax, Tools.RepairTool(noMax, 10));
        // QUIRK: a fully depleted tool can NEVER be repaired
        var depleted = MakeTool("pickaxe", 0, 100);
        Assert.Same(depleted, Tools.RepairTool(depleted, 50));
        Assert.Same(depleted, Tools.RepairTool(depleted));
        // QUIRK: amount 0 is falsy → FULL repair
        Assert.Equal(100, Tools.RepairTool(MakeTool("pickaxe", 30, 100), 0).Durability);
        // a negative amount reduces durability (upper clamp only)
        Assert.Equal(20, Tools.RepairTool(MakeTool("pickaxe", 30, 100), -10).Durability);
    }
}
