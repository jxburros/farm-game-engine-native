//! Inventory (port of `Inventory.cs` / inventory.ts). Pure list helpers: they return new lists.

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

pub fn add_item(
    inventory: &[InventorySlot],
    item: &Item,
    quantity: f64,
    max_inventory_size: f64,
    options: Option<AddItemOptions>,
) -> AddItemResult {
    let _ = (inventory, item, quantity, max_inventory_size, options);
    todo!("port Inventory.AddItem")
}

pub fn remove_item(inventory: &[InventorySlot], item_id: &str, quantity: f64) -> Vec<InventorySlot> {
    let _ = (inventory, item_id, quantity);
    todo!("port Inventory.RemoveItem")
}

pub fn find_slot(inventory: &[InventorySlot], predicate: impl Fn(&InventorySlot) -> bool) -> Option<&InventorySlot> {
    inventory.iter().find(|slot| predicate(slot))
}

pub fn find_tool_slot<'a>(inventory: &'a [InventorySlot], tool_type: &str) -> Option<&'a InventorySlot> {
    let _ = (inventory, tool_type);
    todo!("port Inventory.FindToolSlot")
}

pub fn replace_item(inventory: &[InventorySlot], item_id: &str, item: &Item) -> Vec<InventorySlot> {
    let _ = (inventory, item_id, item);
    todo!("port Inventory.ReplaceItem")
}
