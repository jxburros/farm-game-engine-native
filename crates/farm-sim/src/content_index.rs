//! Id → position tables over [`GameContent`]'s definition lists, built once per
//! [`crate::EngineContext`] so a lookup by id costs a hash probe instead of a scan of the list
//! (content packs can add thousands of items, and collision and pathfinding look node and
//! machine types up for every tile they test).
//!
//! A lookup answers what `list.iter().find(|def| def.id == id)` would: the table keeps the first
//! definition of an id. Content edited after the table was built (tests and benchmarks change
//! `ctx.content` in place) is caught by the list length and the id of the hit, and a miss scans
//! the list, so a stale table only costs time; code that edits content in place should still
//! call [`crate::EngineContext::reindex`] afterwards.

use crate::schema::{
    ActionDef, AnimalSpeciesDefinition, Dialogue, GameContent, Item, MachineTypeDefinition, MinigameDef,
    NodeTypeDefinition, Npc, Quest, RecipeDefinition, Scene, ShopDefinition,
};
use indexmap::IndexMap;

/// Positions of the first definition of each id in one content list.
#[derive(Debug, Clone, Default)]
pub struct IdTable {
    positions: IndexMap<String, usize>,
    /// The length of the list the table was built from.
    len: usize,
}

impl IdTable {
    fn build<T>(list: &[T], id: impl Fn(&T) -> &str) -> Self {
        let mut table = IndexMap::with_capacity(list.len());
        for (index, definition) in list.iter().enumerate() {
            table.entry(id(definition).to_owned()).or_insert(index);
        }
        Self { positions: table, len: list.len() }
    }

    /// The first definition in `list` whose id is `key`.
    pub fn find<'a, T>(&self, list: &'a [T], key: &str, id: impl Fn(&T) -> &str) -> Option<&'a T> {
        if list.len() == self.len {
            if let Some(definition) = self.positions.get(key).and_then(|&index| list.get(index)) {
                if id(definition) == key {
                    return Some(definition);
                }
            }
        }
        // A miss (unknown ids are the rare, failing path), or a table older than the list: scan.
        list.iter().find(|definition| id(definition) == key)
    }
}

/// The id tables of a [`GameContent`] (see the module docs).
#[derive(Debug, Clone, Default)]
pub struct ContentIndex {
    pub items: IdTable,
    pub npcs: IdTable,
    pub dialogues: IdTable,
    pub quests: IdTable,
    pub shops: IdTable,
    pub node_types: IdTable,
    pub recipes: IdTable,
    pub machine_types: IdTable,
    pub animal_species: IdTable,
    pub actions: IdTable,
    pub minigames: IdTable,
    pub scenes: IdTable,
}

impl ContentIndex {
    /// Index every definition list of `content`.
    pub fn build(content: &GameContent) -> Self {
        Self {
            items: IdTable::build(&content.items, item_id),
            npcs: IdTable::build(&content.npcs, npc_id),
            dialogues: IdTable::build(&content.dialogues, dialogue_id),
            quests: IdTable::build(&content.quests, quest_id),
            shops: IdTable::build(&content.shops, shop_id),
            node_types: IdTable::build(&content.node_types, node_type_id),
            recipes: IdTable::build(&content.recipes, recipe_id),
            machine_types: IdTable::build(&content.machine_types, machine_type_id),
            animal_species: IdTable::build(&content.animal_species, animal_species_id),
            actions: IdTable::build(&content.actions, action_id),
            minigames: IdTable::build(&content.minigames, minigame_id),
            scenes: IdTable::build(&content.scenes, scene_id),
        }
    }
}

// The id of each definition kind (named functions, so `build` and the lookups agree).
pub(crate) fn item_id(def: &Item) -> &str {
    &def.id
}
pub(crate) fn npc_id(def: &Npc) -> &str {
    &def.id
}
pub(crate) fn dialogue_id(def: &Dialogue) -> &str {
    &def.id
}
pub(crate) fn quest_id(def: &Quest) -> &str {
    &def.id
}
pub(crate) fn shop_id(def: &ShopDefinition) -> &str {
    &def.id
}
pub(crate) fn node_type_id(def: &NodeTypeDefinition) -> &str {
    &def.id
}
pub(crate) fn recipe_id(def: &RecipeDefinition) -> &str {
    &def.id
}
pub(crate) fn machine_type_id(def: &MachineTypeDefinition) -> &str {
    &def.id
}
pub(crate) fn animal_species_id(def: &AnimalSpeciesDefinition) -> &str {
    &def.id
}
pub(crate) fn action_id(def: &ActionDef) -> &str {
    &def.id
}
pub(crate) fn minigame_id(def: &MinigameDef) -> &str {
    &def.id
}
pub(crate) fn scene_id(def: &Scene) -> &str {
    &def.id
}

#[cfg(test)]
mod tests {
    use super::*;

    fn item(id: &str, name: &str) -> Item {
        Item { id: id.to_owned(), name: name.to_owned(), ..Item::default() }
    }

    #[test]
    fn finds_the_first_definition_like_a_scan() {
        let items = vec![item("a", "first"), item("b", "b"), item("a", "second")];
        let table = IdTable::build(&items, item_id);
        assert_eq!(table.find(&items, "a", item_id).map(|def| def.name.as_str()), Some("first"));
        assert_eq!(table.find(&items, "b", item_id).map(|def| def.name.as_str()), Some("b"));
        assert!(table.find(&items, "c", item_id).is_none());
    }

    #[test]
    fn a_stale_table_falls_back_to_the_scan() {
        let items = vec![item("a", "a"), item("b", "b")];
        let table = IdTable::build(&items, item_id);
        let changed = vec![item("c", "c"), item("a", "moved"), item("d", "new")];
        assert_eq!(table.find(&changed, "a", item_id).map(|def| def.name.as_str()), Some("moved"));
        assert_eq!(table.find(&changed, "d", item_id).map(|def| def.name.as_str()), Some("new"));
        assert!(table.find(&changed, "b", item_id).is_none());
        assert!(table.find(&[], "a", item_id).is_none());
    }
}
