//! Fishing (port of `Fishing.cs` / fishing.ts).
//!
//! Fishing (M4e): cast at water, resolve deterministically through the seeded RNG. Fish tables
//! filter by scene and season; junk rolls first; per-fish difficulty is offset by rod tier. (The
//! timing minigame is a shell-side flourish planned for M7 — it will resolve through this same
//! deterministic path.)

use crate::effects::{message_levels, Effect};
use crate::engine_types::{Effects, EngineContext};
use crate::inventory;
use crate::quests;
use crate::rng::Rng;
use crate::schema::{FishTable, GameState};
use crate::skills;
use crate::units;

/// TS `FishingResult` minus the state (updated in place).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct FishingResult {
    pub effects: Effects,
    pub caught: bool,
}

pub fn active_fish_table<'a>(ctx: &'a EngineContext, state: &GameState) -> Option<&'a FishTable> {
    ctx.content.fish_tables.iter().find(|table| {
        if let Some(scene_ids) = table.scene_ids.as_ref().filter(|ids| !ids.is_empty()) {
            if !scene_ids.contains(&state.player.scene_id) {
                return false;
            }
        }
        if let Some(seasons) = table.seasons.as_ref().filter(|seasons| !seasons.is_empty()) {
            if !seasons.contains(&state.clock.season) {
                return false;
            }
        }
        !table.entries.is_empty()
    })
}

/// Resolve a cast into water (caller already validated rod + water tile).
/// `score` (0–1, from the fishing minigame when one is declared)
/// reduces the escape chance: a perfect score always lands the fish.
///
/// TS `resolveFishing(ctx, state, rodTier, options?)`; `score` is `options.score`.
/// The chance a hooked fish escapes: `max(0, difficulty − 0.15 × (rodTier − 1)) × (1 − score)`,
/// as an exact fraction (numerator, denominator) of 2³² units: each rod tier above 1 takes 15%
/// off the difficulty, and a minigame score (0–1) scales what is left.
pub fn escape_chance(difficulty: u64, rod_tier: i32, score: Option<u64>) -> (u128, u128) {
    let one = i128::from(units::PROBABILITY_ONE);
    // 20 × (difficulty − 3/20 × (tier − 1) × 2³²)
    let scaled = (20 * i128::from(difficulty) - 3 * (i128::from(rod_tier) - 1) * one).max(0) as u128;
    let skill = u128::from(units::PROBABILITY_ONE - score.unwrap_or(0).min(units::PROBABILITY_ONE));
    (scaled * skill, 20 * units::PROBABILITY_ONE as u128)
}

pub fn resolve_fishing(ctx: &EngineContext, state: &mut GameState, rod_tier: i32, score: Option<u64>) -> FishingResult {
    let Some(table) = active_fish_table(ctx, state) else {
        return FishingResult {
            effects: vec![Effect::message(message_levels::INFO, "The water is quiet — nothing seems to live here.")],
            caught: false,
        };
    };

    let mut rng = Rng::new(state.rng.clone());
    let mut effects = Vec::new();
    let mut caught = false;

    // Junk first, then the weighted fish roll, then the escape check.
    // (The float is drawn only when junkChance > 0; `&&` short-circuits.)
    if table.junk_chance > 0
        && rng.chance(table.junk_chance)
        && table.junk_item_id.as_deref().is_some_and(|id| !id.is_empty())
    {
        let junk = table.junk_item_id.as_deref().and_then(|id| ctx.item(id));
        state.rng = rng.state;
        if let Some(junk) = junk {
            let added = inventory::add_item(&state.player.inventory, junk, 1, state.player.max_inventory_size, None);
            if added.added {
                state.player.inventory = added.inventory;
                effects.push(Effect::message(message_levels::INFO, format!("You fished up {}…", junk.name)));
            } else {
                effects.push(Effect::message(message_levels::ERROR, "Inventory is full!"));
            }
        }
        return FishingResult { effects, caught: false };
    }

    let weights: Vec<u32> = table.entries.iter().map(|entry| entry.weight).collect();
    let Some(index) = rng.weighted(&weights) else {
        state.rng = rng.state;
        return FishingResult {
            effects: vec![Effect::message(message_levels::INFO, "Not even a nibble.")],
            caught: false,
        };
    };
    let entry = &table.entries[index];

    // Escape roll: difficulty reduced 15% per rod tier above 1, then scaled
    // down by minigame skill (score 1 → no escape chance at all).
    if rng.chance_below(escape_chance(entry.difficulty, rod_tier, score)) {
        state.rng = rng.state;
        return FishingResult { effects: vec![Effect::message(message_levels::INFO, "It got away!")], caught: false };
    }

    state.rng = rng.state;
    let fish = ctx.item(&entry.item_id);
    if let Some(fish) = fish {
        let added = inventory::add_item(&state.player.inventory, fish, 1, state.player.max_inventory_size, None);
        if added.added {
            state.player.inventory = added.inventory;
            effects.push(Effect::message(message_levels::SUCCESS, format!("Caught a {}!", fish.name)));
            caught = true;
        } else {
            effects.push(Effect::message(message_levels::ERROR, "Inventory is full!"));
        }
    }

    if caught {
        effects.extend(skills::grant_xp(ctx, state, "fishing", 8));
        effects.extend(quests::progress_quests(ctx, state, "collect", &entry.item_id, 1));
    }

    FishingResult { effects, caught }
}
