//! Port of `EngineTypes.cs`: the context a step runs in and what a step returns.
//!
//! **Reducer convention (Rust port).** The C# engine is immutable: every handler returns a new
//! `GameState` (`state with { … }`). The Rust core follows docs/LANGUAGES.md instead: *state is
//! mutable inside a step, immutable across steps*. Handlers take `&mut GameState`, change it in
//! place and return only the effects:
//!
//! ```text
//! C#:   EngineStep HandleX(EngineContext ctx, GameState state, …)   // new state + effects
//! Rust: fn handle_x(ctx: &EngineContext, state: &mut GameState, …) -> Effects
//! ```
//!
//! Ports must keep the C# *decision order*: everything the C# reads from the old state before
//! deciding is read first, and the state is only touched once the command succeeds (an early
//! `return EngineStep.Of(state, message)` in C# is an early `return vec![message]` here with the
//! state untouched). Where the C# builds a candidate state and then discards it, clone first.
//! Pure helpers that return new values (inventory lists, tiles, crops) stay pure.

use crate::effects::Effect;
use crate::hooks::{HookBus, HookEvent, WeatherRollHookPayload};
use crate::schema::GameContent;
use std::cell::RefCell;

/// The effects a step produced, in order (host-facing: toasts, sounds, camera snaps).
pub type Effects = Vec<Effect>;

/// EngineContext bundles immutable content with the hook bus (state.ts). GameContent never
/// changes during play. The bus sits in a `RefCell` so handlers can emit through a shared
/// `&EngineContext` while holding references into `content` (single-threaded by design).
#[derive(Debug, Default)]
pub struct EngineContext {
    pub content: GameContent,
    pub hooks: Option<RefCell<HookBus>>,
}

impl EngineContext {
    pub fn new(content: GameContent) -> Self {
        Self { content, hooks: None }
    }

    pub fn with_hooks(content: GameContent, hooks: HookBus) -> Self {
        Self { content, hooks: Some(RefCell::new(hooks)) }
    }

    /// `ctx.Hooks?.Emit(...)`: records the event when a bus is attached.
    pub fn emit(&self, event: HookEvent) {
        if let Some(bus) = &self.hooks {
            bus.borrow_mut().emit(event);
        }
    }

    /// `ctx.Hooks?.Collect(OnWeatherRoll, …)`: the synchronous override answers (empty without a bus).
    pub fn collect_weather_roll(&self, payload: WeatherRollHookPayload) -> Vec<String> {
        match &self.hooks {
            Some(bus) => bus.borrow_mut().collect_weather_roll(payload),
            None => Vec::new(),
        }
    }

    /// Takes the hook events emitted since the last drain (the host reads them after each step).
    pub fn drain_hook_events(&self) -> Vec<HookEvent> {
        match &self.hooks {
            Some(bus) => bus.borrow_mut().drain(),
            None => Vec::new(),
        }
    }
}

/// Result of a step for hosts and tests: the effects plus the hook events (the state was
/// updated in place).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct StepOutput {
    pub effects: Effects,
    pub hook_events: Vec<HookEvent>,
}
