//! Port of `Settings.cs` (packages/engine-schemas/src/settings.ts).
//!
//! Per-project gameplay settings (vision.md: "light vs sim" — heavy systems are toggleable so
//! cozy configs stay cozy).

use super::primitives::CLASSIC_SEASONS;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CalendarSeason {
    pub id: String,
    pub name: String,
    /// In-game days this season lasts. int, positive.
    pub days: f64,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CalendarFestival {
    pub id: String,
    pub name: String,
    /// Season this festival falls in (matches a CalendarSeason id).
    pub season_id: String,
    /// 1-based day within that season. int, positive.
    pub day: f64,
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
    pub day_start_minute: f64,
    /// Minute-of-day the player collapses if still awake (26:00 = 2am). int.
    pub day_end_minute: f64,
    /// In-game minutes that pass per real-time second. positive.
    pub minutes_per_real_second: f64,
}

impl Default for TimeConfig {
    fn default() -> Self {
        Self { day_start_minute: 6.0 * 60.0, day_end_minute: 26.0 * 60.0, minutes_per_real_second: 1.0 }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct MovementConfig {
    /// Player walk speed in tiles per second (free movement). positive.
    pub player_speed: f64,
}

impl Default for MovementConfig {
    fn default() -> Self {
        Self { player_speed: 4.5 }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ProjectSettings {
    pub movement: MovementConfig,
    pub energy_enabled: bool,
    /// positive.
    pub max_energy: f64,
    /// Fraction of energy restored after a collapse (vs full sleep). 0..1.
    pub collapse_energy_fraction: f64,
    /// Money penalty charged on collapse. nonnegative.
    pub collapse_money_penalty: f64,
    pub time: TimeConfig,
    /// Creator-configurable calendar (M9): seasons, their lengths, and festival days.
    pub calendar: CalendarConfig,
    /// Player skills (M4g): XP per action category, levels unlock recipes.
    pub skills_enabled: bool,
    /// XP thresholds per level (index = level).
    pub skill_level_curve: Vec<f64>,
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
            max_energy: 100.0,
            collapse_energy_fraction: 0.5,
            collapse_money_penalty: 50.0,
            time: TimeConfig::default(),
            calendar: CalendarConfig::default(),
            skills_enabled: true,
            skill_level_curve: vec![0.0, 50.0, 150.0, 300.0, 500.0, 750.0, 1050.0, 1400.0, 1800.0, 2250.0],
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
            CalendarSeason { id: (*id).to_owned(), name, days: 28.0 }
        })
        .collect()
}

/// TS `DEFAULT_PROJECT_SETTINGS = ProjectSettingsSchema.parse({})`.
pub fn default_project_settings() -> ProjectSettings {
    ProjectSettings::default()
}
