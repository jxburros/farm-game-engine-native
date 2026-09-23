using System.Text.Json;
using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>
/// Farming math — extracted from src/lib/crops.ts, behavior-identical
/// (characterization-tested), with all randomness going through the seeded
/// RNG instead of Math.random. (Port of engine-core/src/farming/crops.ts.)
/// </summary>
public static class Crops
{
    public static CropDefinition? GetCropDefinitionFromContent(GameContent content, string cropType) =>
        content.Crops.TryGetValue(cropType, out var definition) ? definition : null;

    public static double GetCropStage(double plantedAt, double currentTime, double growthTime, double stages, bool watered = true)
    {
        var elapsed = currentTime - plantedAt;
        var stageTime = growthTime / stages;
        var waterPenalty = watered ? 1 : 0.5;
        var adjustedElapsed = elapsed * waterPenalty;
        return Math.Min(Math.Floor(adjustedElapsed / stageTime), stages - 1);
    }

    public static bool IsCropMature(double stage, double stages) => stage >= stages - 1;

    /// <summary>Returns one of <see cref="CropQualities"/>.</summary>
    public static string CalculateCropQuality(bool watered, bool fertilized, double daysWithoutWater)
    {
        double qualityScore = 0;
        if (watered) qualityScore += 2;
        if (fertilized) qualityScore += 3;
        qualityScore -= daysWithoutWater;

        if (qualityScore >= 4) return CropQualities.Iridium;
        if (qualityScore >= 2) return CropQualities.Gold;
        if (qualityScore >= 1) return CropQualities.Silver;
        return CropQualities.Normal;
    }

    /// <summary>Returns one of <see cref="CropMutations"/> or null. Always draws exactly one float when a definition is given.</summary>
    public static string? RollMutation(CropDefinition? definition, string quality, IRandomSource rng)
    {
        if (definition is null) return null;
        // `definition.mutationChance || 0`
        var baseChance = definition.MutationChance is double chance && chance != 0 && !double.IsNaN(chance) ? chance : 0;
        var qualityBonus = quality == CropQualities.Iridium ? 2 : quality == CropQualities.Gold ? 1.5 : quality == CropQualities.Silver ? 1.2 : 1;
        var finalChance = baseChance * qualityBonus;

        var roll = rng.Float();
        if (roll < finalChance * 0.1) return CropMutations.Ancient;
        if (roll < finalChance * 0.3) return CropMutations.Golden;
        if (roll < finalChance) return CropMutations.Giant;
        return null;
    }

    public static double RollYield(CropDefinition? definition, string quality, string? mutation, IRandomSource rng)
    {
        if (definition is null) return 1;
        var baseYield = rng.Int(definition.YieldMin, definition.YieldMax);
        var qualityBonus = quality == CropQualities.Iridium ? 1.5 : quality == CropQualities.Gold ? 1.3 : quality == CropQualities.Silver ? 1.1 : 1;
        var mutationBonus = mutation == CropMutations.Ancient ? 3 : mutation == CropMutations.Golden ? 2.5 : mutation == CropMutations.Giant ? 2 : 1;
        return Math.Floor(baseYield * qualityBonus * mutationBonus);
    }

    public static double CalculateHarvestValue(CropDefinition? definition, string quality, string? mutation, double quantity = 1)
    {
        if (definition is null) return 0;
        // Unknown keys read `undefined` in JS → NaN through the multiplication.
        var qualityMultiplier = ContentBuiltin.QualityMultipliers.TryGetValue(quality, out var q) ? q : double.NaN;
        var mutationKey = string.IsNullOrEmpty(mutation) ? "none" : mutation;
        var mutationMultiplier = ContentBuiltin.MutationMultipliers.TryGetValue(mutationKey, out var m) ? m : double.NaN;
        return Math.Floor(definition.BaseHarvestValue * qualityMultiplier * mutationMultiplier * quantity);
    }

    public static bool CanGrowInSeason(CropDefinition? definition, string season)
    {
        if (definition is null) return false;
        return definition.Seasons.Contains(season);
    }

    public static string GetCurrentSeason(double gameDay)
    {
        var seasonIndex = Math.Floor(gameDay / ContentBuiltin.DaysPerSeason) % ContentBuiltin.SeasonOrder.Count;
        // JS returns undefined for a negative index; mirror with a bounds check.
        var i = (int)seasonIndex;
        return i >= 0 && i < ContentBuiltin.SeasonOrder.Count ? ContentBuiltin.SeasonOrder[i] : null!;
    }

    public static double GetDayInSeason(double gameDay) => (gameDay % ContentBuiltin.DaysPerSeason) + 1;

    /// <summary>In-game days to maturity for a definition (with legacy-ms fallback).</summary>
    public static double CropGrowthDays(CropDefinition definition)
    {
        if (definition.GrowthDays is double days && days > 0) return days;
        return Math.Min(28, Math.Max(1, Js.Round(definition.GrowthTime / 5000)));
    }

    /// <summary>In-game days between repeat harvests for regrowing crops.</summary>
    public static double CropRegrowthDays(CropDefinition definition)
    {
        if (definition.RegrowthDays is double days && days > 0) return days;
        // `if (definition.regrowthTime)` — 0 and NaN are falsy.
        if (definition.RegrowthTime is double time && time != 0 && !double.IsNaN(time))
            return Math.Min(28, Math.Max(1, Js.Round(time / 5000)));
        return 1;
    }

    /// <summary>Visual growth stage for the day-based model (0 .. stages-1).</summary>
    public static double ComputeCropStage(Crop crop, CropDefinition definition)
    {
        var growthDays = CropGrowthDays(definition);
        var daysGrown = crop.DaysGrown ?? 0;
        var progress = Math.Min(1, daysGrown / growthDays);
        return Math.Min(Math.Floor(progress * (definition.Stages - 1)), definition.Stages - 1);
    }

    /// <summary>Mature when it has accumulated enough watered days.</summary>
    public static bool IsCropMatureByDays(Crop crop, CropDefinition definition)
    {
        if (crop.Withered == true) return false;
        return (crop.DaysGrown ?? 0) >= CropGrowthDays(definition);
    }

    /// <summary>Day-based planting (M2+): crops start unwatered — water them or they won't grow.</summary>
    public static Crop CreatePlantedCrop(string cropType, double plantedOnDay, bool fertilized) => new()
    {
        Type = cropType,
        PlantedAt = 0,
        PlantedOnDay = plantedOnDay,
        DaysGrown = 0,
        Stage = 0,
        Watered = false,
        LastWateredDay = null,
        Quality = fertilized ? CropQualities.Silver : CropQualities.Normal,
        Mutation = null,
        HarvestCount = 0,
        DaysWithoutWater = 0,
    };

    public static Crop InitializeCrop(string cropType, double plantedAt, bool fertilized) => new()
    {
        Type = cropType,
        PlantedAt = plantedAt,
        Stage = 0,
        Watered = true,
        LastWateredDay = 0,
        Quality = fertilized ? CropQualities.Silver : CropQualities.Normal,
        Mutation = null,
        HarvestCount = 0,
        DaysWithoutWater = 0,
    };

    public static bool CanPlaceMultiTileCrop(List<List<Tile>> tiles, double x, double y, double width, double height)
    {
        for (double dy = 0; dy < height; dy++)
        {
            for (double dx = 0; dx < width; dx++)
            {
                var checkX = x + dx;
                var checkY = y + dy;
                if (checkY < 0 || checkY >= tiles.Count || checkX < 0 || checkX >= tiles[0].Count)
                {
                    return false;
                }
                var tile = tiles[(int)checkY][(int)checkX];
                if (tile.Type != TileTypes.Soil || tile.Crop is not null)
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>Merge built-in crop definitions with a project's custom crops.</summary>
    public static OrderedDictionary<string, CropDefinition> MergeCropDefinitions(IEnumerable<CropDefinition>? customCrops = null)
    {
        var merged = new OrderedDictionary<string, CropDefinition>();
        foreach (var (id, definition) in ContentBuiltin.CropDefinitions) merged[id] = definition;
        foreach (var crop in customCrops ?? [])
        {
            merged[crop.Id] = crop;
        }
        return merged;
    }

    /// <summary>
    /// Overload for <c>project.customCrops</c>: in TS a CustomCropDefinition IS a
    /// CropDefinition (structural typing, passthrough keeps <c>customAsset</c>);
    /// here each one is converted via <see cref="ToCropDefinition"/>.
    /// </summary>
    public static OrderedDictionary<string, CropDefinition> MergeCropDefinitions(IEnumerable<CustomCropDefinition>? customCrops) =>
        MergeCropDefinitions(customCrops?.Select(ToCropDefinition));

    /// <summary>
    /// CustomCropDefinition → CropDefinition via a JSON round-trip, so every key
    /// (including <c>customAsset</c> and passthrough extras) survives in <c>Extra</c>
    /// exactly like the TS object would carry it.
    /// </summary>
    public static CropDefinition ToCropDefinition(CustomCropDefinition custom) =>
        JsonSerializer.Deserialize<CropDefinition>(JsonDefaults.ToElement(custom), JsonDefaults.Options)!;
}
