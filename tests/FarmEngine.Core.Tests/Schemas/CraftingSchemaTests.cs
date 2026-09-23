using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core.Tests.Schemas;

/// <summary>Port of packages/engine-schemas/src/crafting.test.ts.</summary>
public class CraftingSchemaTests
{
    [Fact]
    public void RecipeDefinitionDefaultsCategoryToCraftingAndLeavesRequiresStationCategoryUnsetWhenOmitted()
    {
        var recipe = JsonDefaults.Deserialize<RecipeDefinition>("""{"id":"recipe-x","name":"X","inputs":[],"outputs":[]}""")!;
        Assert.Equal("crafting", recipe.Category);
        Assert.Null(recipe.RequiresStationCategory);
    }

    [Fact]
    public void RecipeDefinitionKeepsAnExplicitCategoryAndRequiresStationCategory()
    {
        var recipe = JsonDefaults.Deserialize<RecipeDefinition>(
            """{"id":"recipe-y","name":"Y","inputs":[],"outputs":[],"category":"magic","requiresStationCategory":"arcane-circle"}""")!;
        Assert.Equal("magic", recipe.Category);
        Assert.Equal("arcane-circle", recipe.RequiresStationCategory);
    }

    [Fact]
    public void MachineTypeDefinitionDefaultsStationCategoriesToAnEmptyArray()
    {
        var machine = JsonDefaults.Deserialize<MachineTypeDefinition>("""{"id":"machine-x","name":"X"}""")!;
        Assert.Empty(machine.StationCategories);
    }

    [Fact]
    public void MachineTypeDefinitionKeepsExplicitStationCategories()
    {
        var machine = JsonDefaults.Deserialize<MachineTypeDefinition>(
            """{"id":"machine-kitchen","name":"Kitchen","stationCategories":["cooking"]}""")!;
        Assert.Equal(["cooking"], machine.StationCategories);
    }
}
