//! Property tests of the determinism contract (docs/LANGUAGES.md "Property tests"): the same
//! seed and the same command log give the same state, whatever the commands.

mod strategies;

use farm_sim::replay::{self, ReplayInput};
use farm_sim::schema::{GameContent, GameProject, InventorySlot, Item, RecipeDefinition};
use farm_sim::{engine, inventory, state, Command, EngineContext, HookBus, StepOutput};
use proptest::prelude::*;
use proptest::sample::select;
use std::collections::BTreeMap;
use std::sync::LazyLock;
use strategies::Ids;

struct Fixture {
    project: GameProject,
    content: GameContent,
    ids: Ids,
}

/// The starter farm: every system on (crops, shop, quests, crafting, machines, animals, fishing).
static STARTER: LazyLock<Fixture> = LazyLock::new(|| {
    let project = strategies::golden_project("starter-farm");
    let content = state::create_content_from_project(&project);
    let ids = Ids::of(&project);
    Fixture { project, content, ids }
});

/// Plays `inputs` on a new game: the final state hash, and every effect and hook event.
fn play(fixture: &Fixture, seed: &str, inputs: &[ReplayInput]) -> (String, StepOutput) {
    let ctx = EngineContext::with_hooks(fixture.content.clone(), HookBus::new());
    let mut game = state::create_game_state(&fixture.project, Some(seed));
    let result = replay::run_replay(&ctx, &mut game, inputs);
    (result.hash, StepOutput { effects: result.effects, hook_events: ctx.drain_hook_events() })
}

proptest! {
    #![proptest_config(ProptestConfig { cases: 64, ..ProptestConfig::default() })]

    #[test]
    fn replaying_the_same_log_from_the_same_seed_gives_the_same_hash(
        seed in strategies::seed(),
        inputs in strategies::inputs(&STARTER.ids, 40, 400),
    ) {
        let (hash, output) = play(&STARTER, &seed, &inputs);
        let (again, output_again) = play(&STARTER, &seed, &inputs);
        prop_assert_eq!(hash, again);
        prop_assert_eq!(output, output_again);
    }

    /// `advance_tick(n)` is exactly `n` × `advance_tick(1)`: how a frame's ticks are batched
    /// never changes the game.
    #[test]
    fn splitting_ticks_does_not_change_the_hash(
        seed in strategies::seed(),
        inputs in strategies::inputs(&STARTER.ids, 20, 400),
    ) {
        let split: Vec<ReplayInput> = inputs
            .iter()
            .flat_map(|input| match input {
                ReplayInput::Tick { ticks } if *ticks >= 2 => {
                    let first = ticks / 2;
                    vec![replay::ticks(first), replay::ticks(ticks - first)]
                }
                other => vec![other.clone()],
            })
            .collect();
        prop_assert_eq!(play(&STARTER, &seed, &inputs).0, play(&STARTER, &seed, &split).0);
    }
}

// --- Inventory and crafting invariants -------------------------------------------------------

/// A small catalogue with every kind of stack cap: 1 (tools), small, large, and 0 (no cap).
fn catalogue() -> Vec<Item> {
    [("tool", 1, false), ("ore", 5, true), ("seed", 99, true), ("loose", 0, true)]
        .into_iter()
        .map(|(id, max_stack, stackable)| Item {
            id: id.to_owned(),
            name: id.to_owned(),
            r#type: "material".to_owned(),
            stackable,
            max_stack,
            ..Item::default()
        })
        .collect()
}

/// An inventory of up to `max` slots of the catalogue, each within its item's cap.
fn inventory_strategy(max: usize) -> impl Strategy<Value = Vec<InventorySlot>> {
    let items = catalogue();
    proptest::collection::vec(
        (0..items.len(), 1u32..=150, proptest::option::of(select(vec!["silver", "gold"]))),
        0..=max,
    )
    .prop_map(move |slots| {
        slots
            .into_iter()
            .map(|(index, quantity, quality)| {
                let item = items[index].clone();
                let quantity = quantity.min(inventory::stack_cap(&item));
                InventorySlot { item, quantity, quality: quality.map(str::to_owned) }
            })
            .collect()
    })
}

/// Units per (item id, quality).
fn units(inventory: &[InventorySlot]) -> BTreeMap<(String, Option<String>), u64> {
    let mut units = BTreeMap::new();
    for slot in inventory {
        *units.entry((slot.item.id.clone(), slot.quality.clone())).or_default() += u64::from(slot.quantity);
    }
    units
}

/// Units per item id (any quality).
fn units_by_id(inventory: &[InventorySlot]) -> BTreeMap<String, i64> {
    let mut units = BTreeMap::new();
    for slot in inventory {
        *units.entry(slot.item.id.clone()).or_default() += i64::from(slot.quantity);
    }
    units
}

proptest! {
    #![proptest_config(ProptestConfig { cases: 256, ..ProptestConfig::default() })]

    /// `add_item` conserves quantity (added + rejected = requested, and exactly the added units
    /// appear), keeps every stack within its cap, never grows past the slot limit, and touches
    /// no other item.
    #[test]
    fn adding_items_conserves_quantity_and_respects_both_caps(
        inventory in inventory_strategy(12),
        item_index in 0usize..4,
        quantity in 0u32..=600,
        max_size in 0u32..=12,
        quality in proptest::option::of(select(vec!["silver", "gold"])),
    ) {
        let item = catalogue()[item_index].clone();
        let result = inventory::add_item_with_quality(&inventory, &item, quality, quantity, max_size, None);
        prop_assert_eq!(result.added_quantity + result.rejected(quantity), quantity);
        prop_assert_eq!(result.added, result.added_quantity == quantity);

        let key = (item.id.clone(), quality.map(str::to_owned));
        let mut expected = units(&inventory);
        *expected.entry(key.clone()).or_default() += u64::from(result.added_quantity);
        expected.retain(|_, count| *count > 0);
        prop_assert_eq!(units(&result.inventory), expected);

        let cap = inventory::stack_cap(&item);
        for slot in &result.inventory {
            prop_assert!(slot.quantity <= inventory::stack_cap(&slot.item), "{} × {}", slot.item.id, slot.quantity);
            prop_assert!(slot.quantity > 0 || inventory.contains(slot));
        }
        prop_assert!(result.inventory.len() <= inventory.len().max(max_size as usize));
        // Nothing is added while an existing slot of the same kind still has room.
        if !result.added {
            let room: u64 = result
                .inventory
                .iter()
                .filter(|slot| slot.item.id == item.id && slot.quality.as_deref() == quality)
                .map(|slot| u64::from(cap - slot.quantity))
                .sum();
            prop_assert_eq!(room, 0);
            prop_assert!(result.inventory.len() >= max_size as usize);
        }
    }

    /// A hand craft is all or nothing: the inventory is unchanged, or exactly the recipe's
    /// inputs are gone and every output is there.
    #[test]
    fn a_craft_either_changes_nothing_or_trades_every_input_for_every_output(
        recipe_index in 0usize..32,
        fill in proptest::collection::vec((0usize..64, 1u32..=120), 0..=14),
        max_size in 1u32..=14,
    ) {
        let content = &STARTER.content;
        let recipes: Vec<&RecipeDefinition> =
            content.recipes.iter().filter(|recipe| recipe.machine_type_id.as_deref().is_none_or(str::is_empty)).collect();
        prop_assume!(!recipes.is_empty());
        let recipe = recipes[recipe_index % recipes.len()];
        let ctx = EngineContext::new(content.clone());
        let mut game = state::create_game_state(&STARTER.project, Some("craft"));
        // Inputs first (so most crafts are possible), then random filler.
        let mut inventory = Vec::new();
        for input in &recipe.inputs {
            if let Some(item) = content.items.iter().find(|item| item.id == input.item_id) {
                inventory = inventory::add_item(&inventory, item, input.quantity, max_size, None).inventory;
            }
        }
        for (index, quantity) in fill {
            let item = &content.items[index % content.items.len()];
            inventory = inventory::add_item(&inventory, item, quantity, max_size, None).inventory;
        }
        game.player.inventory = inventory;
        game.player.max_inventory_size = max_size;
        game.player.skills.clear();
        let before = units_by_id(&game.player.inventory);
        let effects = engine::apply_command(&ctx, &mut game, &Command::Craft { recipe_id: recipe.id.clone() });
        let after = units_by_id(&game.player.inventory);
        if after != before {
            let mut expected = before.clone();
            for input in &recipe.inputs {
                *expected.entry(input.item_id.clone()).or_default() -= i64::from(input.quantity);
            }
            for output in &recipe.outputs {
                if content.items.iter().any(|item| item.id == output.item_id) {
                    *expected.entry(output.item_id.clone()).or_default() += i64::from(output.quantity);
                }
            }
            expected.retain(|_, count| *count != 0);
            prop_assert_eq!(after, expected, "effects: {:?}", effects);
        }
        prop_assert!(game.player.inventory.len() <= max_size as usize);
    }
}
