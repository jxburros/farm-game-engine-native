//! Animals (port of `Animals.cs` / animals.ts).
//!
//! Animals & ranching (M4c). Interacting with an animal feeds it (if you carry its feed),
//! collects a ready product, or pets it. The nightly pass ages animals, rolls products
//! (fed + adult + interval) and adjusts mood.
//!
//! v1 simplifications (documented): animals are editor-placed (no in-game purchase flow yet)
//! and stay where placed; barns/coops reuse the existing interior-scene pattern rather than
//! being special buildings.

use crate::effects::{message_levels, Effect};
use crate::engine_types::{Effects, EngineContext};
use crate::inventory;
use crate::schema::{AnimalSpeciesDefinition, AnimalState, GameState};
use crate::units;

/// The animal species `species_id`.
pub fn species_by_id<'a>(ctx: &'a EngineContext, species_id: &str) -> Option<&'a AnimalSpeciesDefinition> {
    ctx.animal_species(species_id)
}

/// The animal standing on tile `(x, y)` (animal positions are tile-aligned).
pub fn animal_at<'a>(state: &'a GameState, scene_id: &str, x: i32, y: i32) -> Option<&'a AnimalState> {
    let (x, y) = (units::tiles(x), units::tiles(y));
    state.animals.iter().find(|animal| animal.scene_id == scene_id && animal.x == x && animal.y == y)
}

/// TS `updateAnimal(state, id, patch)`: the patch is applied to every animal with that id (in place).
fn update_animal(state: &mut GameState, animal_id: &str, patch: impl Fn(&mut AnimalState)) {
    for animal in &mut state.animals {
        if animal.id == animal_id {
            patch(animal);
        }
    }
}

/// `!string.IsNullOrEmpty(species.FeedItemId)`: the feed item when the species needs one.
fn feed_item_id(species: &AnimalSpeciesDefinition) -> Option<&str> {
    species.feed_item_id.as_deref().filter(|id| !id.is_empty())
}

/// Feed → collect → pet, in that priority.
///
/// `animal` is a copy of the animal being interacted with (the C# passes the record).
pub fn handle_animal_interaction(ctx: &EngineContext, state: &mut GameState, animal: &AnimalState) -> Effects {
    let Some(species) = species_by_id(ctx, &animal.species_id) else {
        return Vec::new();
    };

    // 1. Feed (needs the species' feed item in inventory)
    if !animal.fed_today {
        if let Some(feed_item_id) = feed_item_id(species) {
            let has_feed = state.player.inventory.iter().any(|slot| slot.item.id == feed_item_id);
            if has_feed {
                let inventory = inventory::remove_item(&state.player.inventory, feed_item_id, 1);
                update_animal(state, &animal.id, |a| {
                    a.fed_today = true;
                    a.mood = 100.min(animal.mood.saturating_add(5));
                });
                state.player.inventory = inventory;
                return vec![Effect::message(message_levels::SUCCESS, format!("Fed {}", animal.name))];
            }
        }
    }

    // 2. Collect a ready product
    if animal.product_ready {
        let product = ctx.item(&species.product_item_id);
        if let Some(product) = product {
            let added = inventory::add_item(&state.player.inventory, product, 1, state.player.max_inventory_size, None);
            if !added.added {
                return vec![Effect::message(message_levels::ERROR, "Inventory is full!")];
            }
            state.player.inventory = added.inventory;
            update_animal(state, &animal.id, |a| {
                a.product_ready = false;
                a.days_since_product = 0;
            });
            return vec![Effect::message(
                message_levels::SUCCESS,
                format!("Collected {} from {}", product.name, animal.name),
            )];
        }
    }

    // 3. Pet
    if !animal.petted_today {
        update_animal(state, &animal.id, |a| {
            a.petted_today = true;
            a.mood = 100.min(animal.mood.saturating_add(8));
        });
        return vec![Effect::message(message_levels::SUCCESS, format!("{} looks happy! ♥", animal.name))];
    }

    vec![Effect::message(message_levels::INFO, format!("{} is content.", animal.name))]
}

/// Nightly pass for animals: age, mood, product rolls.
///
/// C# returned a new state; here the animals advance in place.
pub fn advance_animals_nightly(ctx: &EngineContext, state: &mut GameState) {
    if state.animals.is_empty() {
        return;
    }

    for animal in &mut state.animals {
        let Some(species) = species_by_id(ctx, &animal.species_id) else {
            continue;
        };

        let needs_feed = feed_item_id(species).is_some();
        let was_cared_for = !needs_feed || animal.fed_today;
        let mood_delta = (if was_cared_for { 4 } else { -15 }) + (if animal.petted_today { 4 } else { 0 });
        let age_days = animal.age_days.saturating_add(1);
        let is_adult = age_days >= species.days_to_adult;

        let mut days_since_product = animal.days_since_product;
        let mut product_ready = animal.product_ready;
        if is_adult && was_cared_for && !product_ready {
            days_since_product = days_since_product.saturating_add(1);
            if days_since_product >= species.product_interval_days && animal.mood >= 30 {
                product_ready = true;
            }
        }

        animal.age_days = age_days;
        animal.mood = animal.mood.saturating_add(mood_delta).clamp(0, 100);
        animal.fed_today = false;
        animal.petted_today = false;
        animal.days_since_product = days_since_product;
        animal.product_ready = product_ready;
    }
}
