//! Game clock, calendar and the overnight pass (port of `GameTime.cs` / time.ts).

use crate::engine_types::{Effects, EngineContext};
use crate::schema::{CalendarConfig, CalendarFestival, CalendarSeason, GameState};

/// Day phases (TS `DayPhase` union).
pub mod day_phases {
    pub const MORNING: &str = "morning";
    pub const DAY: &str = "day";
    pub const EVENING: &str = "evening";
    pub const NIGHT: &str = "night";
}

/// TS `SleepOptions`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct SleepOptions {
    pub collapsed: bool,
}

pub fn calendar_seasons(calendar: &CalendarConfig) -> Vec<CalendarSeason> {
    let _ = calendar;
    todo!("port GameTime.CalendarSeasons")
}

pub fn season_by_id<'a>(calendar: &'a CalendarConfig, id: &str) -> Option<&'a CalendarSeason> {
    let _ = (calendar, id);
    todo!("port GameTime.SeasonById")
}

pub fn day_of_season(calendar: &CalendarConfig, absolute_day: f64) -> f64 {
    let _ = (calendar, absolute_day);
    todo!("port GameTime.DayOfSeason")
}

pub fn season_for_day(calendar: &CalendarConfig, absolute_day: f64) -> String {
    let _ = (calendar, absolute_day);
    todo!("port GameTime.SeasonForDay")
}

pub fn year_for_day(calendar: &CalendarConfig, absolute_day: f64) -> f64 {
    let _ = (calendar, absolute_day);
    todo!("port GameTime.YearForDay")
}

pub fn festival_on_day(calendar: &CalendarConfig, absolute_day: f64) -> Option<&CalendarFestival> {
    let _ = (calendar, absolute_day);
    todo!("port GameTime.FestivalOnDay")
}

pub fn format_time_of_day(time_minutes: f64) -> String {
    let _ = time_minutes;
    todo!("port GameTime.FormatTimeOfDay")
}

pub fn day_phase(time_minutes: f64) -> &'static str {
    let _ = time_minutes;
    todo!("port GameTime.DayPhase")
}

/// The overnight pass: advances the clock, weather, crops, animals, machines, NPC schedules.
pub fn perform_sleep(ctx: &EngineContext, state: &mut GameState, options: SleepOptions) -> Effects {
    let _ = (ctx, state, options);
    todo!("port GameTime.PerformSleep")
}
