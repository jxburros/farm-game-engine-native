//! More built-in kinds, each tuned by a few config settings (the editor shows them as typed
//! fields, F# `MinigameKinds`):
//!
//! - `rhythm-tap`: notes scroll toward a line; press as each one reaches it.
//! - `moving-target`: keep a bar under a target that wanders; hold to push the bar right.
//! - `memory-sequence`: watch a sequence of symbols, then repeat it with the choice buttons.
//!
//! Like every kind, only the final score in [0, 1] enters the simulation. Movement and the
//! symbol order use the host's cosmetic randomness ([`super::MinigameMountOptions::random`]).

use super::{number_config, prompt_or, MinigameImpl, MinigameMountOptions, MinigameRegistry};
use super::{MinigameSession, SessionCore};
use farm_sim::messages::{self, Message};
use std::any::Any;
use std::f64::consts::TAU;
use std::sync::{Arc, LazyLock};

pub const RHYTHM_TAP_KIND: &str = "rhythm-tap";
pub const MOVING_TARGET_KIND: &str = "moving-target";
pub const MEMORY_SEQUENCE_KIND: &str = "memory-sequence";

static RHYTHM_TAP: LazyLock<Arc<dyn MinigameImpl>> = LazyLock::new(|| Arc::new(RhythmTapMinigame));
static MOVING_TARGET: LazyLock<Arc<dyn MinigameImpl>> = LazyLock::new(|| Arc::new(MovingTargetMinigame));
static MEMORY_SEQUENCE: LazyLock<Arc<dyn MinigameImpl>> = LazyLock::new(|| Arc::new(MemorySequenceMinigame));

pub fn register(registry: &mut MinigameRegistry) {
    registry.register(RHYTHM_TAP_KIND, Arc::clone(&RHYTHM_TAP));
    registry.register(MOVING_TARGET_KIND, Arc::clone(&MOVING_TARGET));
    registry.register(MEMORY_SEQUENCE_KIND, Arc::clone(&MEMORY_SEQUENCE));
}

/// A whole-number setting clamped to `[min, max]`.
fn count_config(options: &MinigameMountOptions, key: &str, fallback: f64, min: f64, max: f64) -> usize {
    number_config(&options.config, key, fallback).round().clamp(min, max) as usize
}

// ---- rhythm-tap ----

/// 'rhythm-tap': `beats` notes (default 8, 1..32) at `bpm` (default 100, 40..200) scroll toward
/// a line after two beats of lead-in. A press within 30 % of a beat of a note hits it: 1 on the
/// beat, 0.5 at the window's edge. Score = the hits' average over all notes; a missed note scores
/// 0. Input: [`MinigameSession::press`].
#[derive(Debug, Clone, Copy, Default)]
pub struct RhythmTapMinigame;

impl MinigameImpl for RhythmTapMinigame {
    fn mount(&self, options: MinigameMountOptions) -> Box<dyn MinigameSession> {
        Box::new(RhythmTapSession::new(options))
    }
}

/// Where the hit line sits on the track (a fraction from the left).
pub const HIT_LINE: f64 = 0.15;
/// Beats of lead-in before the first note, and beats of track visible ahead of the line.
const LEAD_BEATS: f64 = 2.0;
const VISIBLE_BEATS: f64 = 4.0;

#[derive(Debug, Clone, Copy)]
struct Note {
    /// Seconds after the start.
    time: f64,
    judged: bool,
    hit: bool,
}

#[derive(Debug)]
pub struct RhythmTapSession {
    core: SessionCore,
    prompt: String,
    prompt_message: Option<Message>,
    beat_seconds: f64,
    /// Half the hit window, in seconds.
    window: f64,
    notes: Vec<Note>,
    elapsed: f64,
    earned: f64,
}

impl RhythmTapSession {
    pub fn new(options: MinigameMountOptions) -> Self {
        let beats = count_config(&options, "beats", 8.0, 1.0, 32.0);
        let bpm = number_config(&options.config, "bpm", 100.0).clamp(40.0, 200.0);
        let beat_seconds = 60.0 / bpm;
        let (prompt, prompt_message) = prompt_or(&options.config, messages::RHYTHM_PROMPT.with(&[]));
        let notes = (0..beats)
            .map(|index| Note { time: (LEAD_BEATS + index as f64) * beat_seconds, judged: false, hit: false })
            .collect();
        let mut core = SessionCore::new(options);
        core.set_button(messages::RHYTHM_BUTTON.with(&[]));
        Self {
            core,
            prompt,
            prompt_message,
            beat_seconds,
            window: 0.3 * beat_seconds,
            notes,
            elapsed: 0.0,
            earned: 0.0,
        }
    }

    /// Track length ahead of the line, in seconds.
    fn visible_seconds(&self) -> f64 {
        VISIBLE_BEATS * self.beat_seconds
    }

    /// The notes still to play, as positions on the track in [0, 1] (the line is at
    /// [`HIT_LINE`]).
    pub fn note_positions(&self) -> Vec<f64> {
        self.notes
            .iter()
            .filter(|note| !note.judged)
            .map(|note| HIT_LINE + (note.time - self.elapsed) / self.visible_seconds() * (1.0 - HIT_LINE))
            .filter(|position| (0.0..=1.0).contains(position))
            .collect()
    }

    /// The hit window around the line: its left edge and width on the track.
    pub fn zone(&self) -> (f64, f64) {
        let half = self.window / self.visible_seconds() * (1.0 - HIT_LINE);
        (HIT_LINE - half, 2.0 * half)
    }

    pub fn beats(&self) -> usize {
        self.notes.len()
    }

    pub fn hits(&self) -> usize {
        self.notes.iter().filter(|note| note.hit).count()
    }

    /// The score earned so far, as a fraction of the best possible.
    pub fn meter(&self) -> f64 {
        self.earned / self.notes.len().max(1) as f64
    }

    /// How far through the song, in [0, 1].
    pub fn progress(&self) -> f64 {
        let end = self.notes.last().map_or(0.0, |note| note.time + self.window);
        if end > 0.0 {
            (self.elapsed / end).min(1.0)
        } else {
            1.0
        }
    }

    pub fn status_message(&self) -> Message {
        messages::RHYTHM_HITS.with(&[&self.hits(), &self.beats()])
    }

    fn finish_when_played(&mut self) {
        if self.notes.iter().all(|note| note.judged) {
            let score = self.meter().clamp(0.0, 1.0);
            self.core.complete(score);
        }
    }
}

impl MinigameSession for RhythmTapSession {
    session_core_members!();

    fn kind(&self) -> &str {
        RHYTHM_TAP_KIND
    }

    fn prompt(&self) -> String {
        self.prompt.clone()
    }

    fn prompt_message(&self) -> Option<Message> {
        self.prompt_message.clone()
    }

    fn update(&mut self, delta_seconds: f64) {
        if self.core.is_done() {
            return;
        }
        self.elapsed += delta_seconds;
        let (elapsed, window) = (self.elapsed, self.window);
        for note in self.notes.iter_mut().filter(|note| !note.judged && elapsed > note.time + window) {
            note.judged = true;
        }
        self.finish_when_played();
    }

    fn press(&mut self) {
        if self.core.is_done() {
            return;
        }
        let (elapsed, window) = (self.elapsed, self.window);
        // A press away from every note is just a wasted tap.
        if let Some(note) = self.notes.iter_mut().find(|note| !note.judged && (elapsed - note.time).abs() <= window) {
            note.judged = true;
            note.hit = true;
            self.earned += 1.0 - 0.5 * (elapsed - note.time).abs() / window;
        }
        self.finish_when_played();
    }
}

// ---- moving-target ----

/// 'moving-target': for `seconds` (default 8, 2..60) a target wanders along a track at
/// `targetSpeed` (default 0.5, 0.1..3); the player's bar (`barSize` of the track, default 0.3,
/// 0.1..0.8) drifts left and moves right while the button is held. Score = the share of the
/// time the target was over the bar, where 80 % or more scores 1. Input:
/// [`MinigameSession::press`] / [`MinigameSession::release`].
#[derive(Debug, Clone, Copy, Default)]
pub struct MovingTargetMinigame;

impl MinigameImpl for MovingTargetMinigame {
    fn mount(&self, options: MinigameMountOptions) -> Box<dyn MinigameSession> {
        Box::new(MovingTargetSession::new(options))
    }
}

/// Simulation step of the bar (seconds); longer frames are split.
const STEP: f64 = 1.0 / 120.0;
const BAR_ACCELERATION: f64 = 1.8;
const BAR_MAX_SPEED: f64 = 0.9;
/// The share of time over the target that scores 1.
const FULL_MARKS: f64 = 0.8;

#[derive(Debug)]
pub struct MovingTargetSession {
    core: SessionCore,
    prompt: String,
    prompt_message: Option<Message>,
    seconds: f64,
    speed: f64,
    bar_size: f64,
    bar_left: f64,
    velocity: f64,
    holding: bool,
    elapsed: f64,
    inside_seconds: f64,
    phase: f64,
}

impl MovingTargetSession {
    pub fn new(mut options: MinigameMountOptions) -> Self {
        let seconds = number_config(&options.config, "seconds", 8.0).clamp(2.0, 60.0);
        let speed = number_config(&options.config, "targetSpeed", 0.5).clamp(0.1, 3.0);
        let bar_size = number_config(&options.config, "barSize", 0.3).clamp(0.1, 0.8);
        let (prompt, prompt_message) = prompt_or(&options.config, messages::MOVING_PROMPT.with(&[]));
        let phase = options.next_random() * TAU;
        let mut core = SessionCore::new(options);
        core.set_button(messages::MOVING_BUTTON.with(&[]));
        Self {
            core,
            prompt,
            prompt_message,
            seconds,
            speed,
            bar_size,
            bar_left: 0.5 - bar_size / 2.0,
            velocity: 0.0,
            holding: false,
            elapsed: 0.0,
            inside_seconds: 0.0,
            phase,
        }
    }

    /// The target's position on the track at `time` seconds, in [0, 1].
    fn target_at(&self, time: f64) -> f64 {
        let t = time * self.speed;
        let wave = 0.65 * (t * 2.0 + self.phase).sin() + 0.35 * (t * 4.7 + 2.0 * self.phase).sin();
        (0.5 + 0.42 * wave).clamp(0.0, 1.0)
    }

    pub fn target_position(&self) -> f64 {
        self.target_at(self.elapsed)
    }

    pub fn bar_left(&self) -> f64 {
        self.bar_left
    }

    pub fn bar_size(&self) -> f64 {
        self.bar_size
    }

    /// The share of the time so far the target was over the bar, in [0, 1].
    pub fn meter(&self) -> f64 {
        (self.inside_seconds / self.seconds).min(1.0)
    }

    pub fn progress(&self) -> f64 {
        (self.elapsed / self.seconds).min(1.0)
    }

    pub fn status_message(&self) -> Message {
        let left = (self.seconds - self.elapsed).max(0.0).ceil() as i64;
        messages::MOVING_TIME_LEFT.with(&[&left])
    }

    fn step(&mut self, seconds: f64) {
        let acceleration = if self.holding { BAR_ACCELERATION } else { -BAR_ACCELERATION };
        self.velocity = (self.velocity + acceleration * seconds).clamp(-BAR_MAX_SPEED, BAR_MAX_SPEED);
        self.bar_left += self.velocity * seconds;
        let right_end = 1.0 - self.bar_size;
        if self.bar_left <= 0.0 || self.bar_left >= right_end {
            self.bar_left = self.bar_left.clamp(0.0, right_end);
            self.velocity = 0.0;
        }
        self.elapsed += seconds;
        let target = self.target_position();
        if target >= self.bar_left && target <= self.bar_left + self.bar_size {
            self.inside_seconds += seconds;
        }
    }
}

impl MinigameSession for MovingTargetSession {
    session_core_members!();

    fn kind(&self) -> &str {
        MOVING_TARGET_KIND
    }

    fn prompt(&self) -> String {
        self.prompt.clone()
    }

    fn prompt_message(&self) -> Option<Message> {
        self.prompt_message.clone()
    }

    fn update(&mut self, delta_seconds: f64) {
        let mut remaining = delta_seconds;
        while remaining > 0.0 && !self.core.is_done() {
            let step = remaining.min(STEP).min(self.seconds - self.elapsed);
            if step <= 0.0 {
                break;
            }
            self.step(step);
            remaining -= step;
            if self.elapsed >= self.seconds - 1e-9 {
                let score = (self.meter() / FULL_MARKS).min(1.0);
                self.core.complete(score);
            }
        }
    }

    fn press(&mut self) {
        self.holding = true;
    }

    fn release(&mut self) {
        self.holding = false;
    }
}

// ---- memory-sequence ----

/// 'memory-sequence': shows `length` symbols (default 4, 2..10) one at a time, each for
/// `showSeconds` (default 0.8, 0.2..3), then the player repeats them with the choice buttons.
/// The first wrong choice ends the game. Score = the share of the sequence repeated correctly.
/// Input: [`MinigameSession::act`] with one of [`SYMBOLS`].
#[derive(Debug, Clone, Copy, Default)]
pub struct MemorySequenceMinigame;

impl MinigameImpl for MemorySequenceMinigame {
    fn mount(&self, options: MinigameMountOptions) -> Box<dyn MinigameSession> {
        Box::new(MemorySequenceSession::new(options))
    }
}

/// The symbols a sequence is made of (the choice ids).
pub const SYMBOLS: &[&str] = &["sun", "moon", "star", "leaf"];
/// The pause between two shown symbols, in seconds.
const GAP_SECONDS: f64 = 0.25;

#[derive(Debug)]
pub struct MemorySequenceSession {
    core: SessionCore,
    prompt: String,
    prompt_message: Option<Message>,
    sequence: Vec<usize>,
    show_seconds: f64,
    elapsed: f64,
    entered: usize,
}

impl MemorySequenceSession {
    pub fn new(mut options: MinigameMountOptions) -> Self {
        let length = count_config(&options, "length", 4.0, 2.0, 10.0);
        let show_seconds = number_config(&options.config, "showSeconds", 0.8).clamp(0.2, 3.0);
        let (prompt, prompt_message) = prompt_or(&options.config, messages::MEMORY_PROMPT.with(&[]));
        // A small xorshift from the cosmetic random value; no symbol twice in a row.
        let mut seed = ((options.next_random() * 4_294_967_296.0) as u32) | 1;
        let mut sequence: Vec<usize> = Vec::with_capacity(length);
        for _ in 0..length {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            let mut symbol = seed as usize % SYMBOLS.len();
            if sequence.last() == Some(&symbol) {
                symbol = (symbol + 1 + (seed >> 8) as usize % (SYMBOLS.len() - 1)) % SYMBOLS.len();
            }
            sequence.push(symbol);
        }
        let core = SessionCore::new(options);
        Self { core, prompt, prompt_message, sequence, show_seconds, elapsed: 0.0, entered: 0 }
    }

    fn slot_seconds(&self) -> f64 {
        self.show_seconds + GAP_SECONDS
    }

    /// True while the sequence is being shown (choices are ignored).
    pub fn showing(&self) -> bool {
        self.elapsed < self.sequence.len() as f64 * self.slot_seconds()
    }

    /// The symbol shown right now, if any.
    pub fn highlight(&self) -> Option<&'static str> {
        if !self.showing() {
            return None;
        }
        let slot = (self.elapsed / self.slot_seconds()).floor();
        let into = self.elapsed - slot * self.slot_seconds();
        let symbol = self.sequence.get(slot as usize)?;
        (into < self.show_seconds).then(|| SYMBOLS[*symbol])
    }

    /// The sequence, as symbol ids.
    pub fn sequence(&self) -> Vec<&'static str> {
        self.sequence.iter().map(|symbol| SYMBOLS[*symbol]).collect()
    }

    /// Symbols repeated correctly so far.
    pub fn entered(&self) -> usize {
        self.entered
    }

    pub fn progress(&self) -> f64 {
        self.entered as f64 / self.sequence.len().max(1) as f64
    }

    pub fn status_message(&self) -> Message {
        if self.showing() {
            messages::MEMORY_WATCH.with(&[])
        } else {
            messages::MEMORY_TURN.with(&[&self.entered, &self.sequence.len()])
        }
    }
}

impl MinigameSession for MemorySequenceSession {
    session_core_members!();

    fn kind(&self) -> &str {
        MEMORY_SEQUENCE_KIND
    }

    fn prompt(&self) -> String {
        self.prompt.clone()
    }

    fn prompt_message(&self) -> Option<Message> {
        self.prompt_message.clone()
    }

    fn choices(&self) -> &'static [&'static str] {
        SYMBOLS
    }

    fn update(&mut self, delta_seconds: f64) {
        if !self.core.is_done() {
            self.elapsed += delta_seconds;
        }
    }

    fn act(&mut self, choice: &str) {
        if self.core.is_done() || self.showing() {
            return;
        }
        let Some(symbol) = SYMBOLS.iter().position(|candidate| *candidate == choice) else { return };
        if self.sequence.get(self.entered) == Some(&symbol) {
            self.entered += 1;
            if self.entered == self.sequence.len() {
                self.core.complete(1.0);
            }
        } else {
            let score = self.progress();
            self.core.complete(score);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::minigames::MinigameConfig;
    use farm_sim::units;
    use std::sync::Mutex;

    fn mount<S: 'static>(build: fn(MinigameMountOptions) -> S, entries: &[(&str, f64)]) -> (S, Arc<Mutex<Vec<f64>>>) {
        let config: MinigameConfig =
            entries.iter().map(|(key, value)| ((*key).to_owned(), units::value(*value))).collect();
        let scores = Arc::new(Mutex::new(Vec::new()));
        let sink = Arc::clone(&scores);
        let options = MinigameMountOptions::new(config, move |score| sink.lock().unwrap().push(score), || {})
            .with_random(|| 0.25);
        (build(options), scores)
    }

    #[test]
    fn rhythm_tap_scores_hits_by_accuracy_and_misses_as_zero() {
        // 60 bpm: notes at 2 s and 3 s, window ±0.3 s.
        let (mut game, scores) = mount(RhythmTapSession::new, &[("beats", 2.0), ("bpm", 60.0)]);
        assert_eq!(game.prompt(), "Tap when a note reaches the line!");
        assert_eq!(game.button_text(), "Tap! (Space)");
        assert_eq!(game.note_positions().len(), 2);
        game.update(1.0);
        game.press(); // nothing near: ignored
        game.update(1.0);
        game.press(); // on the beat
        assert_eq!(game.hits(), 1);
        assert_eq!(game.status_message().english(), "Hits: 1/2");
        game.update(1.5); // the second note passes unplayed
        assert!(game.is_done());
        assert_eq!(*scores.lock().unwrap(), [0.5]);
    }

    #[test]
    fn moving_target_scores_the_time_over_the_target() {
        let (mut idle, scores) = mount(MovingTargetSession::new, &[("seconds", 2.0), ("barSize", 0.8)]);
        idle.update(1.0);
        assert!(!idle.is_done());
        assert!(idle.bar_left() < 0.15, "the bar drifts left while released: {}", idle.bar_left());
        idle.update(1.5);
        assert!(idle.is_done());
        let score = scores.lock().unwrap()[0];
        assert!((0.0..=1.0).contains(&score));
        // Holding pushes the bar to the right end.
        let (mut held, _) = mount(MovingTargetSession::new, &[("seconds", 4.0)]);
        held.press();
        held.update(3.0);
        assert!((held.bar_left() - 0.7).abs() < 1e-9, "{}", held.bar_left());
        held.release();
        assert_eq!(held.status_message().english(), "1 s left");
    }

    #[test]
    fn memory_sequence_ends_at_the_first_wrong_symbol() {
        let (mut game, scores) = mount(MemorySequenceSession::new, &[("length", 4.0), ("showSeconds", 0.5)]);
        let sequence = game.sequence();
        assert_eq!(sequence.len(), 4);
        assert!(sequence.windows(2).all(|pair| pair[0] != pair[1]), "{sequence:?}");
        assert_eq!(game.highlight(), Some(sequence[0]));
        game.act(sequence[0]); // still showing: ignored
        assert_eq!(game.entered(), 0);
        game.update(0.6);
        assert_eq!(game.highlight(), None, "the gap between symbols");
        game.update(0.2);
        assert_eq!(game.highlight(), Some(sequence[1]));
        game.update(3.0);
        assert!(!game.showing());
        assert_eq!(game.status_message().english(), "Your turn: 0/4");
        game.act(sequence[0]);
        game.act(sequence[1]);
        let wrong = SYMBOLS.iter().find(|symbol| **symbol != sequence[2]).unwrap();
        game.act(wrong);
        assert_eq!(*scores.lock().unwrap(), [0.5]);
        // A full repeat scores 1.
        let (mut game, scores) = mount(MemorySequenceSession::new, &[("length", 2.0)]);
        game.update(10.0);
        for symbol in game.sequence() {
            game.act(symbol);
        }
        assert_eq!(*scores.lock().unwrap(), [1.0]);
    }
}
