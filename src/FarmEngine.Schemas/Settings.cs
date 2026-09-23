namespace FarmEngine.Schemas;

// Port of packages/engine-schemas/src/settings.ts.
//
// Per-project gameplay settings (vision.md: "light vs sim" — heavy systems
// are toggleable so cozy configs stay cozy).

public sealed record CalendarSeason
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>In-game days this season lasts. int, positive.</summary>
    public double Days { get; init; }
}

public sealed record CalendarFestival
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Season this festival falls in (matches a CalendarSeason id).</summary>
    public string SeasonId { get; init; } = "";
    /// <summary>1-based day within that season. int, positive.</summary>
    public double Day { get; init; }
}

public sealed record CalendarConfig
{
    /// <summary>Ordered list of seasons; the calendar year is the sum of their lengths.</summary>
    public List<CalendarSeason> Seasons { get; init; } = SettingsSchema.ClassicCalendarSeasons();
    /// <summary>Named festival days, each pinned to a season + day-of-season.</summary>
    public List<CalendarFestival> Festivals { get; init; } = [];
}

public sealed record TimeConfig
{
    /// <summary>Minute-of-day the player wakes up (6:00). int.</summary>
    public double DayStartMinute { get; init; } = 6 * 60;
    /// <summary>Minute-of-day the player collapses if still awake (26:00 = 2am). int.</summary>
    public double DayEndMinute { get; init; } = 26 * 60;
    /// <summary>In-game minutes that pass per real-time second. positive.</summary>
    public double MinutesPerRealSecond { get; init; } = 1;
}

public sealed record MovementConfig
{
    /// <summary>Player walk speed in tiles per second (free movement). positive.</summary>
    public double PlayerSpeed { get; init; } = 4.5;
}

public sealed record ProjectSettings
{
    public MovementConfig Movement { get; init; } = new();
    public bool EnergyEnabled { get; init; } = true;
    /// <summary>positive.</summary>
    public double MaxEnergy { get; init; } = 100;
    /// <summary>Fraction of energy restored after a collapse (vs full sleep). 0..1.</summary>
    public double CollapseEnergyFraction { get; init; } = 0.5;
    /// <summary>Money penalty charged on collapse. nonnegative.</summary>
    public double CollapseMoneyPenalty { get; init; } = 50;
    public TimeConfig Time { get; init; } = new();
    /// <summary>Creator-configurable calendar (M9): seasons, their lengths, and festival days.</summary>
    public CalendarConfig Calendar { get; init; } = new();
    /// <summary>Player skills (M4g): XP per action category, levels unlock recipes.</summary>
    public bool SkillsEnabled { get; init; } = true;
    /// <summary>XP thresholds per level (index = level).</summary>
    public List<double> SkillLevelCurve { get; init; } = [0, 50, 150, 300, 500, 750, 1050, 1400, 1800, 2250];
    /// <summary>Game-text locale (M7 i18n): packs may carry per-locale string tables.</summary>
    public string Locale { get; init; } = "en";
    /// <summary>Optional, creator-controlled credit shown in exported games.</summary>
    public bool ShowMadeWithCredit { get; init; } = false;
}

public static class SettingsSchema
{
    /// <summary>The classic four-season, 28-day-per-season calendar (today's hardcoded default).</summary>
    public static List<CalendarSeason> ClassicCalendarSeasons() =>
        PrimitivesSchema.ClassicSeasons
            .Select(id => new CalendarSeason { Id = id, Name = char.ToUpperInvariant(id[0]) + id[1..], Days = 28 })
            .ToList();

    /// <summary>TS <c>DEFAULT_PROJECT_SETTINGS = ProjectSettingsSchema.parse({})</c>.</summary>
    public static readonly ProjectSettings DefaultProjectSettings = new();
}
