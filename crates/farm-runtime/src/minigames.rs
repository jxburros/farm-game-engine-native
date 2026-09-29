//! Host-side minigame framework (port of `Minigames.cs` / engine-runtime `minigames.ts` and
//! `custom-game/minigames.ts`).
//!
//! The simulation opens a session (`state.minigame`); the host mounts the implementation
//! registered for the def's `kind`; the player's performance becomes a single score in [0, 1]
//! that re-enters the engine as the deterministic `resolveMinigame` command.
//!
//! The TS implementations own DOM widgets. Here each implementation produces a host-agnostic
//! session model: the host advances it with [`MinigameSession::update`], feeds it input
//! ([`MinigameSession::press`] / [`MinigameSession::release`] or kind-specific calls) and draws
//! its public state. Only `on_complete(score)` crosses back into deterministic simulation.
//!
//! Game code extends this by registering new kinds:
//!
//! ```
//! use farm_runtime::minigames::{self, MinigameImpl, MinigameMountOptions, MinigameSession};
//! use std::sync::Arc;
//!
//! struct MyRhythmGame;
//!
//! impl MinigameImpl for MyRhythmGame {
//!     fn mount(&self, options: MinigameMountOptions) -> Box<dyn MinigameSession> {
//!         // Return a session that calls `options.on_complete(score)` once.
//!         minigames::fallback().mount(options)
//!     }
//! }
//!
//! let mut registry = minigames::create_default_minigame_registry();
//! registry.register("my-rhythm-game", Arc::new(MyRhythmGame));
//! assert!(registry.kinds().any(|kind| kind == "my-rhythm-game"));
//! ```

use farm_sim::schema::MinigameDef;
use indexmap::IndexMap;
use serde_json::Value;
use std::any::Any;
use std::fmt;
use std::sync::{Arc, LazyLock};

/// A def's `config` record, verbatim (`MinigameDef::config`).
pub type MinigameConfig = IndexMap<String, Value>;

/// TS `MinigameMountOptions` (C# `MinigameMountOptions`).
pub struct MinigameMountOptions {
    /// The def's `config` record, verbatim.
    pub config: MinigameConfig,
    /// Resolve with a score in [0, 1]. Called at most once.
    pub on_complete: Box<dyn FnMut(f64) + Send>,
    /// Abort without resolving (the host issues `cancelMinigame`).
    pub on_cancel: Box<dyn FnMut() + Send>,
    /// Cosmetic randomness in [0, 1) (e.g. the timing-bar target placement). The C# default is
    /// `Random.Shared`; the runtime never reads OS randomness, so when this is `None` the
    /// sessions use a fixed 0.5 (a centered target). Hosts that want variety pass their own.
    pub random: Option<Box<dyn FnMut() -> f64 + Send>>,
}

impl MinigameMountOptions {
    pub fn new(
        config: MinigameConfig,
        on_complete: impl FnMut(f64) + Send + 'static,
        on_cancel: impl FnMut() + Send + 'static,
    ) -> Self {
        Self { config, on_complete: Box::new(on_complete), on_cancel: Box::new(on_cancel), random: None }
    }

    /// Sets the cosmetic randomness source.
    pub fn with_random(mut self, random: impl FnMut() -> f64 + Send + 'static) -> Self {
        self.random = Some(Box::new(random));
        self
    }

    fn next_random(&mut self) -> f64 {
        self.random.as_mut().map_or(0.5, |random| random())
    }
}

impl fmt::Debug for MinigameMountOptions {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("MinigameMountOptions")
            .field("config", &self.config)
            .field("random", &self.random.as_ref().map(|_| "<fn>"))
            .finish_non_exhaustive()
    }
}

/// A mounted minigame (TS `MinigameHandle` plus its UI state; C# `IMinigameSession` and the
/// shared `MinigameSessionBase` members). [`dispose`](Self::dispose) ends the session: no
/// completion can be reported afterwards. Sessions are `Send` so a running game can move between
/// threads (the editor steps it off its UI thread).
pub trait MinigameSession: Any + Send {
    /// The registry kind this session implements.
    fn kind(&self) -> &str;
    /// Instruction text for the player.
    fn prompt(&self) -> String;
    /// Label of the primary button.
    fn button_text(&self) -> &str;
    /// True once the score was reported (or the session was cancelled or disposed).
    fn is_done(&self) -> bool;
    /// The score reported via `on_complete`, if any.
    fn score(&self) -> Option<f64>;
    /// Advance real time (seconds since the previous frame).
    fn update(&mut self, _delta_seconds: f64) {}
    /// Primary input pressed (Space / Enter / click / pointer down).
    fn press(&mut self) {}
    /// Primary input released (Space / Enter key-up / pointer up).
    fn release(&mut self) {}
    /// Abort without resolving: the host issues `cancelMinigame` (`on_cancel`, at most once).
    fn cancel(&mut self);
    /// End the session without reporting anything (C# `Dispose`).
    fn dispose(&mut self);
    /// For downcasting to the concrete session (`TimingBarSession`, …).
    fn as_any(&self) -> &dyn Any;
    fn as_any_mut(&mut self) -> &mut dyn Any;
}

impl fmt::Debug for dyn MinigameSession {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("MinigameSession").field("kind", &self.kind()).field("is_done", &self.is_done()).finish()
    }
}

/// TS `MinigameImpl`: a registered minigame kind (C# `IMinigameImpl`).
pub trait MinigameImpl: Send + Sync {
    fn mount(&self, options: MinigameMountOptions) -> Box<dyn MinigameSession>;
}

/// Shared once-only completion plumbing (C# `MinigameSessionBase`).
pub struct SessionCore {
    options: MinigameMountOptions,
    is_done: bool,
    score: Option<f64>,
    /// Label of the primary button.
    pub button_text: String,
}

impl SessionCore {
    pub fn new(options: MinigameMountOptions) -> Self {
        Self { options, is_done: false, score: None, button_text: String::new() }
    }

    pub fn is_done(&self) -> bool {
        self.is_done
    }

    pub fn score(&self) -> Option<f64> {
        self.score
    }

    pub fn config(&self) -> &MinigameConfig {
        &self.options.config
    }

    /// Report the score (at most once).
    pub fn complete(&mut self, score: f64) {
        if self.is_done {
            return;
        }
        self.is_done = true;
        self.score = Some(score);
        (self.options.on_complete)(score);
    }

    /// Abort without resolving: the host issues `cancelMinigame`.
    pub fn cancel(&mut self) {
        if self.is_done {
            return;
        }
        self.is_done = true;
        (self.options.on_cancel)();
    }

    /// End the session silently.
    pub fn dispose(&mut self) {
        self.is_done = true;
    }
}

impl fmt::Debug for SessionCore {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("SessionCore")
            .field("options", &self.options)
            .field("is_done", &self.is_done)
            .field("score", &self.score)
            .field("button_text", &self.button_text)
            .finish()
    }
}

/// Implements the [`MinigameSession`] members every session shares through its `core` field.
macro_rules! session_core_members {
    () => {
        fn button_text(&self) -> &str {
            &self.core.button_text
        }
        fn is_done(&self) -> bool {
            self.core.is_done()
        }
        fn score(&self) -> Option<f64> {
            self.core.score()
        }
        fn cancel(&mut self) {
            self.core.cancel();
        }
        fn dispose(&mut self) {
            self.core.dispose();
        }
        fn as_any(&self) -> &dyn Any {
            self
        }
        fn as_any_mut(&mut self) -> &mut dyn Any {
            self
        }
    };
}

/// TS `MinigameRegistry`.
#[derive(Default, Clone)]
pub struct MinigameRegistry {
    kinds: IndexMap<String, Arc<dyn MinigameImpl>>,
}

impl MinigameRegistry {
    pub fn new() -> Self {
        Self::default()
    }

    /// Registers (or replaces, keeping its position) the implementation for `kind`.
    pub fn register(&mut self, kind: impl Into<String>, implementation: Arc<dyn MinigameImpl>) {
        self.kinds.insert(kind.into(), implementation);
    }

    pub fn get(&self, kind: &str) -> Option<Arc<dyn MinigameImpl>> {
        self.kinds.get(kind).cloned()
    }

    /// Registered kinds, in registration order.
    pub fn kinds(&self) -> impl Iterator<Item = &str> {
        self.kinds.keys().map(String::as_str)
    }
}

impl fmt::Debug for MinigameRegistry {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("MinigameRegistry").field("kinds", &self.kinds().collect::<Vec<_>>()).finish()
    }
}

pub const TIMING_BAR_KIND: &str = "timing-bar";

static TIMING_BAR: LazyLock<Arc<dyn MinigameImpl>> = LazyLock::new(|| Arc::new(TimingBarMinigame));
static FALLBACK: LazyLock<Arc<dyn MinigameImpl>> = LazyLock::new(|| Arc::new(FallbackMinigame));

/// The built-in 'timing-bar' minigame (one shared instance, C# `Minigames.TimingBar`).
pub fn timing_bar() -> Arc<dyn MinigameImpl> {
    Arc::clone(&TIMING_BAR)
}

/// Fallback for unregistered kinds: a single button that scores a neutral 0.5 (one shared
/// instance, C# `Minigames.Fallback`).
pub fn fallback() -> Arc<dyn MinigameImpl> {
    Arc::clone(&FALLBACK)
}

/// Registry preloaded with the built-in kinds (plus the game's own, see [`custom_game`]).
pub fn create_default_minigame_registry() -> MinigameRegistry {
    let mut registry = MinigameRegistry::new();
    registry.register(TIMING_BAR_KIND, timing_bar());
    custom_game::register_game_minigames(&mut registry);
    registry
}

/// Implementation for a def: the registered kind, or the neutral fallback.
pub fn minigame_impl_for(registry: &MinigameRegistry, def: Option<&MinigameDef>) -> Arc<dyn MinigameImpl> {
    def.and_then(|def| registry.get(&def.kind)).unwrap_or_else(fallback)
}

/// TS `numberConfig`: a finite number, else the fallback. (JSON numbers are always finite.)
pub fn number_config(config: &MinigameConfig, key: &str, fallback: f64) -> f64 {
    config.get(key).and_then(Value::as_f64).filter(|value| value.is_finite()).unwrap_or(fallback)
}

/// A string config value, else `None`.
pub fn string_config<'a>(config: &'a MinigameConfig, key: &str) -> Option<&'a str> {
    config.get(key).and_then(Value::as_str)
}

/// Built-in 'timing-bar' minigame: a marker sweeps across a bar; stop it as close to the target
/// zone's center as possible. Score = 1 inside the zone, else falling off linearly to 0 at a
/// full bar-width from the center.
///
/// Config: `speed` (sweeps/second, default 0.9), `targetSize` (target zone width as a bar
/// fraction, default 0.18), `prompt` (instruction text). Input: [`MinigameSession::press`]
/// (Space / Enter / "Stop!" button) stops the marker.
#[derive(Debug, Clone, Copy, Default)]
pub struct TimingBarMinigame;

impl MinigameImpl for TimingBarMinigame {
    fn mount(&self, options: MinigameMountOptions) -> Box<dyn MinigameSession> {
        Box::new(TimingBarSession::new(options))
    }
}

#[derive(Debug)]
pub struct TimingBarSession {
    core: SessionCore,
    elapsed_seconds: f64,
    speed: f64,
    target_size: f64,
    target_center: f64,
    prompt: String,
}

impl TimingBarSession {
    pub fn new(mut options: MinigameMountOptions) -> Self {
        let speed = number_config(&options.config, "speed", 0.9).max(0.1);
        let target_size = number_config(&options.config, "targetSize", 0.18).clamp(0.02, 0.9);
        let prompt = string_config(&options.config, "prompt").unwrap_or("Stop the marker in the zone!").to_owned();
        // Cosmetic placement only; the score is what enters the log.
        let target_center = 0.3 + 0.4 * options.next_random();
        let mut core = SessionCore::new(options);
        core.button_text = "Stop! (Space)".to_owned();
        Self { core, elapsed_seconds: 0.0, speed, target_size, target_center, prompt }
    }

    /// Sweeps per second.
    pub fn speed(&self) -> f64 {
        self.speed
    }

    /// Target zone width as a fraction of the bar.
    pub fn target_size(&self) -> f64 {
        self.target_size
    }

    /// Target zone center as a fraction of the bar.
    pub fn target_center(&self) -> f64 {
        self.target_center
    }

    /// Target zone left edge (bar fraction), for drawing.
    pub fn target_left(&self) -> f64 {
        self.target_center - self.target_size / 2.0
    }

    /// Marker position in [0, 1]: a triangle wave sweeping right then left, continuously.
    pub fn position(&self) -> f64 {
        let t = self.elapsed_seconds * self.speed;
        let phase = t % 2.0;
        if phase <= 1.0 {
            phase
        } else {
            2.0 - phase
        }
    }

    /// Stop the marker and score (TS `stop()`).
    pub fn stop(&mut self) {
        if self.core.is_done() {
            return;
        }
        let distance = (self.position() - self.target_center).abs();
        let in_zone = distance <= self.target_size / 2.0;
        self.core.complete(if in_zone { 1.0 } else { (1.0 - distance).max(0.0) });
    }
}

impl MinigameSession for TimingBarSession {
    session_core_members!();

    fn kind(&self) -> &str {
        TIMING_BAR_KIND
    }

    fn prompt(&self) -> String {
        self.prompt.clone()
    }

    fn update(&mut self, delta_seconds: f64) {
        if !self.core.is_done() {
            self.elapsed_seconds += delta_seconds;
        }
    }

    fn press(&mut self) {
        self.stop();
    }
}

/// Fallback for unregistered kinds: a single "Go!" button that scores a neutral 0.5.
#[derive(Debug, Clone, Copy, Default)]
pub struct FallbackMinigame;

impl MinigameImpl for FallbackMinigame {
    fn mount(&self, options: MinigameMountOptions) -> Box<dyn MinigameSession> {
        Box::new(FallbackSession::new(options))
    }
}

#[derive(Debug)]
pub struct FallbackSession {
    core: SessionCore,
    prompt: String,
}

impl FallbackSession {
    pub fn new(options: MinigameMountOptions) -> Self {
        let prompt = string_config(&options.config, "prompt").unwrap_or("Ready?").to_owned();
        let mut core = SessionCore::new(options);
        core.button_text = "Go!".to_owned();
        Self { core, prompt }
    }
}

impl MinigameSession for FallbackSession {
    session_core_members!();

    fn kind(&self) -> &str {
        "fallback"
    }

    fn prompt(&self) -> String {
        self.prompt.clone()
    }

    fn press(&mut self) {
        self.core.complete(0.5);
    }
}

/// YOUR GAME CODE GOES HERE (port of `custom-game/minigames.ts`, C# `CustomGameMinigames`).
/// Both the editor and exported shell call this registration function. Add your own
/// implementations and register them. Each session owns its UI state; `dispose` must stop it.
/// Only `on_complete(score)` crosses back into deterministic simulation.
pub mod custom_game {
    use super::{string_config, MinigameConfig, MinigameImpl, MinigameMountOptions, MinigameRegistry};
    use super::{MinigameSession, SessionCore};
    use farm_sim::units;
    use std::any::Any;
    use std::sync::{Arc, LazyLock};

    pub const HOLD_TO_CATCH_KIND: &str = "hold-to-catch";
    pub const SIMPLE_BATTLE_KIND: &str = "simple-battle";

    static HOLD_TO_CATCH: LazyLock<Arc<dyn MinigameImpl>> = LazyLock::new(|| Arc::new(HoldToCatchMinigame));
    static SIMPLE_BATTLE: LazyLock<Arc<dyn MinigameImpl>> = LazyLock::new(|| Arc::new(SimpleBattleMinigame));

    /// The shared 'hold-to-catch' instance (C# `CustomGameMinigames.HoldToCatch`).
    pub fn hold_to_catch() -> Arc<dyn MinigameImpl> {
        Arc::clone(&HOLD_TO_CATCH)
    }

    /// The shared 'simple-battle' instance (C# `CustomGameMinigames.SimpleBattle`).
    pub fn simple_battle() -> Arc<dyn MinigameImpl> {
        Arc::clone(&SIMPLE_BATTLE)
    }

    pub fn register_game_minigames(registry: &mut MinigameRegistry) {
        registry.register(HOLD_TO_CATCH_KIND, hold_to_catch());
        registry.register(SIMPLE_BATTLE_KIND, simple_battle());
    }

    /// TS `number(value, fallback, max = 100)`: a finite number clamped to [1, max], else the
    /// fallback.
    pub fn number(config: &MinigameConfig, key: &str, fallback: f64, max: f64) -> f64 {
        match config.get(key).and_then(serde_json::Value::as_f64).filter(|value| value.is_finite()) {
            Some(value) => value.min(max).max(1.0),
            None => fallback,
        }
    }

    /// Example fishing replacement: hold the button, then release near the target.
    /// Config: `holdMs` (target hold, default 1200, 1..10000). Input: [`MinigameSession::press`]
    /// starts reeling, [`MinigameSession::release`] finishes; [`HoldToCatchSession::cancel_hold`]
    /// is a pointer-cancel.
    #[derive(Debug, Clone, Copy, Default)]
    pub struct HoldToCatchMinigame;

    impl MinigameImpl for HoldToCatchMinigame {
        fn mount(&self, options: MinigameMountOptions) -> Box<dyn MinigameSession> {
            Box::new(HoldToCatchSession::new(options))
        }
    }

    const HOLD_BUTTON_TEXT: &str = "Hold to reel (Space)";

    #[derive(Debug)]
    pub struct HoldToCatchSession {
        core: SessionCore,
        now_ms: f64,
        started_ms: Option<f64>,
        target_ms: f64,
        prompt: String,
    }

    impl HoldToCatchSession {
        pub fn new(options: MinigameMountOptions) -> Self {
            let target_ms = number(&options.config, "holdMs", 1200.0, 10000.0);
            let prompt =
                format!("Hold for {} seconds, then release to reel in.", units::to_fixed(target_ms / 1000.0, 1));
            let mut core = SessionCore::new(options);
            core.button_text = HOLD_BUTTON_TEXT.to_owned();
            Self { core, now_ms: 0.0, started_ms: None, target_ms, prompt }
        }

        pub fn target_ms(&self) -> f64 {
            self.target_ms
        }

        /// Milliseconds held so far (`None` when not holding).
        pub fn held_ms(&self) -> Option<f64> {
            self.started_ms.map(|started| self.now_ms - started)
        }

        /// Pointer cancelled mid-hold: reset without scoring.
        pub fn cancel_hold(&mut self) {
            self.started_ms = None;
            self.core.button_text = HOLD_BUTTON_TEXT.to_owned();
        }
    }

    impl MinigameSession for HoldToCatchSession {
        session_core_members!();

        fn kind(&self) -> &str {
            HOLD_TO_CATCH_KIND
        }

        fn prompt(&self) -> String {
            self.prompt.clone()
        }

        fn update(&mut self, delta_seconds: f64) {
            self.now_ms += delta_seconds * 1000.0;
        }

        fn press(&mut self) {
            if !self.core.is_done() && self.started_ms.is_none() {
                self.started_ms = Some(self.now_ms);
                self.core.button_text = "Reeling… release!".to_owned();
            }
        }

        fn release(&mut self) {
            let Some(started) = self.started_ms else { return };
            if self.core.is_done() {
                return;
            }
            let score = (1.0 - (self.now_ms - started - self.target_ms).abs() / self.target_ms).max(0.0);
            self.core.complete(score);
        }
    }

    /// Small turn-based encounter; win/loss consequences are authored as result tiers (score 1
    /// = win, 0 = loss). Config: `playerHealth` (30), `enemyHealth` (24), `attack` (7),
    /// `enemyAttack` (5), `enemyName`. Input: [`SimpleBattleSession::act`] with "attack" |
    /// "guard" | "magic".
    #[derive(Debug, Clone, Copy, Default)]
    pub struct SimpleBattleMinigame;

    impl MinigameImpl for SimpleBattleMinigame {
        fn mount(&self, options: MinigameMountOptions) -> Box<dyn MinigameSession> {
            Box::new(SimpleBattleSession::new(options))
        }
    }

    #[derive(Debug)]
    pub struct SimpleBattleSession {
        core: SessionCore,
        power: f64,
        foe_power: f64,
        turn: f64,
        hp: f64,
        enemy: f64,
        mana: f64,
        enemy_name: String,
        log: String,
    }

    impl SimpleBattleSession {
        pub const CHOICES: &'static [&'static str] = &["attack", "guard", "magic"];

        pub fn new(options: MinigameMountOptions) -> Self {
            let config = &options.config;
            let hp = number(config, "playerHealth", 30.0, 100.0);
            let enemy = number(config, "enemyHealth", 24.0, 100.0);
            let power = number(config, "attack", 7.0, 100.0);
            let foe_power = number(config, "enemyAttack", 5.0, 100.0);
            let enemy_name = string_config(config, "enemyName").unwrap_or("Forest slime").to_owned();
            Self {
                core: SessionCore::new(options),
                power,
                foe_power,
                turn: 0.0,
                hp,
                enemy,
                mana: 3.0,
                enemy_name,
                log: String::new(),
            }
        }

        pub fn hp(&self) -> f64 {
            self.hp
        }

        pub fn enemy(&self) -> f64 {
            self.enemy
        }

        pub fn mana(&self) -> f64 {
            self.mana
        }

        pub fn enemy_name(&self) -> &str {
            &self.enemy_name
        }

        /// Latest battle narration (empty before the first turn).
        pub fn log(&self) -> &str {
            &self.log
        }

        pub fn status(&self) -> String {
            format!(
                "You: {} health · {} magic | {}: {} health",
                units::format_number(self.hp.max(0.0)),
                units::format_number(self.mana),
                self.enemy_name,
                units::format_number(self.enemy.max(0.0))
            )
        }

        /// One turn: `kind` is "attack" | "guard" | "magic".
        pub fn act(&mut self, kind: &str) {
            if self.core.is_done() || (kind == "magic" && self.mana == 0.0) {
                return;
            }
            let heavy = self.turn % 3.0 == 2.0;
            if kind == "attack" {
                self.enemy -= self.power;
            }
            if kind == "magic" {
                self.mana -= 1.0;
                self.enemy -= self.power * 2.0;
            }
            if self.enemy > 0.0 {
                self.hp -= if kind == "guard" { 1.0 } else { self.foe_power * if heavy { 2.0 } else { 1.0 } };
            }
            self.turn += 1.0;
            self.log = if self.turn % 3.0 == 2.0 {
                format!("{} is preparing a heavy attack. Guard next turn!", self.enemy_name)
            } else {
                format!("{} attacks. Choose your next move.", self.enemy_name)
            };
            if self.hp <= 0.0 || self.enemy <= 0.0 {
                self.core.complete(if self.enemy <= 0.0 { 1.0 } else { 0.0 });
            }
        }
    }

    impl MinigameSession for SimpleBattleSession {
        session_core_members!();

        fn kind(&self) -> &str {
            SIMPLE_BATTLE_KIND
        }

        fn prompt(&self) -> String {
            self.status()
        }

        /// Primary input defaults to a plain attack.
        fn press(&mut self) {
            self.act("attack");
        }
    }
}

#[cfg(test)]
mod tests {
    use super::custom_game::{HoldToCatchSession, SimpleBattleSession};
    use super::*;
    use farm_sim::units;
    use std::sync::{Arc, Mutex};

    fn options(config: MinigameConfig) -> (MinigameMountOptions, Arc<Mutex<Vec<f64>>>) {
        let scores = Arc::new(Mutex::new(Vec::new()));
        let sink = Arc::clone(&scores);
        (MinigameMountOptions::new(config, move |score| sink.lock().unwrap().push(score), || {}), scores)
    }

    #[test]
    fn default_registry_lists_kinds_in_registration_order() {
        let registry = create_default_minigame_registry();
        assert_eq!(registry.kinds().collect::<Vec<_>>(), ["timing-bar", "hold-to-catch", "simple-battle"]);
    }

    #[test]
    fn hold_to_catch_prompt_uses_js_to_fixed_and_cancel_hold_resets() {
        let (opts, scores) = options([("holdMs".to_owned(), units::value(250.0))].into_iter().collect());
        let mut session = HoldToCatchSession::new(opts);
        // (0.25).toFixed(1) is "0.3" in JavaScript.
        assert_eq!(session.prompt(), "Hold for 0.3 seconds, then release to reel in.");
        session.release(); // not holding: ignored
        session.press();
        session.update(0.1);
        assert_eq!(session.held_ms(), Some(100.0));
        session.cancel_hold();
        assert_eq!(session.held_ms(), None);
        assert_eq!(session.button_text(), "Hold to reel (Space)");
        session.press();
        session.update(0.5);
        session.release();
        // Held 500 ms for a 250 ms target: 1 - |500 - 250| / 250 = 0.
        assert_eq!(*scores.lock().unwrap(), [0.0]);
    }

    #[test]
    fn simple_battle_heavy_attacks_and_loss() {
        let config = [("playerHealth".to_owned(), units::value(12.0)), ("enemyName".to_owned(), Value::from("Crab"))]
            .into_iter()
            .collect();
        let (opts, scores) = options(config);
        let mut battle = SimpleBattleSession::new(opts);
        assert_eq!(battle.prompt(), "You: 12 health · 3 magic | Crab: 24 health");
        battle.act("attack"); // enemy 17, hp 7
        assert_eq!(battle.log(), "Crab attacks. Choose your next move.");
        battle.act("guard"); // hp 6; next turn is heavy
        assert_eq!(battle.log(), "Crab is preparing a heavy attack. Guard next turn!");
        battle.press(); // attack: enemy 10, heavy hit 10 -> hp -4
        assert_eq!(battle.status(), "You: 0 health · 3 magic | Crab: 10 health");
        assert_eq!(*scores.lock().unwrap(), [0.0]);
        assert!(battle.is_done());
    }
}
