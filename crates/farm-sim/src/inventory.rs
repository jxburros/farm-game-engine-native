//! Inventory (port of `Inventory.cs` / inventory.ts). Pure list helpers: they return new lists.
//!
//! Pure inventory helpers. All return new lists; slots are copied on write.
//!
//! Stacking rules (every add path goes through [`add_item`]):
//! - a slot holds one item id at one crop quality; slots of different qualities never merge;
//! - a slot holds at most the item's `maxStack` units, where a `maxStack` of 0 means "no cap"
//!   (an item authored without one, or the historical unbounded merge);
//! - the inventory holds at most `maxInventorySize` slots, except for slots that were already
//!   there (an old save, a debug edit): adds never make an over-full inventory worse.

use crate::schema::{crop_qualities, InventorySlot, Item};

/// TS `AddItemResult`, plus how much of the request fit.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct AddItemResult {
    /// The inventory with every unit that fit. When not everything fit, it still holds the
    /// part that did (`added_quantity`): callers that want all-or-nothing keep their old list.
    pub inventory: Vec<InventorySlot>,
    /// Every requested unit fit.
    pub added: bool,
    /// How many of the requested units fit (`added_quantity + rejected == requested`).
    pub added_quantity: u32,
}

impl AddItemResult {
    /// The units that did not fit.
    pub fn rejected(&self, requested: u32) -> u32 {
        requested.saturating_sub(self.added_quantity)
    }
}

/// TS `addItem` options bag `{ requireStackableForMerge?: boolean }`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct AddItemOptions {
    pub require_stackable_for_merge: Option<bool>,
}

/// The most units of `item` one slot holds: `maxStack`, where 0 means no cap.
pub fn stack_cap(item: &Item) -> u32 {
    if item.max_stack == 0 {
        u32::MAX
    } else {
        item.max_stack
    }
}

/// The quality a slot records: `None` for normal quality (and for anything that is not a crop
/// quality), so normal-quality units stack with slots written before qualities existed.
pub fn slot_quality(quality: Option<&str>) -> Option<String> {
    quality.filter(|q| *q != crop_qualities::NORMAL && crop_qualities::ALL.contains(q)).map(str::to_owned)
}

/// Add `quantity` of `item` at normal quality. See [`add_item_with_quality`].
pub fn add_item(
    inventory: &[InventorySlot],
    item: &Item,
    quantity: u32,
    max_inventory_size: u32,
    options: Option<AddItemOptions>,
) -> AddItemResult {
    add_item_with_quality(inventory, item, None, quantity, max_inventory_size, options)
}

/// Add `quantity` of `item` at a crop `quality` in one pass: top up every slot already holding
/// the item at that quality to its stack cap (the cap of the item being added), then open new
/// slots of at most one cap each while the inventory has room. With
/// `require_stackable_for_merge`, an unstackable item never merges into existing slots.
///
/// The result says how much fit; the units that did not fit are not added anywhere.
pub fn add_item_with_quality(
    inventory: &[InventorySlot],
    item: &Item,
    quality: Option<&str>,
    quantity: u32,
    max_inventory_size: u32,
    options: Option<AddItemOptions>,
) -> AddItemResult {
    let options = options.unwrap_or_default();
    let quality = slot_quality(quality);
    let cap = stack_cap(item);
    let merge = options.require_stackable_for_merge != Some(true) || item.stackable;
    let mut next = inventory.to_vec();
    let mut remaining = quantity;

    if merge {
        for slot in next.iter_mut().filter(|slot| slot.item.id == item.id && slot.quality == quality) {
            if remaining == 0 {
                break;
            }
            // A held copy can predate a change to the item's stack size: neither cap is passed
            // (#116). Copies of the current definition have the same cap.
            let take = cap.min(stack_cap(&slot.item)).saturating_sub(slot.quantity).min(remaining);
            slot.quantity += take;
            remaining -= take;
        }
    }
    while remaining > 0 && slot_count(&next) < max_inventory_size {
        let take = cap.min(remaining);
        next.push(InventorySlot { item: item.clone(), quantity: take, quality: quality.clone() });
        remaining -= take;
    }

    AddItemResult { inventory: next, added: remaining == 0, added_quantity: quantity - remaining }
}

/// The number of slots, for comparing with a `u32` inventory size.
fn slot_count(inventory: &[InventorySlot]) -> u32 {
    u32::try_from(inventory.len()).unwrap_or(u32::MAX)
}

/// The units of `item_id` held across every slot (any quality).
pub fn count_item(inventory: &[InventorySlot], item_id: &str) -> u64 {
    inventory.iter().filter(|slot| slot.item.id == item_id).map(|slot| u64::from(slot.quantity)).sum()
}

/// The units of `item_id` at one quality (`None`: normal) held across every slot.
pub fn count_item_with_quality(inventory: &[InventorySlot], item_id: &str, quality: Option<&str>) -> u64 {
    let quality = slot_quality(quality);
    inventory
        .iter()
        .filter(|slot| slot.item.id == item_id && slot.quality == quality)
        .map(|slot| u64::from(slot.quantity))
        .sum()
}

/// Remove `quantity` of `item_id` at one quality (`None`: normal), draining across every slot
/// holding it; deletes emptied slots.
pub fn remove_item_with_quality(
    inventory: &[InventorySlot],
    item_id: &str,
    quality: Option<&str>,
    quantity: u32,
) -> Vec<InventorySlot> {
    let quality = slot_quality(quality);
    let mut remaining_to_remove = quantity;
    let mut next = Vec::with_capacity(inventory.len());
    for slot in inventory {
        if slot.item.id != item_id || slot.quality != quality || remaining_to_remove == 0 {
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

/// The first slot matching `predicate`.
pub fn find_slot(inventory: &[InventorySlot], predicate: impl Fn(&InventorySlot) -> bool) -> Option<&InventorySlot> {
    inventory.iter().find(|slot| predicate(slot))
}

/// The first slot holding a tool of `tool_type`.
pub fn find_tool_slot<'a>(inventory: &'a [InventorySlot], tool_type: &str) -> Option<&'a InventorySlot> {
    inventory.iter().find(|slot| slot.item.tool_type.as_deref() == Some(tool_type))
}

/// A held (or dropped) copy of an item brought up to date with its `current` definition, keeping
/// the instance data the copy carries: a tool's `durability` stays (clamped to the new
/// `maxDurability`). A tool that became unbreakable loses it; a tool that became breakable
/// starts at full durability.
pub fn refresh_item(saved: &Item, current: &Item) -> Item {
    let durability = match (saved.durability, current.max_durability) {
        (Some(durability), Some(max)) => Some(durability.min(max)),
        (Some(_), None) => current.durability,
        (None, _) => current.durability,
    };
    Item { durability, ..current.clone() }
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
