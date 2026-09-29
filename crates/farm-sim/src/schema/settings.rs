//! Port of `Settings.cs` (packages/engine-schemas/src/settings.ts).
//!
//! Per-project gameplay settings (vision.md: "light vs sim" — heavy systems are toggleable so
//! cozy configs stay cozy).

use super::primitives::CLASSIC_SEASONS;
use crate::units;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CalendarSeason {
    pub id: String,
    pub name: String,
    /// In-game days this season lasts. int, positive.
    #[serde(with = "crate::units::count")]
    pub days: u32,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CalendarFestival {
    pub id: String,
    pub name: String,
    /// Season this festival falls in (matches a CalendarSeason id).
    pub season_id: String,
    /// 1-based day within that season. int, positive.
    #[serde(with = "crate::units::count")]
    pub day: u32,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CalendarConfig {
    /// Ordered list of seasons; the calendar year is the sum of their lengths.
    pub seasons: Vec<CalendarSeason>,
    /// Named festival days, each pinned to a season + day-of-season.
    pub festivals: Vec<CalendarFestival>,
}

impl Default for CalendarConfig {
    fn default() -> Self {
        Self { seasons: classic_calendar_seasons(), festivals: Vec::new() }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct TimeConfig {
    /// Minute-of-day the player wakes up (6:00). int.
    #[serde(with = "crate::units::count")]
    pub day_start_minute: u32,
    /// Minute-of-day the player collapses if still awake (26:00 = 2am). int.
    #[serde(with = "crate::units::count")]
    pub day_end_minute: u32,
    /// In-game minutes that pass per real-time second. positive.
    #[serde(with = "crate::units::minute_rate")]
    pub minutes_per_real_second: u32,
}

impl Default for TimeConfig {
    fn default() -> Self {
        Self {
            day_start_minute: 6 * 60,
            day_end_minute: 26 * 60,
            minutes_per_real_second: units::MINUTE / units::TICKS_PER_SECOND,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct MovementConfig {
    /// Player walk speed in tiles per second (free movement). positive.
    #[serde(with = "crate::units::speed")]
    pub player_speed: i32,
}

impl Default for MovementConfig {
    fn default() -> Self {
        Self { player_speed: units::from_authoring::<units::Speed>(4.5) }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ProjectSettings {
    pub movement: MovementConfig,
    pub energy_enabled: bool,
    /// positive.
    #[serde(with = "crate::units::energy")]
    pub max_energy: i32,
    /// Fraction of energy restored after a collapse (vs full sleep). 0..1.
    #[serde(with = "crate::units::milli")]
    pub collapse_energy_fraction: u32,
    /// Money penalty charged on collapse. nonnegative.
    #[serde(with = "crate::units::money")]
    pub collapse_money_penalty: i64,
    pub time: TimeConfig,
    /// Creator-configurable calendar (M9): seasons, their lengths, and festival days.
    pub calendar: CalendarConfig,
    /// Player skills (M4g): XP per action category, levels unlock recipes.
    pub skills_enabled: bool,
    /// XP thresholds per level (index = level).
    #[serde(with = "crate::units::count::vec")]
    pub skill_level_curve: Vec<u32>,
    /// Game-text locale (M7 i18n): packs may carry per-locale string tables.
    pub locale: String,
    /// Optional, creator-controlled credit shown in exported games.
    pub show_made_with_credit: bool,
}

impl Default for ProjectSettings {
    fn default() -> Self {
        Self {
            movement: MovementConfig::default(),
            energy_enabled: true,
            max_energy: units::points(100),
            collapse_energy_fraction: 500,
            collapse_money_penalty: 50,
            time: TimeConfig::default(),
            calendar: CalendarConfig::default(),
            skills_enabled: true,
            skill_level_curve: vec![0, 50, 150, 300, 500, 750, 1050, 1400, 1800, 2250],
            locale: "en".to_owned(),
            show_made_with_credit: false,
        }
    }
}

/// The classic four-season, 28-day-per-season calendar (today's hardcoded default).
pub fn classic_calendar_seasons() -> Vec<CalendarSeason> {
    CLASSIC_SEASONS
        .iter()
        .map(|id| {
            let mut chars = id.chars();
            let name = match chars.next() {
                Some(first) => first.to_uppercase().chain(chars).collect(),
                None => String::new(),
            };
            CalendarSeason { id: (*id).to_owned(), name, days: 28 }
        })
        .collect()
}

/// TS `DEFAULT_PROJECT_SETTINGS = ProjectSettingsSchema.parse({})`.
pub fn default_project_settings() -> ProjectSettings {
    ProjectSettings::default()
}
