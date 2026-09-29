//! Inventory (port of `Inventory.cs` / inventory.ts). Pure list helpers: they return new lists.
//!
//! Pure inventory helpers. All return new lists; slots are copied on write.

use crate::schema::{InventorySlot, Item};

/// TS `AddItemResult`.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct AddItemResult {
    pub inventory: Vec<InventorySlot>,
    pub added: bool,
}

/// TS `addItem` options bag `{ requireStackableForMerge?: boolean }`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct AddItemOptions {
    pub require_stackable_for_merge: Option<bool>,
}

/// Add `quantity` of `item`. Mirrors the historical app semantics:
/// an existing slot with the same item id absorbs the quantity (optionally
/// only when the item is stackable); otherwise a new slot is appended if
/// there is room.
pub fn add_item(
    inventory: &[InventorySlot],
    item: &Item,
    quantity: u32,
    max_inventory_size: u32,
    options: Option<AddItemOptions>,
) -> AddItemResult {
    let options = options.unwrap_or_default();
    let index = inventory.iter().position(|slot| slot.item.id == item.id);
    if let Some(index) = index {
        if options.require_stackable_for_merge != Some(true) || inventory[index].item.stackable {
            let slot = &inventory[index];
            // Honor an authored stack cap (the item editor writes maxStack); items
            // without one keep the historical unbounded-merge behavior.
            // NOTE: Item.max_stack is a required number in the schema, so it is never
            // "undefined"; the Option view keeps this branch valid if the schema ever
            // makes it optional.
            let cap: Option<u32> = Some(slot.item.max_stack);
            if let Some(cap_value) = cap {
                if u64::from(slot.quantity) + u64::from(quantity) > u64::from(cap_value) {
                    let room_in_slot = cap_value.saturating_sub(slot.quantity);
                    let overflow = quantity - room_in_slot;
                    if slot_count(inventory) >= max_inventory_size {
                        // No room for an overflow slot: absorb what fits, reject the rest.
                        if room_in_slot == 0 {
                            return AddItemResult { inventory: inventory.to_vec(), added: false };
                        }
                        let capped = with_quantity_at(inventory, index, cap_value);
                        return AddItemResult { inventory: capped, added: false };
                    }
                    let mut next = with_quantity_at(inventory, index, cap_value);
                    next.push(InventorySlot { item: item.clone(), quantity: overflow });
                    return AddItemResult { inventory: next, added: true };
                }
            }
            let merged = with_quantity_at(inventory, index, slot.quantity.saturating_add(quantity));
            return AddItemResult { inventory: merged, added: true };
        }
    }
    if slot_count(inventory) < max_inventory_size {
        let mut next = inventory.to_vec();
        next.push(InventorySlot { item: item.clone(), quantity });
        return AddItemResult { inventory: next, added: true };
    }
    AddItemResult { inventory: inventory.to_vec(), added: false }
}

/// The number of slots, for comparing with a `u32` inventory size.
fn slot_count(inventory: &[InventorySlot]) -> u32 {
    u32::try_from(inventory.len()).unwrap_or(u32::MAX)
}

/// TS `inventory.map((s, i) => i === index ? { ...s, quantity } : s)`.
fn with_quantity_at(inventory: &[InventorySlot], index: usize, quantity: u32) -> Vec<InventorySlot> {
    inventory
        .iter()
        .enumerate()
        .map(|(i, slot)| if i == index { InventorySlot { quantity, ..slot.clone() } } else { slot.clone() })
        .collect()
}

/// Remove `quantity` of `item_id`, draining across EVERY slot holding it;
/// deletes emptied slots. Multiple slots per item id can exist (stack caps,
/// pack reconciliation), and `has_ingredients` counts across all of them —
/// consuming from only the first slot allowed item duplication.
pub fn remove_item(inventory: &[InventorySlot], item_id: &str, quantity: u32) -> Vec<InventorySlot> {
    if !inventory.iter().any(|slot| slot.item.id == item_id) {
        return inventory.to_vec();
    }
    let mut remaining_to_remove = quantity;
    let mut next = Vec::new();
    for slot in inventory {
        if slot.item.id != item_id || remaining_to_remove == 0 {
            next.push(slot.clone());
            continue;
        }
        let removed = slot.quantity.min(remaining_to_remove);
        remaining_to_remove -= removed;
        if slot.quantity > removed {
            next.push(InventorySlot { quantity: slot.quantity - removed, ..slot.clone() });
        }
    }
    next
}

pub fn find_slot(inventory: &[InventorySlot], predicate: impl Fn(&InventorySlot) -> bool) -> Option<&InventorySlot> {
    inventory.iter().find(|slot| predicate(slot))
}

pub fn find_tool_slot<'a>(inventory: &'a [InventorySlot], tool_type: &str) -> Option<&'a InventorySlot> {
    inventory.iter().find(|slot| slot.item.tool_type.as_deref() == Some(tool_type))
}

/// Replace the item object in the slot matching `item_id` (e.g. durability change).
pub fn replace_item(inventory: &[InventorySlot], item_id: &str, item: &Item) -> Vec<InventorySlot> {
    inventory
        .iter()
        .map(
            |slot| {
                if slot.item.id == item_id {
                    InventorySlot { item: item.clone(), ..slot.clone() }
                } else {
                    slot.clone()
                }
            },
        )
        .collect()
}
