//! Host-neutral views for the desktop bridge and standalone player. The host supplies raw
//! input/time and cosmetic randomness; gameplay bindings and minigame scoring stay in Rust.

use crate::input::{self, InputManager, Modifiers, MoveVector};
use crate::minigames::arcade::{MemorySequenceSession, MovingTargetSession, RhythmTapSession};
use crate::minigames::{
    self, custom_game::SimpleBattleSession, MinigameMountOptions, MinigameSession, TimingBarSession,
};
use crate::timestep::FixedTimestep;
use farm_sim::messages::Message;
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

/// What a running minigame shows. Texts are English (or the def's own); the `*_message` fields
/// carry the catalog messages they were made from, for players that translate them (they are not
/// serialized).
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MinigameView {
    pub kind: String,
    pub prompt: String,
    pub button_text: String,
    pub is_done: bool,
    pub score: Option<f64>,
    /// The moving marker on the track (timing-bar: the marker; moving-target: the target).
    pub position: f64,
    /// The zone on the track (timing-bar: the target zone; moving-target: the player's bar;
    /// rhythm-tap: the hit window).
    pub target_left: f64,
    pub target_size: f64,
    pub status: String,
    pub log: String,
    /// Choice buttons (simple-battle, memory-sequence); empty when the kind uses the primary
    /// button.
    pub choices: Vec<String>,
    /// rhythm-tap: the notes still to play, as track positions.
    pub notes: Vec<f64>,
    /// A score meter in [0, 1] (rhythm-tap: earned so far; moving-target: time over the target).
    pub meter: f64,
    /// How far through the game, in [0, 1] (0 for kinds without an end time).
    pub progress: f64,
    /// memory-sequence: the symbol shown now (empty between symbols and after the showing).
    pub highlight: String,
    #[serde(skip)]
    pub prompt_message: Option<Message>,
    #[serde(skip)]
    pub button_message: Option<Message>,
    #[serde(skip)]
    pub status_message: Option<Message>,
    #[serde(skip)]
    pub log_message: Option<Message>,
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
                let choices = self.session.choices();
                if choices.is_empty() {
                    return Err("This minigame has no choices.".to_owned());
                }
                if !choices.contains(&choice.as_str()) {
                    return Err("Unknown choice.".to_owned());
                }
                self.session.act(&choice);
            }
        }
        Ok(self.view())
    }

    pub fn view(&self) -> MinigameView {
        let session = self.session.as_any();
        let mut view = MinigameView {
            kind: self.session.kind().to_owned(),
            prompt: self.session.prompt(),
            button_text: self.session.button_text().to_owned(),
            is_done: self.session.is_done(),
            score: self.session.score(),
            position: 0.0,
            target_left: 0.0,
            target_size: 0.0,
            status: String::new(),
            log: String::new(),
            choices: self.session.choices().iter().map(|choice| (*choice).to_owned()).collect(),
            notes: Vec::new(),
            meter: 0.0,
            progress: 0.0,
            highlight: String::new(),
            prompt_message: self.session.prompt_message(),
            button_message: self.session.button_message(),
            status_message: None,
            log_message: None,
        };
        let status = |view: &mut MinigameView, message: Message| {
            view.status = message.english();
            view.status_message = Some(message);
        };
        if let Some(bar) = session.downcast_ref::<TimingBarSession>() {
            view.position = bar.position();
            view.target_left = bar.target_left();
            view.target_size = bar.target_size();
        } else if let Some(battle) = session.downcast_ref::<SimpleBattleSession>() {
            status(&mut view, battle.status_message());
            view.log = battle.log().to_owned();
            view.log_message = battle.log_message();
        } else if let Some(rhythm) = session.downcast_ref::<RhythmTapSession>() {
            (view.target_left, view.target_size) = rhythm.zone();
            view.notes = rhythm.note_positions();
            view.meter = rhythm.meter();
            view.progress = rhythm.progress();
            status(&mut view, rhythm.status_message());
        } else if let Some(chase) = session.downcast_ref::<MovingTargetSession>() {
            view.position = chase.target_position();
            view.target_left = chase.bar_left();
            view.target_size = chase.bar_size();
            view.meter = chase.meter();
            view.progress = chase.progress();
            status(&mut view, chase.status_message());
        } else if let Some(memory) = session.downcast_ref::<MemorySequenceSession>() {
            view.highlight = memory.highlight().unwrap_or_default().to_owned();
            view.progress = memory.progress();
            status(&mut view, memory.status_message());
        }
        view
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
        // Choices must be the kind's own.
        let memory = MinigameDef { kind: "memory-sequence".into(), ..Default::default() };
        let mut game = HostedMinigame::mount(Some(&memory), 0.5).unwrap();
        assert_eq!(game.view().choices, ["sun", "moon", "star", "leaf"]);
        assert!(game.apply(MinigameInput::Act { choice: "attack".into() }).is_err());
        assert!(game.apply(MinigameInput::Act { choice: "sun".into() }).is_ok());
    }

    #[test]
    fn views_carry_the_catalog_messages_of_their_texts() {
        let definition = MinigameDef { kind: "simple-battle".into(), ..Default::default() };
        let mut game = HostedMinigame::mount(Some(&definition), 0.5).unwrap();
        let view = game.apply(MinigameInput::Act { choice: "attack".into() }).unwrap();
        assert_eq!(view.status_message.as_ref().map(Message::english).as_deref(), Some(view.status.as_str()));
        assert_eq!(view.log_message.as_ref().map(Message::key), Some("msg.minigame.battleAttacks"));
        assert_eq!(view.log, "Forest slime attacks. Choose your next move.");
        let rhythm = MinigameDef { kind: "rhythm-tap".into(), ..Default::default() };
        let view = HostedMinigame::mount(Some(&rhythm), 0.5).unwrap().view();
        assert_eq!(view.prompt_message.as_ref().map(Message::key), Some("msg.minigame.rhythmPrompt"));
        assert_eq!(view.button_message.as_ref().map(Message::key), Some("msg.minigame.rhythmButton"));
        assert!(!view.notes.is_empty());
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
