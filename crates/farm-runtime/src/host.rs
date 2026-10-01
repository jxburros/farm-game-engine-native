//! Host-neutral views for the desktop bridge and standalone player. The host supplies raw
//! input/time and cosmetic randomness; gameplay bindings and minigame scoring stay in Rust.

use crate::input::{self, InputManager, Modifiers, MoveVector};
use crate::minigames::{
    self, custom_game::SimpleBattleSession, MinigameMountOptions, MinigameSession, TimingBarSession,
};
use crate::timestep::FixedTimestep;
use farm_sim::{schema::MinigameDef, GameState};
use serde::{Deserialize, Serialize};

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CalendarView {
    pub season_name: Option<String>,
    pub season_days: u32,
    pub day_of_season: u32,
    pub time_text: String,
    pub seasons: Vec<farm_sim::schema::CalendarSeason>,
}

pub fn calendar_view(content: &farm_sim::GameContent, state: &GameState) -> CalendarView {
    let calendar = &content.settings.calendar;
    // The clock's own season and day (#23); a season the calendar lacks shows no name.
    let season = farm_sim::game_time::season_by_id(calendar, &state.clock.season);
    let date = farm_sim::game_time::clock_date(calendar, &state.clock);
    CalendarView {
        season_name: season.map(|s| s.name.clone()),
        season_days: date.season.days,
        day_of_season: date.day_of_season,
        time_text: farm_sim::game_time::format_time_of_day(state.clock.time_minutes),
        seasons: farm_sim::game_time::calendar_seasons(calendar),
    }
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InputFrame {
    #[serde(default)]
    pub keys_down: Vec<String>,
    #[serde(default)]
    pub keys_just_pressed: Vec<String>,
}

impl InputFrame {
    pub fn tracker(&self) -> InputManager {
        let mut input = InputManager::new();
        for key in &self.keys_down {
            input.key_down(key, Modifiers::NONE);
        }
        input.end_frame();
        for key in &self.keys_just_pressed {
            input.key_down(key, Modifiers::NONE);
            if !self.keys_down.iter().any(|held| held.eq_ignore_ascii_case(key)) {
                input.key_up(key);
            }
        }
        input
    }
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PreparedFrame {
    pub ticks: u32,
    pub alpha: f64,
    pub dx: f64,
    pub dy: f64,
}

pub fn prepare_frame(
    timestep: &mut FixedTimestep,
    input: &InputFrame,
    state: &GameState,
    delta_seconds: f64,
    host_modal_open: bool,
) -> Result<PreparedFrame, String> {
    if !delta_seconds.is_finite() {
        return Err("Frame time must be finite.".to_owned());
    }
    let intent =
        if host_modal_open { MoveVector::default() } else { input::move_intent(&input.tracker(), state, None) };
    let ticks = timestep.advance(delta_seconds);
    Ok(PreparedFrame { ticks, alpha: timestep.alpha(), dx: intent.dx, dy: intent.dy })
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MinigameView {
    pub kind: String,
    pub prompt: String,
    pub button_text: String,
    pub is_done: bool,
    pub score: Option<f64>,
    pub position: f64,
    pub target_left: f64,
    pub target_size: f64,
    pub status: String,
    pub log: String,
    pub choices: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub enum MinigameInput {
    Update { seconds: f64 },
    Press,
    Release,
    Act { choice: String },
}

#[derive(Debug)]
pub struct HostedMinigame {
    session: Box<dyn MinigameSession>,
}

impl HostedMinigame {
    pub fn mount(definition: Option<&MinigameDef>, random: f64) -> Result<Self, String> {
        if !random.is_finite() || !(0.0..1.0).contains(&random) {
            return Err("Cosmetic random value must be in [0, 1).".to_owned());
        }
        let registry = minigames::create_default_minigame_registry();
        let implementation = minigames::minigame_impl_for(&registry, definition);
        let options =
            MinigameMountOptions::new(definition.map(|def| def.config.clone()).unwrap_or_default(), |_| {}, || {})
                .with_random(move || random);
        Ok(Self { session: implementation.mount(options) })
    }

    pub fn apply(&mut self, input: MinigameInput) -> Result<MinigameView, String> {
        match input {
            MinigameInput::Update { seconds } => {
                if !seconds.is_finite() || seconds < 0.0 {
                    return Err("Minigame frame time must be finite and nonnegative.".to_owned());
                }
                self.session.update(seconds);
            }
            MinigameInput::Press => self.session.press(),
            MinigameInput::Release => self.session.release(),
            MinigameInput::Act { choice } => {
                let battle = self
                    .session
                    .as_any_mut()
                    .downcast_mut::<SimpleBattleSession>()
                    .ok_or("Only simple-battle accepts choices.")?;
                if !SimpleBattleSession::CHOICES.contains(&choice.as_str()) {
                    return Err("Unknown battle choice.".to_owned());
                }
                battle.act(&choice);
            }
        }
        Ok(self.view())
    }

    pub fn view(&self) -> MinigameView {
        let bar = self.session.as_any().downcast_ref::<TimingBarSession>();
        let battle = self.session.as_any().downcast_ref::<SimpleBattleSession>();
        MinigameView {
            kind: self.session.kind().to_owned(),
            prompt: self.session.prompt(),
            button_text: self.session.button_text().to_owned(),
            is_done: self.session.is_done(),
            score: self.session.score(),
            position: bar.map_or(0.0, TimingBarSession::position),
            target_left: bar.map_or(0.0, TimingBarSession::target_left),
            target_size: bar.map_or(0.0, TimingBarSession::target_size),
            status: battle.map_or_else(String::new, SimpleBattleSession::status),
            log: battle.map_or_else(String::new, |battle| battle.log().to_owned()),
            choices: if battle.is_some() {
                SimpleBattleSession::CHOICES.iter().map(|s| (*s).to_owned()).collect()
            } else {
                Vec::new()
            },
        }
    }
}

impl Drop for HostedMinigame {
    fn drop(&mut self) {
        self.session.dispose();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn input_snapshot_preserves_taps_without_repeating_held_presses() {
        let frame = InputFrame { keys_down: vec!["W".into()], keys_just_pressed: vec!["e".into()] };
        let input = frame.tracker();
        assert!(input.pressed("w"));
        assert!(!input.just_pressed("w"));
        assert!(input.pressed("e"));
        assert!(input.just_pressed("e"));
        assert!(!input.keys_down().contains("e"));
    }

    #[test]
    fn invalid_runtime_input_is_rejected_without_changing_the_view() {
        for random in [f64::NAN, f64::INFINITY, -0.1, 1.0] {
            assert!(HostedMinigame::mount(None, random).is_err());
        }
        let definition = MinigameDef { kind: "timing-bar".into(), ..Default::default() };
        let mut game = HostedMinigame::mount(Some(&definition), 0.5).unwrap();
        let before = game.view();
        for seconds in [f64::NAN, f64::INFINITY, -1.0] {
            assert!(game.apply(MinigameInput::Update { seconds }).is_err());
            assert_eq!(game.view(), before);
        }
        assert!(game.apply(MinigameInput::Act { choice: "attack".into() }).is_err());
        assert_eq!(game.view(), before);
    }

    #[test]
    fn completion_is_stable_under_repeated_input() {
        let mut game = HostedMinigame::mount(None, 0.5).unwrap();
        let completed = game.apply(MinigameInput::Press).unwrap();
        assert_eq!(completed.score, Some(0.5));
        assert!(completed.is_done);
        assert_eq!(game.apply(MinigameInput::Press).unwrap(), completed);
        assert_eq!(game.apply(MinigameInput::Release).unwrap(), completed);
    }
}
