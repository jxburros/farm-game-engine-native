//! Port of `EngineTypes.cs`: the context a step runs in and what a step returns.

use crate::effects::Effect;
use crate::hooks::HookBus;
use crate::schema::{GameContent, GameState};

/// EngineContext bundles immutable content with the hook bus (state.ts). GameContent never
/// changes during play.
#[derive(Debug, Default)]
pub struct EngineContext {
    pub content: GameContent,
    pub hooks: Option<HookBus>,
}

impl EngineContext {
    pub fn new(content: GameContent) -> Self {
        Self { content, hooks: None }
    }

    pub fn with_hooks(content: GameContent, hooks: HookBus) -> Self {
        Self { content, hooks: Some(hooks) }
    }
}

/// Result of a reducer step: the next state plus host-facing effects.
#[derive(Debug, Clone, PartialEq)]
pub struct EngineStep {
    pub state: GameState,
    pub effects: Vec<Effect>,
}

impl EngineStep {
    pub fn of(state: GameState) -> Self {
        Self { state, effects: Vec::new() }
    }

    pub fn with_effects(state: GameState, effects: Vec<Effect>) -> Self {
        Self { state, effects }
    }
}
