using FarmEngine.Core;
using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core.Tests.Core;

/// <summary>
/// Port of engine-core/src/m9-calendar.test.ts — M9 calendar tests:
/// creator-configurable seasons + festival days.
/// </summary>
public class M9CalendarTests
{
    private const string NeedsEngine = "needs EngineState/Engine/EngineTests.MakeProject (+ Crafting, Tiles, Inventory, …) — enable at integration";

    private static readonly CalendarConfig CustomCalendar = new()
    {
        Seasons =
        [
            new CalendarSeason { Id = "a", Name = "Alpha", Days = 10 },
            new CalendarSeason { Id = "b", Name = "Beta", Days = 10 },
            new CalendarSeason { Id = "c", Name = "Gamma", Days = 10 },
        ],
        Festivals = [],
    };

    private static readonly CalendarConfig CalendarWithFestival = new()
    {
        Seasons = [new CalendarSeason { Id = "a", Name = "Alpha", Days = 10 }],
        Festivals = [new CalendarFestival { Id = "harvest-fest", Name = "Harvest Festival", SeasonId = "a", Day = 5 }],
    };

    private sealed record TestEngine(EngineContext Ctx, GameState State, GameProject Project);

    private static TestEngine MakeEngine(Func<GameProject, GameProject>? mutate = null)
    {
        var project = EngineTests.MakeProject();
        if (mutate is not null) project = mutate(project);
        var ctx = new EngineContext(EngineState.CreateContentFromProject(project));
        var state = EngineState.CreateGameState(project, new EngineState.CreateGameStateOptions(Seed: "m9"));
        return new TestEngine(ctx, state, project);
    }

    private static Func<GameProject, GameProject> WithCustomCalendar(CalendarConfig calendar, string? currentSeason = null) =>
        project => project with
        {
            Settings = project.Settings with { Calendar = calendar },
            CurrentSeason = currentSeason ?? calendar.Seasons[0].Id,
            CurrentDay = 1,
            CurrentYear = 1,
        };

    private static bool HasMessage(List<Effect> effects, Func<string, bool> predicate) =>
        effects.Any(e => e is MessageEffect message && predicate(message.Text));

    // --- calendar math (pure functions) ---

    [Fact]
    public void ResolvesDayOfSeasonSeasonYearAcrossUnevenAndEvenCustomCalendars()
    {
        Assert.Equal(["a", "b", "c"], GameTime.CalendarSeasons(CustomCalendar).Select(s => s.Id));
        Assert.Equal(1, GameTime.DayOfSeason(CustomCalendar, 1));
        Assert.Equal(10, GameTime.DayOfSeason(CustomCalendar, 10));
        Assert.Equal(1, GameTime.DayOfSeason(CustomCalendar, 11));
        Assert.Equal("a", GameTime.SeasonForDay(CustomCalendar, 1));
        Assert.Equal("a", GameTime.SeasonForDay(CustomCalendar, 10));
        Assert.Equal("b", GameTime.SeasonForDay(CustomCalendar, 11));
        Assert.Equal("c", GameTime.SeasonForDay(CustomCalendar, 21));
        Assert.Equal("c", GameTime.SeasonForDay(CustomCalendar, 30));
        Assert.Equal("a", GameTime.SeasonForDay(CustomCalendar, 31));
        Assert.Equal(1, GameTime.YearForDay(CustomCalendar, 30));
        Assert.Equal(2, GameTime.YearForDay(CustomCalendar, 31));
    }

    [Fact]
    public void HonorsEachSeasonsOwnLengthForHeterogeneousCalendars()
    {
        var uneven = new CalendarConfig
        {
            Seasons =
            [
                new CalendarSeason { Id = "short", Name = "Short", Days = 5 },
                new CalendarSeason { Id = "long", Name = "Long", Days = 20 },
            ],
            Festivals = [],
        };
        Assert.Equal(5, GameTime.DayOfSeason(uneven, 5));
        Assert.Equal("short", GameTime.SeasonForDay(uneven, 5));
        Assert.Equal(1, GameTime.DayOfSeason(uneven, 6));
        Assert.Equal("long", GameTime.SeasonForDay(uneven, 6));
        Assert.Equal(20, GameTime.DayOfSeason(uneven, 25));
        Assert.Equal("long", GameTime.SeasonForDay(uneven, 25));
        Assert.Equal("short", GameTime.SeasonForDay(uneven, 26)); // year wraps: 5 + 20 = 25 days/year
        Assert.Equal(1, GameTime.YearForDay(uneven, 25));
        Assert.Equal(2, GameTime.YearForDay(uneven, 26));
    }

    [Fact]
    public void FallsBackToTheClassicFourSeasonCalendarWhenSeasonsIsEmptyOrDegenerate()
    {
        var empty = new CalendarConfig { Seasons = [], Festivals = [] };
        Assert.Equal(["spring", "summer", "fall", "winter"], GameTime.CalendarSeasons(empty).Select(s => s.Id));
        Assert.Equal("spring", GameTime.SeasonForDay(empty, 1));
        Assert.Equal("summer", GameTime.SeasonForDay(empty, 29));

        var allZero = new CalendarConfig { Seasons = [new CalendarSeason { Id = "x", Name = "X", Days = 0 }], Festivals = [] };
        Assert.Equal(["spring", "summer", "fall", "winter"], GameTime.CalendarSeasons(allZero).Select(s => s.Id));
    }

    [Fact]
    public void SeasonByIdLooksUpTheEffectiveFallbackSafeSeasonList()
    {
        Assert.Equal("Beta", GameTime.SeasonById(CustomCalendar, "b")?.Name);
        Assert.Null(GameTime.SeasonById(CustomCalendar, "nope"));
        Assert.Equal("Spring", GameTime.SeasonById(new CalendarConfig { Seasons = [], Festivals = [] }, "spring")?.Name);
    }

    // --- custom calendar day/season/year rollover via performSleep ---

    [Fact]
    public void RollsSeasonAndYearCorrectlyAcrossA3x10DayCalendar()
    {
        var (ctx, state, _) = MakeEngine(WithCustomCalendar(CustomCalendar));
        Assert.Equal(1, state.Clock.Day);
        Assert.Equal("a", state.Clock.Season);
        Assert.Equal(1, state.Clock.Year);

        // Sleep 9 times: day 1 → day 10, season stays 'a'.
        var current = state;
        for (var i = 0; i < 9; i++)
        {
            current = GameTime.PerformSleep(ctx, current).State;
        }
        Assert.Equal(10, current.Clock.Day);
        Assert.Equal("a", current.Clock.Season);
        Assert.Equal(10, GameTime.DayOfSeason(CustomCalendar, current.Clock.Day));

        // Sleep once more: day 10 → 11 crosses into season 'b'.
        var rolled = GameTime.PerformSleep(ctx, current);
        Assert.Equal(11, rolled.State.Clock.Day);
        Assert.Equal("b", rolled.State.Clock.Season);
        Assert.Equal(1, rolled.State.Clock.Year);
        Assert.True(HasMessage(rolled.Effects, text => text.Contains("Beta has arrived")));

        // Sleep through 'b' and 'c' (19 more days) to complete the year: day 11 → day 30.
        current = rolled.State;
        for (var i = 0; i < 19; i++)
        {
            current = GameTime.PerformSleep(ctx, current).State;
        }
        Assert.Equal(30, current.Clock.Day);
        Assert.Equal("c", current.Clock.Season);
        Assert.Equal(1, current.Clock.Year);

        // One more sleep wraps back to season 'a' and rolls the year.
        var yearRolled = GameTime.PerformSleep(ctx, current);
        Assert.Equal(31, yearRolled.State.Clock.Day);
        Assert.Equal("a", yearRolled.State.Clock.Season);
        Assert.Equal(2, yearRolled.State.Clock.Year);
        Assert.True(HasMessage(yearRolled.Effects, text => text.Contains("Year 2 begins")));
    }

    [Fact]
    public void FormatsDayXOfSeasonUsingTheCalendarsDayOfSeasonCount()
    {
        var (ctx, state, _) = MakeEngine(WithCustomCalendar(CustomCalendar));
        var step = GameTime.PerformSleep(ctx, state);
        Assert.True(HasMessage(step.Effects, text => text == "Day 2 of a, Year 1"));
    }

    // --- festivals ---

    [Fact]
    public void AnnouncesTheFestivalOnlyOnItsConfiguredDay()
    {
        var (ctx, state, _) = MakeEngine(WithCustomCalendar(CalendarWithFestival));
        // Day 1 → 2: not the festival.
        var notYet = GameTime.PerformSleep(ctx, state);
        Assert.False(HasMessage(notYet.Effects, text => text.Contains("Harvest Festival")));

        // Advance to day 4, then sleep into day 5 (the festival).
        var current = notYet.State;
        for (var i = 0; i < 2; i++) current = GameTime.PerformSleep(ctx, current).State;
        Assert.Equal(4, current.Clock.Day);
        var festivalDay = GameTime.PerformSleep(ctx, current);
        Assert.Equal(5, festivalDay.State.Clock.Day);
        Assert.True(HasMessage(festivalDay.Effects, text => text == "Today is the Harvest Festival!"));

        // The day after is quiet again.
        var after = GameTime.PerformSleep(ctx, festivalDay.State);
        Assert.False(HasMessage(after.Effects, text => text.Contains("Harvest Festival")));
    }

    [Fact]
    public void FestivalOnDayResolvesTheFestivalForItsDayOnly()
    {
        Assert.Equal("harvest-fest", GameTime.FestivalOnDay(CalendarWithFestival, 5)?.Id);
        Assert.Null(GameTime.FestivalOnDay(CalendarWithFestival, 4));
        Assert.Equal("harvest-fest", GameTime.FestivalOnDay(CalendarWithFestival, 15)?.Id); // next year, same day-of-season
    }

    [Fact]
    public void TheFestivalIdEventConditionIsTrueOnlyOnTheFestivalDay()
    {
        var festivalEvent = new GameEvent
        {
            Id = "evt-festival",
            Name = "Festival banner",
            SceneId = "",
            Trigger = "tick",
            Conditions = [new FestivalIdCondition { FestivalId = "harvest-fest" }],
            Outcomes = [new EventOutcome { Type = EventOutcomeTypes.SetFlag, FlagName = "festival-seen" }],
            Active = true,
            Repeatable = true,
        };
        var (ctx, state, _) = MakeEngine(project => WithCustomCalendar(CalendarWithFestival)(project) with { Events = [festivalEvent] });

        // Not the festival day yet: the tick event does not fire.
        var early = Engine.AdvanceTick(ctx, state, 20);
        Assert.False(early.State.Flags.ContainsKey("festival-seen"));

        // Sleep to the festival day (day 5), then tick.
        var current = state;
        for (var i = 0; i < 4; i++) current = GameTime.PerformSleep(ctx, current).State;
        Assert.Equal(5, current.Clock.Day);
        var onFestival = Engine.AdvanceTick(ctx, current, 20);
        Assert.True(onFestival.State.Flags.TryGetValue("festival-seen", out var seen) && seen.ValueKind == System.Text.Json.JsonValueKind.True);

        // The next day, the condition is false again.
        var nextDay = GameTime.PerformSleep(ctx, onFestival.State).State;
        var afterFestival = Engine.AdvanceTick(ctx, nextDay with { Flags = [] }, 20);
        Assert.False(afterFestival.State.Flags.ContainsKey("festival-seen"));
    }

    // --- weather fallback for custom seasons ---
    // The TS tests build ctx via makeProject + createContentFromProject, which only
    // copies project.weather into content.weather; these build that content directly
    // so the weather lookup is covered before integration.

    private static EngineContext ContextWithWeather(WeatherConfig weather) =>
        new(new GameContent { Settings = new ProjectSettings { Calendar = CustomCalendar }, Weather = weather });

    [Fact]
    public void FallsBackToAnotherConfiguredTableWhenACustomSeasonHasNoneOfItsOwn()
    {
        var ctx = ContextWithWeather(new WeatherConfig
        {
            Types = [new WeatherTypeDefinition { Id = "sun", Name = "Sunny", WatersOutdoorSoil = false, CropDamageChance = 0, NpcsStayInside = false, Overlay = null }],
            // Only season 'a' gets an explicit table; 'b' and 'c' are untouched.
            Table = new() { ["a"] = [new WeatherTableEntry { WeatherId = "sun", Weight = 1 }] },
        });
        Assert.Equal([new WeatherTableEntry { WeatherId = "sun", Weight = 1 }], Weather.WeatherTableForSeason(ctx, "a"));
        // 'b' has no table of its own — falls back to 'a's table rather than an empty roll.
        Assert.Equal([new WeatherTableEntry { WeatherId = "sun", Weight = 1 }], Weather.WeatherTableForSeason(ctx, "b"));
    }

    [Fact]
    public void ReturnsAnEmptyTableWhenNothingIsConfiguredAtAll()
    {
        var ctx = ContextWithWeather(new WeatherConfig { Types = [], Table = [] });
        Assert.Empty(Weather.WeatherTableForSeason(ctx, "a"));
    }

    // --- crops with custom season ids ---

    private static readonly CustomCropDefinition CustomCrop = new()
    {
        Id = "moonflower", Name = "Moonflower", SeedCost = 10, BaseHarvestValue = 20,
        GrowthTime = 15000, GrowthDays = 3, Stages = 4, Seasons = ["a"], CanRegrow = false,
        YieldMin = 1, YieldMax = 2, MutationChance = 0.01,
    };

    private static readonly Item SeedItem = new()
    {
        Id = "seed-moonflower", Name = "Moonflower Seeds", Description = "A custom-season seed",
        Type = "seed", Stackable = true, MaxStack = 99, Value = 10, CropType = "moonflower",
    };

    [Fact]
    public void CanGrowInSeasonAcceptsCustomSeasonIdsDirectly()
    {
        var definition = Crops.ToCropDefinition(CustomCrop);
        Assert.True(Crops.CanGrowInSeason(definition, "a"));
        Assert.False(Crops.CanGrowInSeason(definition, "b"));
    }

    [Fact]
    public void GrowsWhileItsSeasonIsCurrentAndWithersOnceTheCalendarRollsOutOfIt()
    {
        var (ctx, state, _) = MakeEngine(project => WithCustomCalendar(CustomCalendar)(project) with
        {
            CustomCrops = [CustomCrop],
            Player = project.Player with
            {
                X = 3, Y = 3, Direction = "up",
                Inventory =
                [
                    new InventorySlot { Item = SeedItem, Quantity = 1 },
                    new InventorySlot { Item = project.Items.Find(i => i.Id == "tool-watering-can")!, Quantity = 1 },
                ],
            },
        });

        // Plant on the pre-tilled soil tile (facing up from (3,3) → (3,2)).
        var planted = Engine.ApplyCommand(ctx, state, new InteractCommand());
        var tile = planted.State.World.Scenes[0].Tiles[2][3];
        Assert.Equal("moonflower", tile.Crop?.Type);
        Assert.NotEqual(true, tile.Crop?.Withered);

        // Water + sleep for two days, still within season 'a' (10 days long) — grows, doesn't wither.
        var current = planted.State;
        for (var i = 0; i < 2; i++)
        {
            current = Engine.ApplyCommand(ctx, current, new UseToolCommand("watering-can")).State;
            current = Engine.ApplyCommand(ctx, current, new SleepCommand()).State;
        }
        Assert.NotEqual(true, current.World.Scenes[0].Tiles[2][3].Crop?.Withered);
        Assert.Equal(2, current.World.Scenes[0].Tiles[2][3].Crop?.DaysGrown);

        // Jump to the last day of season 'a' and sleep past the boundary into 'b'.
        current = current with { Clock = current.Clock with { Day = 10 } };
        var rolled = Engine.ApplyCommand(ctx, current, new SleepCommand());
        Assert.Equal("b", rolled.State.Clock.Season);
        Assert.Equal(true, rolled.State.World.Scenes[0].Tiles[2][3].Crop?.Withered);
    }
}
