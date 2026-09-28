//! Port of the retired C# `InventoryTests.cs`
//! (packages/engine-core/src/inventory.test.ts).

use farm_sim::inventory::{add_item, find_slot, find_tool_slot, remove_item, replace_item, AddItemOptions};
use farm_sim::schema::{InventorySlot, Item};

fn make_item(id: &str, max_stack: f64) -> Item {
    Item {
        id: id.to_owned(),
        name: id.to_owned(),
        description: id.to_owned(),
        r#type: "material".to_owned(),
        stackable: true,
        max_stack,
        value: 1.0,
        ..Item::default()
    }
}

fn slot(item: &Item, quantity: f64) -> InventorySlot {
    InventorySlot { item: item.clone(), quantity }
}

// describe('removeItem')

#[test]
fn drains_across_multiple_slots_holding_the_same_item_id() {
    let wood = make_item("wood", 99.0);
    let inventory = vec![slot(&wood, 3.0), slot(&make_item("stone", 99.0), 5.0), slot(&wood, 4.0)];
    let next = remove_item(&inventory, "wood", 5.0);
    // 3 from the first slot (deleted), 2 from the second wood slot.
    assert_eq!(next, vec![slot(&make_item("stone", 99.0), 5.0), slot(&wood, 2.0)]);
}

#[test]
fn removes_only_what_exists_and_preserves_unrelated_slots() {
    let wood = make_item("wood", 99.0);
    let next = remove_item(&[slot(&wood, 2.0)], "wood", 10.0);
    assert!(next.is_empty());
    let untouched = remove_item(&[slot(&wood, 2.0)], "iron", 1.0);
    assert_eq!(untouched, vec![slot(&wood, 2.0)]);
}

#[test]
fn does_not_mutate_the_input_inventory() {
    let wood = make_item("wood", 99.0);
    let inventory = vec![slot(&wood, 3.0)];
    let _ = remove_item(&inventory, "wood", 1.0);
    assert_eq!(inventory[0].quantity, 3.0);
}

// describe('addItem stack caps')

#[test]
fn overflows_past_max_stack_into_a_new_slot() {
    let wood = make_item("wood", 10.0);
    let result = add_item(&[slot(&wood, 8.0)], &wood, 5.0, 5.0, None);
    assert!(result.added);
    assert_eq!(result.inventory, vec![slot(&wood, 10.0), slot(&wood, 3.0)]);
}

#[test]
fn absorbs_what_fits_and_rejects_overflow_when_the_inventory_is_full() {
    let wood = make_item("wood", 10.0);
    let filler = slot(&make_item("stone", 99.0), 1.0);
    let result = add_item(&[slot(&wood, 8.0), filler.clone()], &wood, 5.0, 2.0, None);
    assert!(!result.added);
    assert_eq!(result.inventory, vec![slot(&wood, 10.0), filler]);
}

#[test]
#[ignore = "schema: Item.max_stack is a non-optional f64, so an item without a cap cannot be represented — enable if max_stack becomes Option<f64>"]
fn keeps_the_historical_unbounded_merge_for_items_without_a_cap() {
    // TS deletes maxStack from the item; the port cannot express "absent" for a
    // required number. With an optional max_stack this would be `max_stack: None`.
    let uncapped = make_item("wood", 99.0);
    let result = add_item(&[slot(&uncapped, 500.0)], &uncapped, 600.0, 1.0, None);
    assert!(result.added);
    assert_eq!(result.inventory[0].quantity, 1100.0);
}

// Extra coverage of the remaining helpers (inventory.ts has no tests for them).

#[test]
fn rejects_a_full_slot_when_no_overflow_slot_fits() {
    let wood = make_item("wood", 10.0);
    let filler = slot(&make_item("stone", 99.0), 1.0);
    let result = add_item(&[slot(&wood, 10.0), filler.clone()], &wood, 1.0, 2.0, None);
    assert!(!result.added);
    assert_eq!(result.inventory, vec![slot(&wood, 10.0), filler]);
}

#[test]
fn appends_a_new_slot_only_while_there_is_room() {
    let wood = make_item("wood", 99.0);
    let stone = make_item("stone", 99.0);
    let added = add_item(&[slot(&wood, 1.0)], &stone, 2.0, 2.0, None);
    assert!(added.added);
    assert_eq!(added.inventory, vec![slot(&wood, 1.0), slot(&stone, 2.0)]);
    let full = add_item(&[slot(&wood, 1.0)], &stone, 2.0, 1.0, None);
    assert!(!full.added);
    assert_eq!(full.inventory, vec![slot(&wood, 1.0)]);
}

#[test]
fn require_stackable_for_merge_appends_instead_of_merging_unstackable_items() {
    let mut hoe = make_item("hoe", 1.0);
    hoe.stackable = false;
    let options = Some(AddItemOptions { require_stackable_for_merge: Some(true) });
    let result = add_item(&[slot(&hoe, 1.0)], &hoe, 1.0, 5.0, options);
    assert!(result.added);
    assert_eq!(result.inventory, vec![slot(&hoe, 1.0), slot(&hoe, 1.0)]);
    // Without the option the slot merges (and overflows past the cap of 1).
    let merged = add_item(&[slot(&hoe, 1.0)], &hoe, 1.0, 5.0, None);
    assert_eq!(merged.inventory, vec![slot(&hoe, 1.0), slot(&hoe, 1.0)]);
    let stackable = make_item("wood", 99.0);
    let merged = add_item(&[slot(&stackable, 1.0)], &stackable, 1.0, 5.0, options);
    assert_eq!(merged.inventory, vec![slot(&stackable, 2.0)]);
}

#[test]
fn finds_slots_by_predicate_and_tool_type() {
    let mut axe = make_item("axe", 1.0);
    axe.tool_type = Some("axe".to_owned());
    let wood = make_item("wood", 99.0);
    let inventory = vec![slot(&wood, 3.0), slot(&axe, 1.0)];
    assert_eq!(find_tool_slot(&inventory, "axe"), Some(&inventory[1]));
    assert_eq!(find_tool_slot(&inventory, "hoe"), None);
    assert_eq!(find_slot(&inventory, |s| s.quantity == 3.0), Some(&inventory[0]));
    assert_eq!(find_slot(&inventory, |s| s.quantity == 9.0), None);
}

#[test]
fn replace_item_swaps_the_item_object_in_every_matching_slot() {
    let wood = make_item("wood", 99.0);
    let stone = make_item("stone", 99.0);
    let mut renamed = wood.clone();
    renamed.name = "Timber".to_owned();
    let inventory = vec![slot(&wood, 3.0), slot(&stone, 1.0), slot(&wood, 4.0)];
    let next = replace_item(&inventory, "wood", &renamed);
    assert_eq!(next, vec![slot(&renamed, 3.0), slot(&stone, 1.0), slot(&renamed, 4.0)]);
    assert_eq!(inventory[0].item.name, "wood");
}
