//! Port of the retired C# `InventoryTests.cs`
//! (packages/engine-core/src/inventory.test.ts).

use farm_sim::inventory::{
    add_item, add_item_with_quality, count_item, count_item_with_quality, find_slot, find_tool_slot, refresh_item,
    remove_item, remove_item_with_quality, replace_item, AddItemOptions,
};
use farm_sim::schema::{InventorySlot, Item};

fn make_item(id: &str, max_stack: u32) -> Item {
    Item {
        id: id.to_owned(),
        name: id.to_owned(),
        description: id.to_owned(),
        r#type: "material".to_owned(),
        stackable: true,
        max_stack,
        value: 1,
        ..Item::default()
    }
}

fn slot(item: &Item, quantity: u32) -> InventorySlot {
    InventorySlot::new(item.clone(), quantity)
}

// describe('removeItem')

#[test]
fn drains_across_multiple_slots_holding_the_same_item_id() {
    let wood = make_item("wood", 99);
    let inventory = vec![slot(&wood, 3), slot(&make_item("stone", 99), 5), slot(&wood, 4)];
    let next = remove_item(&inventory, "wood", 5);
    // 3 from the first slot (deleted), 2 from the second wood slot.
    assert_eq!(next, vec![slot(&make_item("stone", 99), 5), slot(&wood, 2)]);
}

#[test]
fn removes_only_what_exists_and_preserves_unrelated_slots() {
    let wood = make_item("wood", 99);
    let next = remove_item(&[slot(&wood, 2)], "wood", 10);
    assert!(next.is_empty());
    let untouched = remove_item(&[slot(&wood, 2)], "iron", 1);
    assert_eq!(untouched, vec![slot(&wood, 2)]);
}

#[test]
fn does_not_mutate_the_input_inventory() {
    let wood = make_item("wood", 99);
    let inventory = vec![slot(&wood, 3)];
    let _ = remove_item(&inventory, "wood", 1);
    assert_eq!(inventory[0].quantity, 3);
}

// describe('addItem stack caps')

#[test]
fn overflows_past_max_stack_into_a_new_slot() {
    let wood = make_item("wood", 10);
    let result = add_item(&[slot(&wood, 8)], &wood, 5, 5, None);
    assert!(result.added);
    assert_eq!(result.inventory, vec![slot(&wood, 10), slot(&wood, 3)]);
}

#[test]
fn absorbs_what_fits_and_rejects_overflow_when_the_inventory_is_full() {
    let wood = make_item("wood", 10);
    let filler = slot(&make_item("stone", 99), 1);
    let result = add_item(&[slot(&wood, 8), filler.clone()], &wood, 5, 2, None);
    assert!(!result.added);
    assert_eq!(result.inventory, vec![slot(&wood, 10), filler]);
}

#[test]
fn keeps_the_historical_unbounded_merge_for_items_without_a_cap() {
    // A maxStack of 0 (an item authored without one) means no cap.
    let uncapped = make_item("wood", 0);
    let result = add_item(&[slot(&uncapped, 500)], &uncapped, 600, 1, None);
    assert!(result.added);
    assert_eq!(result.inventory, vec![slot(&uncapped, 1100)]);
    let fresh = add_item(&[], &uncapped, 600, 1, None);
    assert_eq!(fresh.inventory, vec![slot(&uncapped, 600)]);
}

// The stacking bugs of #22, with seed-wheat's cap of 99.

#[test]
fn new_slots_are_capped_at_max_stack() {
    let seeds = make_item("seed-wheat", 99);
    let result = add_item(&[], &seeds, 500, 20, None);
    let quantities: Vec<u32> = result.inventory.iter().map(|s| s.quantity).collect();
    assert_eq!(quantities, vec![99, 99, 99, 99, 99, 5]);
    assert_eq!(result.added_quantity, 500);
}

#[test]
fn overflow_tops_up_then_splits_into_capped_slots() {
    let seeds = make_item("seed-wheat", 99);
    let result = add_item(&[slot(&seeds, 1)], &seeds, 500, 20, None);
    let quantities: Vec<u32> = result.inventory.iter().map(|s| s.quantity).collect();
    assert_eq!(quantities, vec![99, 99, 99, 99, 99, 6]);
}

#[test]
fn single_adds_fill_any_slot_with_room_before_opening_a_new_one() {
    let seeds = make_item("seed-wheat", 99);
    let mut inventory = vec![slot(&seeds, 99), slot(&seeds, 50)];
    for _ in 0..5 {
        inventory = add_item(&inventory, &seeds, 1, 20, None).inventory;
    }
    assert_eq!(inventory, vec![slot(&seeds, 99), slot(&seeds, 55)]);
}

#[test]
fn a_partial_add_reports_how_much_fit() {
    let seeds = make_item("seed-wheat", 99);
    let result = add_item(&[slot(&seeds, 90)], &seeds, 120, 2, None);
    assert!(!result.added);
    assert_eq!(result.added_quantity, 108);
    assert_eq!(result.rejected(120), 12);
    assert_eq!(result.inventory, vec![slot(&seeds, 99), slot(&seeds, 99)]);
}

#[test]
fn qualities_stack_apart() {
    let wheat = make_item("crop-wheat", 99);
    let result = add_item_with_quality(&[slot(&wheat, 3)], &wheat, Some("gold"), 2, 5, None);
    assert_eq!(result.inventory[0], slot(&wheat, 3));
    assert_eq!(result.inventory[1].quality.as_deref(), Some("gold"));
    // Normal quality is stored as no quality, so it stacks with older slots.
    let normal = add_item_with_quality(&result.inventory, &wheat, Some("normal"), 1, 5, None);
    assert_eq!(normal.inventory[0], slot(&wheat, 4));
    assert_eq!(count_item(&normal.inventory, "crop-wheat"), 6);
    assert_eq!(count_item_with_quality(&normal.inventory, "crop-wheat", Some("gold")), 2);
    let sold = remove_item_with_quality(&normal.inventory, "crop-wheat", Some("gold"), 2);
    assert_eq!(sold, vec![slot(&wheat, 4)]);
}

#[test]
fn refreshing_an_item_keeps_its_durability_within_the_new_maximum() {
    let mut hoe = make_item("hoe", 1);
    hoe.durability = Some(80);
    hoe.max_durability = Some(100);
    let mut renamed = hoe.clone();
    renamed.name = "Old Hoe".to_owned();
    renamed.durability = Some(100);
    renamed.max_durability = Some(60);
    let refreshed = refresh_item(&hoe, &renamed);
    assert_eq!(refreshed.name, "Old Hoe");
    assert_eq!(refreshed.durability, Some(60));
    renamed.max_durability = Some(120);
    assert_eq!(refresh_item(&hoe, &renamed).durability, Some(80));
    // An unbreakable tool now: it takes the definition's (absent) durability.
    renamed.max_durability = None;
    renamed.durability = None;
    assert_eq!(refresh_item(&hoe, &renamed).durability, None);
}

// Extra coverage of the remaining helpers (inventory.ts has no tests for them).

#[test]
fn rejects_a_full_slot_when_no_overflow_slot_fits() {
    let wood = make_item("wood", 10);
    let filler = slot(&make_item("stone", 99), 1);
    let result = add_item(&[slot(&wood, 10), filler.clone()], &wood, 1, 2, None);
    assert!(!result.added);
    assert_eq!(result.inventory, vec![slot(&wood, 10), filler]);
}

#[test]
fn appends_a_new_slot_only_while_there_is_room() {
    let wood = make_item("wood", 99);
    let stone = make_item("stone", 99);
    let added = add_item(&[slot(&wood, 1)], &stone, 2, 2, None);
    assert!(added.added);
    assert_eq!(added.inventory, vec![slot(&wood, 1), slot(&stone, 2)]);
    let full = add_item(&[slot(&wood, 1)], &stone, 2, 1, None);
    assert!(!full.added);
    assert_eq!(full.inventory, vec![slot(&wood, 1)]);
}

#[test]
fn require_stackable_for_merge_appends_instead_of_merging_unstackable_items() {
    let mut hoe = make_item("hoe", 1);
    hoe.stackable = false;
    let options = Some(AddItemOptions { require_stackable_for_merge: Some(true) });
    let result = add_item(&[slot(&hoe, 1)], &hoe, 1, 5, options);
    assert!(result.added);
    assert_eq!(result.inventory, vec![slot(&hoe, 1), slot(&hoe, 1)]);
    // Without the option the slot merges (and overflows past the cap of 1).
    let merged = add_item(&[slot(&hoe, 1)], &hoe, 1, 5, None);
    assert_eq!(merged.inventory, vec![slot(&hoe, 1), slot(&hoe, 1)]);
    let stackable = make_item("wood", 99);
    let merged = add_item(&[slot(&stackable, 1)], &stackable, 1, 5, options);
    assert_eq!(merged.inventory, vec![slot(&stackable, 2)]);
}

#[test]
fn finds_slots_by_predicate_and_tool_type() {
    let mut axe = make_item("axe", 1);
    axe.tool_type = Some("axe".to_owned());
    let wood = make_item("wood", 99);
    let inventory = vec![slot(&wood, 3), slot(&axe, 1)];
    assert_eq!(find_tool_slot(&inventory, "axe"), Some(&inventory[1]));
    assert_eq!(find_tool_slot(&inventory, "hoe"), None);
    assert_eq!(find_slot(&inventory, |s| s.quantity == 3), Some(&inventory[0]));
    assert_eq!(find_slot(&inventory, |s| s.quantity == 9), None);
}

#[test]
fn replace_item_swaps_the_item_object_in_every_matching_slot() {
    let wood = make_item("wood", 99);
    let stone = make_item("stone", 99);
    let mut renamed = wood.clone();
    renamed.name = "Timber".to_owned();
    let inventory = vec![slot(&wood, 3), slot(&stone, 1), slot(&wood, 4)];
    let next = replace_item(&inventory, "wood", &renamed);
    assert_eq!(next, vec![slot(&renamed, 3), slot(&stone, 1), slot(&renamed, 4)]);
    assert_eq!(inventory[0].item.name, "wood");
}
