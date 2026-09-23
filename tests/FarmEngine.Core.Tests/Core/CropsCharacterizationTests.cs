using FarmEngine.Json;
using FarmEngine.Schemas;
using static FarmEngine.Core.Tests.Core.CoreTestHelpers;

namespace FarmEngine.Core.Tests.Core;

/// <summary>
/// Port of tests/unit/crops.characterization.test.ts. src/lib/crops.ts is a
/// thin wrapper over the Core farming math; the wrappers are reproduced here
/// as local helpers (<c>GetCropDefinition(cropType, customCrops)</c> →
/// <see cref="Crops.MergeCropDefinitions(IEnumerable{CustomCropDefinition})"/>,
/// the Math.random-backed <c>legacyRandom</c> → a pinned <see cref="IRandomSource"/>).
/// </summary>
public class CropsCharacterizationTests
{
    /// <summary>TS legacyRandom with Math.random pinned to a fixed roll; counts draws.</summary>
    private sealed class PinnedRandom(double value) : IRandomSource
    {
        public int Draws { get; private set; }
        public double Float()
        {
            Draws++;
            return value;
        }
        public double Int(double min, double max)
        {
            Draws++;
            return Math.Floor(value * (max - min + 1)) + min;
        }
    }

    private static CustomCropDefinition MakeCustomCrop(string id, string name = "Custom", double baseHarvestValue = 100, double mutationChance = 0.5, List<string>? seasons = null) => new()
    {
        Id = id, Name = name, SeedCost = 5, BaseHarvestValue = baseHarvestValue, GrowthTime = 10000, Stages = 5,
        Seasons = seasons ?? ["winter"], CanRegrow = false, YieldMin = 1, YieldMax = 1, MutationChance = mutationChance,
    };

    private static OrderedDictionary<string, CropDefinition> GetAllCropDefinitions(List<CustomCropDefinition>? customCrops = null) =>
        Crops.MergeCropDefinitions(customCrops);

    private static CropDefinition? GetCropDefinition(string cropType, List<CustomCropDefinition>? customCrops = null) =>
        GetAllCropDefinitions(customCrops).TryGetValue(cropType, out var definition) ? definition : null;

    private static string? CheckForMutation(string cropType, string quality, double roll, List<CustomCropDefinition>? customCrops = null) =>
        Crops.RollMutation(GetCropDefinition(cropType, customCrops), quality, new PinnedRandom(roll));

    private static double CalculateYield(string cropType, string quality, string? mutation, double roll) =>
        Crops.RollYield(GetCropDefinition(cropType), quality, mutation, new PinnedRandom(roll));

    private static double CalculateHarvestValue(string cropType, string quality, string? mutation, double quantity = 1, List<CustomCropDefinition>? customCrops = null) =>
        Crops.CalculateHarvestValue(GetCropDefinition(cropType, customCrops), quality, mutation, quantity);

    private static bool CanGrowInSeason(string cropType, string season, List<CustomCropDefinition>? customCrops = null) =>
        Crops.CanGrowInSeason(GetCropDefinition(cropType, customCrops), season);

    /// <summary>h rows by w cols grid of empty soil tiles.</summary>
    private static List<List<Tile>> SoilGrid(int w, int h) =>
        Enumerable.Range(0, h).Select(_ => Enumerable.Range(0, w).Select(_ => new Tile { Type = "soil" }).ToList()).ToList();

    // --- exported constants ---

    [Fact]
    public void QualityMultipliersValues() =>
        Assert.Equal([("normal", 1.0), ("silver", 1.25), ("gold", 1.5), ("iridium", 2.0)], ContentBuiltin.QualityMultipliers.Select(kv => (kv.Key, kv.Value)));

    [Fact]
    public void MutationMultipliersValues() =>
        Assert.Equal([("none", 1.0), ("giant", 2.5), ("golden", 3.0), ("ancient", 4.0)], ContentBuiltin.MutationMultipliers.Select(kv => (kv.Key, kv.Value)));

    [Fact]
    public void DaysPerSeasonIs28() => Assert.Equal(28, ContentBuiltin.DaysPerSeason);

    [Fact]
    public void SeasonOrderIsSpringSummerFallWinter() => Assert.Equal(["spring", "summer", "fall", "winter"], ContentBuiltin.SeasonOrder);

    [Fact]
    public void CropDefinitionsContainsExactlyThe9BuiltInCropsKeyedById()
    {
        Assert.Equal(
            new[] { "blueberry", "carrot", "cauliflower", "corn", "potato", "pumpkin", "strawberry", "tomato", "wheat" },
            ContentBuiltin.CropDefinitions.Keys.Order(StringComparer.Ordinal));
        foreach (var (key, definition) in ContentBuiltin.CropDefinitions) Assert.Equal(key, definition.Id);
    }

    [Fact]
    public void WheatBuiltInDefinitionIsPinnedExactly() =>
        Assert.Equal(
            """{"baseHarvestValue":25,"canRegrow":false,"growthDays":3,"growthTime":15000,"id":"wheat","mutationChance":0.01,"name":"Wheat","seasons":["spring","fall"],"seedCost":10,"stages":4,"yieldMax":2,"yieldMin":1}""",
            StableJson.Stringify(ContentBuiltin.CropDefinitions["wheat"]));

    // --- getAllCropDefinitions / getCropDefinition ---

    [Fact]
    public void ReturnsOnlyThe9BuiltInsWithNoCustomCrops()
    {
        var all = GetAllCropDefinitions();
        Assert.Equal(9, all.Count);
        AssertDeepEqual(ContentBuiltin.CropDefinitions["wheat"], all["wheat"]);
        Assert.Equal(9, GetAllCropDefinitions([]).Count);
    }

    [Fact]
    public void MergesInCustomCropsUnderTheirId()
    {
        var all = GetAllCropDefinitions([MakeCustomCrop("moonberry", "Moonberry")]);
        Assert.Equal(10, all.Count);
        Assert.Equal("Moonberry", all["moonberry"].Name);
    }

    [Fact]
    public void CustomCropWithABuiltInIdOverridesTheBuiltInDefinition()
    {
        var all = GetAllCropDefinitions([MakeCustomCrop("wheat", "Evil Wheat", baseHarvestValue: 999)]);
        Assert.Equal(9, all.Count);
        Assert.Equal("Evil Wheat", all["wheat"].Name);
        Assert.Equal(999, all["wheat"].BaseHarvestValue);
    }

    [Fact]
    public void GetCropDefinitionResolvesBuiltInsUnknownsAndOverrides()
    {
        Assert.Same(ContentBuiltin.CropDefinitions["corn"], GetCropDefinition("corn"));
        Assert.Null(GetCropDefinition("nope"));
        List<CustomCropDefinition> custom = [MakeCustomCrop("moonberry"), MakeCustomCrop("wheat", "Evil Wheat")];
        Assert.Equal("moonberry", GetCropDefinition("moonberry", custom)?.Id);
        Assert.Equal("Evil Wheat", GetCropDefinition("wheat", custom)?.Name);
    }

    // --- getCropStage (wheat-like numbers: growthTime 15000, stages 4 → stageTime 3750) ---

    [Fact]
    public void GetCropStagePinsItsBehavior()
    {
        Assert.Equal(0, Crops.GetCropStage(0, 0, 15000, 4));
        Assert.Equal(0, Crops.GetCropStage(0, 3749, 15000, 4));
        Assert.Equal(1, Crops.GetCropStage(0, 3750, 15000, 4));
        // clamps to stages - 1 at and beyond full growth time
        Assert.Equal(3, Crops.GetCropStage(0, 15000, 15000, 4));
        Assert.Equal(3, Crops.GetCropStage(0, 1_000_000, 15000, 4));
        // watered defaults to true
        Assert.Equal(Crops.GetCropStage(0, 3750, 15000, 4, true), Crops.GetCropStage(0, 3750, 15000, 4));
        // unwatered halves effective elapsed time (0.5 penalty)
        Assert.Equal(2, Crops.GetCropStage(0, 7500, 15000, 4, true));
        Assert.Equal(1, Crops.GetCropStage(0, 7500, 15000, 4, false));
        Assert.Equal(2, Crops.GetCropStage(0, 15000, 15000, 4, false));
        Assert.Equal(3, Crops.GetCropStage(0, 30000, 15000, 4, false));
        // QUIRK: currentTime before plantedAt yields a NEGATIVE stage (no lower clamp)
        Assert.Equal(-1, Crops.GetCropStage(1000, 0, 15000, 4));
    }

    [Fact]
    public void IsMatureExactlyWhenStageIsAtLeastStagesMinusOne()
    {
        Assert.True(Crops.IsCropMature(3, 4));
        Assert.False(Crops.IsCropMature(2, 4));
        Assert.True(Crops.IsCropMature(4, 4)); // over-shoot still mature
        Assert.True(Crops.IsCropMature(0, 1));
    }

    // --- calculateCropQuality: score = (watered ? 2 : 0) + (fertilized ? 3 : 0) - daysWithoutWater ---

    [Theory]
    [InlineData(true, true, 0, "iridium")]
    [InlineData(true, true, 1, "iridium")]
    [InlineData(true, true, 2, "gold")]
    [InlineData(true, true, 3, "gold")]
    [InlineData(true, true, 4, "silver")]
    [InlineData(true, true, 5, "normal")]
    [InlineData(true, false, 0, "gold")]
    [InlineData(true, false, 1, "silver")]
    [InlineData(true, false, 2, "normal")]
    [InlineData(false, true, 0, "gold")] // QUIRK: fertilized only caps at gold
    [InlineData(false, false, 0, "normal")]
    public void CalculateCropQualityBoundaries(bool watered, bool fertilized, double daysWithoutWater, string expected) =>
        Assert.Equal(expected, Crops.CalculateCropQuality(watered, fertilized, daysWithoutWater));

    // --- checkForMutation ---

    [Theory]
    [InlineData(0, "ancient")]
    [InlineData(0.0499, "ancient")]
    [InlineData(0.05, "golden")] // exact ancient boundary falls through to golden
    [InlineData(0.1, "golden")]
    [InlineData(0.15, "giant")] // exact golden boundary falls through to giant
    [InlineData(0.4999, "giant")]
    [InlineData(0.5, null)] // exact chance boundary is NOT a mutation
    [InlineData(0.999, null)]
    public void RollBandsForAHalfChanceCropAtNormalQuality(double roll, string? expected) =>
        Assert.Equal(expected, CheckForMutation("mutey", "normal", roll, [MakeCustomCrop("mutey", mutationChance: 0.5)]));

    [Fact]
    public void QualityBonusScalesTheChance()
    {
        // wheat baseChance 0.01
        Assert.Equal("giant", CheckForMutation("wheat", "silver", 0.0119));
        Assert.Null(CheckForMutation("wheat", "normal", 0.0119));
        Assert.Equal("giant", CheckForMutation("wheat", "gold", 0.0149));
        Assert.Null(CheckForMutation("wheat", "silver", 0.0149));
        Assert.Equal("giant", CheckForMutation("wheat", "iridium", 0.0199));
        Assert.Null(CheckForMutation("wheat", "gold", 0.0199));
    }

    [Fact]
    public void AZeroMutationChanceCropNeverMutatesEvenOnRoll0() =>
        Assert.Null(CheckForMutation("dud", "iridium", 0, [MakeCustomCrop("dud", mutationChance: 0)]));

    [Fact]
    public void UnknownCropTypeMutationReturnsNullWithoutConsumingARandomRoll()
    {
        var rng = new PinnedRandom(0);
        Assert.Null(Crops.RollMutation(GetCropDefinition("nope"), "iridium", rng));
        Assert.Equal(0, rng.Draws);
    }

    // --- calculateYield ---

    [Theory]
    [InlineData("wheat", 0, 1)]
    [InlineData("wheat", 0.4999, 1)]
    [InlineData("wheat", 0.5, 2)]
    [InlineData("wheat", 0.9999, 2)]
    [InlineData("blueberry", 0, 2)]
    [InlineData("blueberry", 0.9999, 5)]
    public void BaseYieldRollSpansYieldMinToYieldMaxInclusive(string crop, double roll, double expected) =>
        Assert.Equal(expected, CalculateYield(crop, "normal", null, roll));

    [Fact]
    public void QualityAndMutationBonusesAreFlooredAndCompound()
    {
        // quality bonus is floored away on small yields (base 1)
        Assert.Equal(1, CalculateYield("wheat", "silver", null, 0));
        Assert.Equal(1, CalculateYield("wheat", "gold", null, 0));
        Assert.Equal(1, CalculateYield("wheat", "iridium", null, 0));
        // quality bonus applies on larger base yields (base 2)
        Assert.Equal(2, CalculateYield("wheat", "silver", null, 0.9999));
        Assert.Equal(2, CalculateYield("wheat", "gold", null, 0.9999));
        Assert.Equal(3, CalculateYield("wheat", "iridium", null, 0.9999));
        // mutation bonus: giant x2, golden x2.5, ancient x3 (pumpkin base yield always 1)
        Assert.Equal(2, CalculateYield("pumpkin", "normal", "giant", 0));
        Assert.Equal(2, CalculateYield("pumpkin", "normal", "golden", 0));
        Assert.Equal(3, CalculateYield("pumpkin", "normal", "ancient", 0));
        // compound
        Assert.Equal(9, CalculateYield("wheat", "iridium", "ancient", 0.9999));
    }

    [Fact]
    public void UnknownCropTypeYieldReturns1WithoutConsumingARandomRoll()
    {
        var rng = new PinnedRandom(0.9999);
        Assert.Equal(1, Crops.RollYield(GetCropDefinition("nope"), "iridium", "ancient", rng));
        Assert.Equal(0, rng.Draws);
    }

    // --- calculateHarvestValue ---

    [Fact]
    public void CalculateHarvestValuePinsItsBehavior()
    {
        Assert.Equal(25, CalculateHarvestValue("wheat", "normal", null));
        Assert.Equal(31, CalculateHarvestValue("wheat", "silver", null));
        Assert.Equal(37, CalculateHarvestValue("wheat", "gold", null));
        Assert.Equal(50, CalculateHarvestValue("wheat", "iridium", null));
        Assert.Equal(62, CalculateHarvestValue("wheat", "normal", "giant"));
        Assert.Equal(75, CalculateHarvestValue("wheat", "normal", "golden"));
        Assert.Equal(100, CalculateHarvestValue("wheat", "normal", "ancient"));
        Assert.Equal(600, CalculateHarvestValue("wheat", "iridium", "ancient", 3));
        Assert.Equal(0, CalculateHarvestValue("wheat", "iridium", "ancient", 0));
        Assert.Equal(0, CalculateHarvestValue("nope", "iridium", "ancient", 5));
        Assert.Equal(1000, CalculateHarvestValue("wheat", "normal", null, 1, [MakeCustomCrop("wheat", baseHarvestValue: 1000)]));
    }

    // --- canGrowInSeason ---

    [Fact]
    public void CanGrowInSeasonMatchesTheDefinitionSeasonsList()
    {
        Assert.True(CanGrowInSeason("wheat", "spring"));
        Assert.True(CanGrowInSeason("wheat", "fall"));
        Assert.False(CanGrowInSeason("wheat", "summer"));
        Assert.False(CanGrowInSeason("wheat", "winter"));
        Assert.True(CanGrowInSeason("carrot", "winter"));
        Assert.False(CanGrowInSeason("nope", "spring"));
        List<CustomCropDefinition> custom = [MakeCustomCrop("wheat", seasons: ["winter"])];
        Assert.False(CanGrowInSeason("wheat", "spring", custom));
        Assert.True(CanGrowInSeason("wheat", "winter", custom));
    }

    // --- getCurrentSeason / getDayInSeason ---

    [Theory]
    [InlineData(0, "spring")]
    [InlineData(27, "spring")]
    [InlineData(28, "summer")]
    [InlineData(55, "summer")]
    [InlineData(56, "fall")]
    [InlineData(83, "fall")]
    [InlineData(84, "winter")]
    [InlineData(111, "winter")]
    [InlineData(112, "spring")] // full year wraps
    [InlineData(-1, null)] // QUIRK: negative gameDay gives undefined season
    public void GameDayIsZeroBasedSeasonRollsOverAtDay28(double day, string? season) => Assert.Equal(season, Crops.GetCurrentSeason(day));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(27, 28)]
    [InlineData(28, 1)]
    [InlineData(55, 28)]
    [InlineData(112, 1)]
    [InlineData(-1, 0)] // QUIRK: (-1 % 28) + 1 = 0
    public void GetDayInSeasonIsOneBasedWithinA28DaySeason(double day, double expected) => Assert.Equal(expected, Crops.GetDayInSeason(day));

    // --- initializeCrop ---

    [Fact]
    public void UnfertilizedCropStartsNormalQualityWithAllCountersZeroed() =>
        Assert.Equal(
            """{"daysWithoutWater":0,"harvestCount":0,"lastWateredDay":0,"mutation":null,"plantedAt":12345,"quality":"normal","stage":0,"type":"wheat","watered":true}""",
            StableJson.Stringify(Crops.InitializeCrop("wheat", 12345, false)));

    [Fact]
    public void FertilizedCropStartsAtSilverQualityAndTypesAreUnvalidated()
    {
        Assert.Equal("silver", Crops.InitializeCrop("tomato", 0, true).Quality);
        Assert.Equal("not-a-real-crop", Crops.InitializeCrop("not-a-real-crop", 7, false).Type);
    }

    // --- canPlaceMultiTileCrop ---

    [Fact]
    public void CanPlaceMultiTileCropPinsItsBehavior()
    {
        Assert.True(Crops.CanPlaceMultiTileCrop(SoilGrid(4, 4), 0, 0, 2, 2));
        Assert.True(Crops.CanPlaceMultiTileCrop(SoilGrid(4, 4), 2, 2, 2, 2)); // flush with edge
        Assert.False(Crops.CanPlaceMultiTileCrop(SoilGrid(4, 4), 3, 0, 2, 2)); // off right edge
        Assert.False(Crops.CanPlaceMultiTileCrop(SoilGrid(4, 4), 0, 3, 2, 2)); // off bottom edge
        Assert.False(Crops.CanPlaceMultiTileCrop(SoilGrid(4, 4), -1, 0, 2, 2));
        Assert.False(Crops.CanPlaceMultiTileCrop(SoilGrid(4, 4), 0, -1, 2, 2));

        var occupied = SoilGrid(4, 4);
        occupied[1][1] = occupied[1][1] with { Crop = new Crop { Type = "wheat" } };
        Assert.False(Crops.CanPlaceMultiTileCrop(occupied, 0, 0, 2, 2));
        Assert.True(Crops.CanPlaceMultiTileCrop(occupied, 2, 2, 2, 2)); // clear region still fine

        var grass = SoilGrid(4, 4);
        grass[0][1] = grass[0][1] with { Type = "grass" };
        Assert.False(Crops.CanPlaceMultiTileCrop(grass, 0, 0, 2, 2));

        // QUIRK: zero-sized footprint is always placeable, even out of bounds
        Assert.True(Crops.CanPlaceMultiTileCrop(SoilGrid(2, 2), 99, 99, 0, 0));
    }

    [Fact]
    public void QuirkBoundsUseTheFirstRowLengthSoRaggedGridsThrow()
    {
        List<List<Tile>> ragged = [[new Tile { Type = "soil" }, new Tile { Type = "soil" }], [new Tile { Type = "soil" }]];
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => Crops.CanPlaceMultiTileCrop(ragged, 0, 0, 2, 2));
    }
}
