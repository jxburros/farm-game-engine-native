using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>
/// Weather (M4b): rolled at each day start from a per-season weighted table;
/// effects (rain waters soil, storms damage crops, NPCs shelter) apply in
/// the nightly pass and NPC scheduler. (Port of engine-core/src/weather.ts.)
/// </summary>
public static class Weather
{
    public static WeatherTypeDefinition? WeatherTypeById(EngineContext ctx, string weatherId) =>
        ctx.Content.Weather.Types.Find(type => type.Id == weatherId);

    public static WeatherTypeDefinition? CurrentWeather(EngineContext ctx, GameState state) =>
        WeatherTypeById(ctx, state.Clock.WeatherId);

    /// <summary>
    /// The weighted roll table for a season. Custom seasons (M9 calendars) may
    /// not have their own entry in <c>weather.table</c> — fall back to any other
    /// configured table so a creator-defined season still gets weather instead
    /// of always rolling the same type.
    /// </summary>
    public static List<WeatherTableEntry> WeatherTableForSeason(EngineContext ctx, string season)
    {
        var table = ctx.Content.Weather.Table;
        if (table.TryGetValue(season, out var own) && own is { Count: > 0 }) return own;
        // Object.keys order == insertion order for non-integer-like keys.
        foreach (var (key, entries) in table)
        {
            if (entries is { Count: > 0 }) return table[key];
        }
        return [];
    }

    /// <summary>Roll tomorrow's weather with the seeded RNG. Falls back to the first type.</summary>
    public static string RollWeather(EngineContext ctx, string season, Rng rng)
    {
        var table = WeatherTableForSeason(ctx, season);
        if (table.Count == 0)
        {
            var types = ctx.Content.Weather.Types;
            return types.Count > 0 ? types[0].Id : "sun";
        }
        var index = rng.Weighted(table.Select(entry => entry.Weight).ToList());
        return index >= 0 ? table[index].WeatherId : table[0].WeatherId;
    }
}
