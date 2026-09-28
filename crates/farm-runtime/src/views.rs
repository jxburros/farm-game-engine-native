//! Read models for the desktop HUD and creator panels. Game/calendar rules stay in Rust.
use crate::panels::{self, PanelState, PanelView};
use farm_sim::schema::{CalendarSeason, GameContent, GameProject, GameState};
use farm_sim::{content_builtin, game_time};
use serde::Serialize;

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RuntimeView {
    pub season_name: Option<String>,
    pub season_days: f64,
    pub day_of_season: f64,
    pub time_label: String,
    pub calendar_seasons: Vec<CalendarSeason>,
    pub panels: Vec<PanelView>,
}

pub fn runtime_view(
    project: &GameProject,
    content: &GameContent,
    state: &GameState,
    host_modal_open: bool,
) -> RuntimeView {
    let calendar = &content.settings.calendar;
    let season = game_time::season_by_id(calendar, &state.clock.season);
    RuntimeView {
        season_name: season.map(|s| s.name.clone()),
        season_days: season.map_or(content_builtin::DAYS_PER_SEASON, |s| s.days),
        day_of_season: game_time::day_of_season(calendar, state.clock.day),
        time_label: game_time::format_time_of_day(state.clock.time_minutes.floor()),
        calendar_seasons: game_time::calendar_seasons(calendar),
        panels: panels::render(
            project.game_panels.as_deref().unwrap_or(&[]),
            &PanelState::from_game_state(state, host_modal_open),
        ),
    }
}
