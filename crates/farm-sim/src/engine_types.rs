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

use crate::content_index::{self as ids, ContentIndex};
use crate::effects::Effect;
use crate::hooks::{HookBus, HookEvent, WeatherRollHookPayload};
use crate::schema::{
    ActionDef, AnimalSpeciesDefinition, Dialogue, GameContent, Item, MachineTypeDefinition, MinigameDef,
    NodeTypeDefinition, Npc, Quest, RecipeDefinition, Scene, ShopDefinition,
};
use std::cell::RefCell;

/// The effects a step produced, in order (host-facing: toasts, sounds, camera snaps).
pub type Effects = Vec<Effect>;

/// Which commands apply in which situations. Rust decides what a player may do
/// (docs/LANGUAGES.md): replays, the FFI and web sessions and any future lockstep play feed
/// commands straight to the engine, so the player UI's own restraint is not enough.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum CommandRules {
    /// What a player can do (every host's default). While a dialogue, shop or minigame is open
    /// only its own commands apply (and `setMoveIntent` and plugin mutations). `descendMine`
    /// works on a mine floor or beside the mine entrance, down to the floor after the deepest
    /// elevator checkpoint or the current floor; `exitMine` only on a mine floor; `openShop` only
    /// facing an NPC whose dialogue opens that shop; `startMinigame` is refused (minigames open
    /// from actions, items, tools and plugins).
    #[default]
    Player,
    /// Scripts and tests: every command applies wherever the player stands, as in the reference
    /// engines. The golden replays are input logs scripted this way.
    Scripted,
}

/// EngineContext bundles immutable content with the hook bus (state.ts). GameContent never
/// changes during play. The bus sits in a `RefCell` so handlers can emit through a shared
/// `&EngineContext` while holding references into `content` (single-threaded by design).
#[derive(Debug, Default)]
pub struct EngineContext {
    pub content: GameContent,
    pub hooks: Option<RefCell<HookBus>>,
    /// Which commands apply when (see [`CommandRules`]).
    pub rules: CommandRules,
    /// Id tables over `content`, built with the context (see [`ContentIndex`]). The lookup
    /// methods below use them; call [`Self::reindex`] after editing `content` in place.
    index: ContentIndex,
}

impl EngineContext {
    /// A context over `content` under [`CommandRules::Player`], without a hook bus.
    pub fn new(content: GameContent) -> Self {
        let index = ContentIndex::build(&content);
        Self { content, hooks: None, rules: CommandRules::Player, index }
    }

    /// A context over `content` whose handlers emit into `hooks`.
    pub fn with_hooks(content: GameContent, hooks: HookBus) -> Self {
        let index = ContentIndex::build(&content);
        Self { content, hooks: Some(RefCell::new(hooks)), rules: CommandRules::Player, index }
    }

    /// Rebuild the id tables after `content` was edited in place (lookups stay correct without
    /// it, only slower).
    pub fn reindex(&mut self) {
        self.index = ContentIndex::build(&self.content);
    }

    /// The id tables over `content`.
    pub fn index(&self) -> &ContentIndex {
        &self.index
    }

    /// The item definition `id` (the first one, like a scan of `content.items`).
    pub fn item(&self, id: &str) -> Option<&Item> {
        self.index.items.find(&self.content.items, id, ids::item_id)
    }

    /// The NPC definition `id`.
    pub fn npc(&self, id: &str) -> Option<&Npc> {
        self.index.npcs.find(&self.content.npcs, id, ids::npc_id)
    }

    /// The standalone dialogue `id`.
    pub fn dialogue(&self, id: &str) -> Option<&Dialogue> {
        self.index.dialogues.find(&self.content.dialogues, id, ids::dialogue_id)
    }

    /// The quest definition `id`.
    pub fn quest(&self, id: &str) -> Option<&Quest> {
        self.index.quests.find(&self.content.quests, id, ids::quest_id)
    }

    /// The shop definition `id`.
    pub fn shop(&self, id: &str) -> Option<&ShopDefinition> {
        self.index.shops.find(&self.content.shops, id, ids::shop_id)
    }

    /// The gathering node type `id`.
    pub fn node_type(&self, id: &str) -> Option<&NodeTypeDefinition> {
        self.index.node_types.find(&self.content.node_types, id, ids::node_type_id)
    }

    /// The crafting recipe `id`.
    pub fn recipe(&self, id: &str) -> Option<&RecipeDefinition> {
        self.index.recipes.find(&self.content.recipes, id, ids::recipe_id)
    }

    /// The machine type `id`.
    pub fn machine_type(&self, id: &str) -> Option<&MachineTypeDefinition> {
        self.index.machine_types.find(&self.content.machine_types, id, ids::machine_type_id)
    }

    /// The animal species `id`.
    pub fn animal_species(&self, id: &str) -> Option<&AnimalSpeciesDefinition> {
        self.index.animal_species.find(&self.content.animal_species, id, ids::animal_species_id)
    }

    /// The creator-defined action `id`.
    pub fn action(&self, id: &str) -> Option<&ActionDef> {
        self.index.actions.find(&self.content.actions, id, ids::action_id)
    }

    /// The declared minigame `id`.
    pub fn minigame(&self, id: &str) -> Option<&MinigameDef> {
        self.index.minigames.find(&self.content.minigames, id, ids::minigame_id)
    }

    /// The authored scene `id` (the template; the running world is in the state).
    pub fn content_scene(&self, id: &str) -> Option<&Scene> {
        self.index.scenes.find(&self.content.scenes, id, ids::scene_id)
    }

    /// This context under other [`CommandRules`].
    pub fn with_rules(self, rules: CommandRules) -> Self {
        Self { rules, ..self }
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
