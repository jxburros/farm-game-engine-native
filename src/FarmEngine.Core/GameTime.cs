using System.Collections;
using System.Reflection;
using System.Text.Json;
using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>TS <c>DayPhase</c>: 'morning' | 'day' | 'evening' | 'night'.</summary>
public static class DayPhases
{
    public const string Morning = "morning";
    public const string Day = "day";
    public const string Evening = "evening";
    public const string Night = "night";
}

/// <summary>TS <c>SleepOptions</c>.</summary>
public sealed record SleepOptions(bool Collapsed = false);

/// <summary>
/// Game clock &amp; calendar (M2, made creator-configurable in M9). Ticks advance
/// in-game minutes; the <c>sleep</c> command (or a collapse) ends the day; the
/// last day of a season rolls to the next season; wrapping past the last
/// season rolls the year. The nightly pass is where the world "lives": crops
/// grow, soil dries, nodes respawn, energy restores.
///
/// All calendar math below is driven by a project's <c>settings.calendar</c>
/// (seasons + their lengths, plus festival days) rather than a hardcoded
/// 4×28 layout. Every helper takes the resolved <see cref="CalendarConfig"/> — usually
/// <c>ctx.Content.Settings.Calendar</c> — rather than a full <see cref="EngineContext"/>, so
/// calendar math stays a pure, easily-testable function of calendar + day.
/// (Port of engine-core/src/time.ts.)
/// </summary>
public static class GameTime
{
    /// <summary>Effective season list: the project's calendar, or the classic four-season/28-day fallback when it's empty or every season has a non-positive length.</summary>
    public static List<CalendarSeason> CalendarSeasons(CalendarConfig calendar)
    {
        var seasons = calendar.Seasons.Where(season => season.Days > 0).ToList();
        return seasons.Count > 0 ? seasons : SettingsSchema.ClassicCalendarSeasons();
    }

    private static double CalendarYearLength(List<CalendarSeason> seasons)
    {
        double sum = 0;
        foreach (var season in seasons) sum += season.Days;
        return sum;
    }

    /// <summary>Look up a calendar season by id (searches the effective, fallback-safe list).</summary>
    public static CalendarSeason? SeasonById(CalendarConfig calendar, string id) =>
        CalendarSeasons(calendar).Find(season => season.Id == id);

    private sealed record CalendarPosition(CalendarSeason Season, int SeasonIndex, double DayOfSeason);

    /// <summary>Resolve an absolute (1-based) day to its season + day-of-season, honoring each season's own length via cumulative offsets.</summary>
    private static CalendarPosition PositionForDay(CalendarConfig calendar, double absoluteDay)
    {
        var seasons = CalendarSeasons(calendar);
        var yearLength = CalendarYearLength(seasons);
        var dayInYear = ((absoluteDay - 1) % yearLength + yearLength) % yearLength;
        var remaining = dayInYear;
        for (var i = 0; i < seasons.Count; i++)
        {
            if (remaining < seasons[i].Days)
            {
                return new CalendarPosition(seasons[i], i, remaining + 1);
            }
            remaining -= seasons[i].Days;
        }
        // Unreachable when yearLength = sum(seasons.days), kept for type safety.
        var last = seasons[^1];
        return new CalendarPosition(last, seasons.Count - 1, last.Days);
    }

    /// <summary>1-based day within the current season for an absolute (1-based) day.</summary>
    public static double DayOfSeason(CalendarConfig calendar, double absoluteDay) =>
        PositionForDay(calendar, absoluteDay).DayOfSeason;

    public static string SeasonForDay(CalendarConfig calendar, double absoluteDay) =>
        PositionForDay(calendar, absoluteDay).Season.Id;

    public static double YearForDay(CalendarConfig calendar, double absoluteDay)
    {
        var yearLength = CalendarYearLength(CalendarSeasons(calendar));
        return Math.Floor((absoluteDay - 1) / yearLength) + 1;
    }

    /// <summary>The festival configured for the given absolute day, if any.</summary>
    public static CalendarFestival? FestivalOnDay(CalendarConfig calendar, double absoluteDay)
    {
        if (calendar.Festivals.Count == 0) return null;
        var position = PositionForDay(calendar, absoluteDay);
        return calendar.Festivals.Find(festival => festival.SeasonId == position.Season.Id && festival.Day == position.DayOfSeason);
    }

    /// <summary>Format minute-of-day as a clock string, e.g. 810 → "1:30 PM".</summary>
    public static string FormatTimeOfDay(double timeMinutes)
    {
        var total = Math.Floor(timeMinutes) % (24 * 60);
        var hours24 = Math.Floor(total / 60);
        var minutes = total % 60;
        var suffix = hours24 < 12 ? "AM" : "PM";
        var hours12 = hours24 % 12 == 0 ? 12 : hours24 % 12;
        return $"{Js.Num(hours12)}:{Js.Num(minutes).PadLeft(2, '0')} {suffix}";
    }

    /// <summary>Returns one of <see cref="DayPhases"/>.</summary>
    public static string DayPhase(double timeMinutes)
    {
        var t = timeMinutes % (24 * 60);
        if (t >= 5 * 60 && t < 10 * 60) return DayPhases.Morning;
        if (t >= 10 * 60 && t < 17 * 60) return DayPhases.Day;
        if (t >= 17 * 60 && t < 21 * 60) return DayPhases.Evening;
        return DayPhases.Night;
    }

    /// <summary>
    /// End the day: nightly world pass + calendar roll + energy restore.
    /// Emits onDayEnd / onSeasonChange / onYearStart / onDayStart hooks.
    /// </summary>
    public static EngineStep PerformSleep(EngineContext ctx, GameState state, SleepOptions? options = null)
    {
        options ??= new SleepOptions(false);
        var effects = new List<Effect>();
        var settings = ctx.Content.Settings;
        var previousDay = state.Clock.Day;
        var previousSeason = state.Clock.Season;
        var previousYear = state.Clock.Year;

        ctx.Hooks?.Emit(HookNames.OnDayEnd, new DayHookPayload(previousDay, previousSeason, previousYear));

        var newDay = previousDay + 1;
        var calendar = settings.Calendar;
        var seasons = CalendarSeasons(calendar);
        // Season progression is relative to the CURRENT season so authored
        // projects may start in any season regardless of the absolute day.
        // dayOfSeason(newDay) === 1 detects a season boundary at the calendar's
        // cumulative offsets — correct for uneven season lengths too, since only
        // one day elapses per sleep.
        var seasonRolls = DayOfSeason(calendar, newDay) == 1;
        var previousSeasonIndex = Math.Max(0, seasons.FindIndex(season => season.Id == previousSeason));
        var newSeason = seasonRolls
            ? seasons[(previousSeasonIndex + 1) % seasons.Count].Id
            : previousSeason;
        var newYear = seasonRolls && newSeason == seasons[0].Id ? previousYear + 1 : previousYear;

        // Object.fromEntries: later duplicates win.
        var nodeTypes = new Dictionary<string, NodeTypeDefinition>();
        foreach (var def in ctx.Content.NodeTypes) nodeTypes[def.Id] = def;

        // Roll the new day's weather with the seeded RNG (M4). Its effects (rain
        // watering, storm damage) apply while the world pass runs below.
        var rng = new Rng(state.Rng);
        var rolledWeatherId = ctx.Content.Weather.Types.Count > 0
            ? Weather.RollWeather(ctx, newSeason, rng)
            : state.Clock.WeatherId;
        // onWeatherRoll has reroll capability: a listener may return
        // { weatherId } to override the roll (last valid override wins; order is
        // deterministic subscription order).
        var rollResponses = ctx.Hooks?.Collect(HookNames.OnWeatherRoll, new WeatherRollHookPayload(rolledWeatherId, newDay)) ?? [];
        var newWeatherId = rolledWeatherId;
        foreach (var response in rollResponses)
        {
            var weatherOverride = ReadWeatherIdOverride(response);
            if (weatherOverride is not null && ctx.Content.Weather.Types.Any(type => type.Id == weatherOverride))
            {
                newWeatherId = weatherOverride;
            }
        }
        var weatherDef = ctx.Content.Weather.Types.Find(type => type.Id == newWeatherId);

        // Nightly world pass
        var scenes = state.World.Scenes.Select(scene =>
        {
            var tiles = Tiles.CloneTiles(scene.Tiles);
            foreach (var row in tiles)
            {
                for (var x = 0; x < row.Count; x++)
                {
                    var tile = row[x];
                    // Storm damage rolls before growth (the storm hits overnight).
                    if (tile.Crop is not null && tile.Crop.Withered != true && (weatherDef?.CropDamageChance ?? 0) > 0)
                    {
                        if (rng.Float() < weatherDef!.CropDamageChance)
                        {
                            tile = tile with { Crop = null };
                        }
                    }
                    if (tile.Crop is not null)
                    {
                        ctx.Content.Crops.TryGetValue(tile.Crop.Type, out var definition);
                        var crop = tile.Crop;
                        if (crop.Withered != true && definition is not null)
                        {
                            if (crop.Watered)
                            {
                                crop = crop with { DaysGrown = (crop.DaysGrown ?? 0) + 1, DaysWithoutWater = 0 };
                            }
                            else
                            {
                                crop = crop with { DaysWithoutWater = crop.DaysWithoutWater + 1 };
                            }
                            crop = crop with { Watered = false };
                            crop = crop with { Stage = Crops.ComputeCropStage(crop, definition) };
                            // Season check happens against the NEW season.
                            if (!definition.Seasons.Contains(newSeason))
                            {
                                crop = crop with { Withered = true };
                            }
                        }
                        tile = tile with { Crop = crop };
                    }

                    // Unfertilized watered soil dries out overnight — unless the new
                    // day's weather waters it (rain/storm, M4).
                    if (weatherDef is { WatersOutdoorSoil: true } && tile.Background == TileTypes.Soil)
                    {
                        tile = tile with
                        {
                            SoilMoisture = 100,
                            SoilState = tile.SoilState == SoilStates.Fertilized ? SoilStates.Fertilized : SoilStates.Watered,
                        };
                        if (tile.Crop is not null && tile.Crop.Withered != true)
                        {
                            tile = tile with { Crop = tile.Crop with { Watered = true, LastWateredDay = newDay, DaysWithoutWater = 0 } };
                        }
                    }
                    else
                    {
                        tile = tile with { SoilMoisture = 0 };
                        if (tile.SoilState == SoilStates.Watered)
                        {
                            tile = tile with { SoilState = SoilStates.Dry };
                        }
                    }

                    // Node respawns
                    if (tile.Node?.DepletedOnDay is double depletedOnDay)
                    {
                        nodeTypes.TryGetValue(tile.Node.TypeId, out var definition);
                        if (definition?.RespawnDays is double respawnDays && newDay - depletedOnDay >= respawnDays)
                        {
                            tile = tile with { Node = new TileNode { TypeId = tile.Node.TypeId, RemainingHealth = definition.Health } };
                        }
                    }
                    row[x] = tile;
                }
            }
            return scene with { Tiles = tiles };
        }).ToList();

        // Energy restore / collapse penalty
        var maxEnergy = state.Player.MaxEnergy;
        var money = state.Player.Money;
        if (options.Collapsed)
        {
            var penalty = Math.Min(money, settings.CollapseMoneyPenalty);
            money -= penalty;
            effects.Add(Effect.Message(MessageLevels.Error, $"You collapsed from exhaustion! Lost ${Js.Num(penalty)}."));
        }
        var energy = options.Collapsed
            ? Js.Round(maxEnergy * settings.CollapseEnergyFraction)
            : maxEnergy;

        var nextState = state with
        {
            Clock = state.Clock with
            {
                Day = newDay,
                Season = newSeason,
                Year = newYear,
                TimeMinutes = settings.Time.DayStartMinute,
                WeatherId = newWeatherId,
            },
            World = state.World with { Scenes = scenes },
            Player = state.Player with { Energy = energy, Money = money },
            Rng = rng.State,
            Dialogue = null,
            Shop = null,
            ShopPurchasesToday = [],
        };

        // Animals: age, mood, product rolls (M4c). Machines finish overnight jobs
        // automatically since completion is an absolute-minute comparison (M4a).
        nextState = Animals.AdvanceAnimalsNightly(ctx, nextState);
        nextState = Crafting.SettleMachines(ctx, nextState);

        if (weatherDef is not null && newWeatherId != state.Clock.WeatherId)
        {
            effects.Add(Effect.Message(MessageLevels.Info, $"Weather: {weatherDef.Name}"));
        }

        if (newSeason != previousSeason)
        {
            var newSeasonName = seasons.Find(season => season.Id == newSeason)?.Name ?? newSeason;
            effects.Add(Effect.Message(MessageLevels.Info, $"{newSeasonName} has arrived!"));
            ctx.Hooks?.Emit(HookNames.OnSeasonChange, new SeasonChangeHookPayload(newSeason, previousSeason, newYear));
        }
        if (newYear != previousYear)
        {
            effects.Add(Effect.Message(MessageLevels.Info, $"Year {Js.Num(newYear)} begins!"));
            ctx.Hooks?.Emit(HookNames.OnYearStart, new YearStartHookPayload(newYear));
        }

        effects.Add(new DayStartedEffect(newDay, newSeason, newYear));
        effects.Add(Effect.Message(MessageLevels.Success, $"Day {Js.Num(DayOfSeason(calendar, newDay))} of {newSeason}, Year {Js.Num(newYear)}"));

        var festival = FestivalOnDay(calendar, newDay);
        if (festival is not null)
        {
            effects.Add(Effect.Message(MessageLevels.Info, $"Today is the {festival.Name}!"));
        }

        ctx.Hooks?.Emit(HookNames.OnDayStart, new DayHookPayload(newDay, newSeason, newYear));

        return new EngineStep(nextState, effects);
    }

    /// <summary>
    /// TS <c>(response as { weatherId?: unknown } | null)?.weatherId</c>, then
    /// <c>typeof override === 'string'</c>. C# listeners return <c>object?</c>, so
    /// accept the shapes a listener can plausibly produce: a JSON object, a
    /// string-keyed dictionary, or any object with a string <c>WeatherId</c>/<c>weatherId</c>
    /// property (e.g. a <see cref="WeatherRollHookPayload"/>).
    /// </summary>
    private static string? ReadWeatherIdOverride(object? response)
    {
        switch (response)
        {
            case null:
                return null;
            case JsonElement { ValueKind: JsonValueKind.Object } element:
                return element.TryGetProperty("weatherId", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            case JsonElement:
                return null;
            case string:
                return null;
            case IDictionary<string, object?> dictionary:
                return dictionary.TryGetValue("weatherId", out var fromDictionary) ? AsString(fromDictionary) : null;
            case IReadOnlyDictionary<string, object?> readOnly:
                return readOnly.TryGetValue("weatherId", out var fromReadOnly) ? AsString(fromReadOnly) : null;
            case IDictionary legacy:
                return legacy.Contains("weatherId") ? AsString(legacy["weatherId"]) : null;
        }
        var property = response.GetType().GetProperty("WeatherId", BindingFlags.Public | BindingFlags.Instance)
            ?? response.GetType().GetProperty("weatherId", BindingFlags.Public | BindingFlags.Instance);
        return property is not null ? AsString(property.GetValue(response)) : null;
    }

    private static string? AsString(object? value) => value switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        _ => null,
    };
}
