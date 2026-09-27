//! Content pack loading (M5, port of `Packs.cs` / packs.ts): deterministic load order,
//! `packId:localId` namespacing, explicit override semantics. Conflicts surface as problems for
//! the Problems panel — never silent last-wins.

use crate::farming::crops::to_custom_crop_definition;
use crate::schema::{
    is_engine_compatible, ContentPack, CropDefinition, CustomCropDefinition, GameContent, GameProject, GameState,
    InventorySlot, Item, PackContent, PackInstallation, SavePackRef, ENGINE_VERSION,
};
use indexmap::{IndexMap, IndexSet};
use serde::de::DeserializeOwned;
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

/// A pack load/merge problem for the Problems panel (TS `PackProblem`).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PackProblem {
    pub pack_id: String,
    /// 'error' | 'warning'.
    pub severity: String,
    pub message: String,
}

impl PackProblem {
    fn error(pack_id: &str, message: String) -> Self {
        Self { pack_id: pack_id.to_owned(), severity: "error".to_owned(), message }
    }

    fn warning(pack_id: &str, message: String) -> Self {
        Self { pack_id: pack_id.to_owned(), severity: "warning".to_owned(), message }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ResolvePackOrderResult {
    pub packs: Vec<ContentPack>,
    pub problems: Vec<PackProblem>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MergePacksIntoContentResult {
    pub content: GameContent,
    pub problems: Vec<PackProblem>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ApplyPackToProjectResult {
    pub project: GameProject,
    pub problems: Vec<PackProblem>,
}

/// Namespace a local id into a pack: ids that already contain ':' are absolute.
pub fn namespaced_id(pack_id: &str, id: &str) -> String {
    if id.contains(':') {
        id.to_owned()
    } else {
        format!("{pack_id}:{id}")
    }
}

/// The pack prefix of a namespaced id, or None for plain (base) ids.
pub fn pack_id_of(id: &str) -> Option<&str> {
    match id.find(':') {
        Some(index) if index > 0 => Some(&id[..index]),
        _ => None,
    }
}

/// Object keys that hold references to other definitions. During namespacing these are rewritten
/// only when the value refers to an id defined in the same pack — references to base/global
/// content must stay untouched, and cross-pack references must be written fully qualified by the
/// author.
const REFERENCE_KEYS: &[&str] = &[
    "itemId",
    "cropType",
    "machineTypeId",
    "feedItemId",
    "productItemId",
    "npcId",
    "dialogueId",
    "nextDialogueId",
    "openShopId",
    "offerQuestId",
    "questId",
    "targetNPCId",
    "targetCropType",
    "shopId",
    "giver",
    "typeId",
    "speciesId",
    "recipeId",
    "nodeTypeId",
    "weatherId",
    "targetItemId",
    "requiredItemId",
    "giftItemId",
    "actionId",
    "minigameId",
    "useActionId",
];

/// Keys holding arrays of reference strings.
const REFERENCE_LIST_KEYS: &[&str] = &["prerequisites"];

fn rewrite_references(value: &Value, pack_id: &str, local_ids: &IndexSet<String>) -> Value {
    match value {
        Value::Array(items) => {
            Value::Array(items.iter().map(|entry| rewrite_references(entry, pack_id, local_ids)).collect())
        }
        Value::Object(source) => {
            let mut result = Map::new();
            for (key, entry) in source {
                let rewritten = match entry {
                    Value::String(reference)
                        if REFERENCE_KEYS.contains(&key.as_str()) && local_ids.contains(reference) =>
                    {
                        Value::String(namespaced_id(pack_id, reference))
                    }
                    Value::Array(list) if REFERENCE_LIST_KEYS.contains(&key.as_str()) => Value::Array(
                        list.iter()
                            .map(|item| match item {
                                Value::String(s) if local_ids.contains(s) => Value::String(namespaced_id(pack_id, s)),
                                other => other.clone(),
                            })
                            .collect(),
                    ),
                    other => rewrite_references(other, pack_id, local_ids),
                };
                result.insert(key.clone(), rewritten);
            }
            Value::Object(result)
        }
        other => other.clone(),
    }
}

/// Rewrite a typed definition through its JSON shape (TS operates on plain objects), optionally
/// namespacing its top-level `id`. A definition always round-trips through its own JSON, so the
/// fallbacks only guard a type-level bug; the goldens would show it as a mismatch.
fn rewrite_definition<T: Serialize + DeserializeOwned + Clone>(
    definition: &T,
    pack_id: &str,
    local_ids: &IndexSet<String>,
    namespace_id: bool,
) -> T {
    let Ok(json) = serde_json::to_value(definition) else {
        return definition.clone();
    };
    let mut rewritten = rewrite_references(&json, pack_id, local_ids);
    if namespace_id {
        if let Some(Value::String(id)) = rewritten.get("id") {
            let namespaced = namespaced_id(pack_id, id);
            if let Some(object) = rewritten.as_object_mut() {
                object.insert("id".to_owned(), Value::String(namespaced));
            }
        }
    }
    serde_json::from_value(rewritten).unwrap_or_else(|_| definition.clone())
}

fn rewrite_collection<T: Serialize + DeserializeOwned + Clone>(
    definitions: &[T],
    pack_id: &str,
    local_ids: &IndexSet<String>,
) -> Vec<T> {
    definitions.iter().map(|definition| rewrite_definition(definition, pack_id, local_ids, true)).collect()
}

/// Definition ids per collection, in TS `CONTENT_COLLECTIONS` order.
fn collection_ids(content: &PackContent) -> Vec<&str> {
    let mut ids: Vec<&str> = Vec::new();
    ids.extend(content.crops.iter().map(|d| d.id.as_str()));
    ids.extend(content.items.iter().map(|d| d.id.as_str()));
    ids.extend(content.recipes.iter().map(|d| d.id.as_str()));
    ids.extend(content.machine_types.iter().map(|d| d.id.as_str()));
    ids.extend(content.node_types.iter().map(|d| d.id.as_str()));
    ids.extend(content.animal_species.iter().map(|d| d.id.as_str()));
    ids.extend(content.fish_tables.iter().map(|d| d.id.as_str()));
    ids.extend(content.weather_types.iter().map(|d| d.id.as_str()));
    ids.extend(content.npcs.iter().map(|d| d.id.as_str()));
    ids.extend(content.dialogues.iter().map(|d| d.id.as_str()));
    ids.extend(content.scenes.iter().map(|d| d.id.as_str()));
    ids.extend(content.events.iter().map(|d| d.id.as_str()));
    ids.extend(content.quests.iter().map(|d| d.id.as_str()));
    ids.extend(content.shops.iter().map(|d| d.id.as_str()));
    ids.extend(content.actions.iter().map(|d| d.id.as_str()));
    ids.extend(content.minigames.iter().map(|d| d.id.as_str()));
    ids
}

fn collect_local_ids(pack: &ContentPack) -> IndexSet<String> {
    let mut ids = IndexSet::new();
    for id in collection_ids(&pack.content) {
        if !id.contains(':') {
            ids.insert(id.to_owned());
        }
    }
    // NPC dialogue arrays embed dialogues with their own ids.
    for npc in &pack.content.npcs {
        for dialogue in &npc.dialogue {
            if !dialogue.id.contains(':') {
                ids.insert(dialogue.id.clone());
            }
        }
    }
    ids
}

/// Return a copy of the pack with every definition id and intra-pack reference rewritten to
/// `packId:localId`. Base packs pass through as-is.
pub fn namespace_pack(pack: &ContentPack) -> ContentPack {
    if pack.manifest.base {
        return pack.clone();
    }
    let pack_id = pack.manifest.id.as_str();
    let local_ids = collect_local_ids(pack);

    let source = &pack.content;
    let mut content = PackContent {
        crops: rewrite_collection(&source.crops, pack_id, &local_ids),
        items: rewrite_collection(&source.items, pack_id, &local_ids),
        recipes: rewrite_collection(&source.recipes, pack_id, &local_ids),
        machine_types: rewrite_collection(&source.machine_types, pack_id, &local_ids),
        node_types: rewrite_collection(&source.node_types, pack_id, &local_ids),
        animal_species: rewrite_collection(&source.animal_species, pack_id, &local_ids),
        fish_tables: rewrite_collection(&source.fish_tables, pack_id, &local_ids),
        weather_types: rewrite_collection(&source.weather_types, pack_id, &local_ids),
        npcs: rewrite_collection(&source.npcs, pack_id, &local_ids),
        dialogues: rewrite_collection(&source.dialogues, pack_id, &local_ids),
        scenes: rewrite_collection(&source.scenes, pack_id, &local_ids),
        events: rewrite_collection(&source.events, pack_id, &local_ids),
        quests: rewrite_collection(&source.quests, pack_id, &local_ids),
        shops: rewrite_collection(&source.shops, pack_id, &local_ids),
        actions: rewrite_collection(&source.actions, pack_id, &local_ids),
        minigames: rewrite_collection(&source.minigames, pack_id, &local_ids),
        player_start: source.player_start.as_ref().map(|start| rewrite_definition(start, pack_id, &local_ids, false)),
        strings: source.strings.clone(),
        extra: source.extra.clone(),
    };
    // String-table keys embed ids (`item:{id}:name`) — namespace those too.
    if !source.strings.is_empty() {
        let mut strings = IndexMap::new();
        for (locale, table) in &source.strings {
            let mut rewritten = IndexMap::new();
            for (key, value) in table {
                let parts: Vec<&str> = key.split(':').collect();
                if parts.len() >= 3 {
                    let middle = parts[1..parts.len() - 1].join(":");
                    if local_ids.contains(&middle) {
                        let last = parts[parts.len() - 1];
                        rewritten.insert(
                            format!("{}:{}:{}", parts[0], namespaced_id(pack_id, &middle), last),
                            value.clone(),
                        );
                        continue;
                    }
                }
                rewritten.insert(key.clone(), value.clone());
            }
            strings.insert(locale.clone(), rewritten);
        }
        content.strings = strings;
    }
    ContentPack { manifest: pack.manifest.clone(), content, plugins: pack.plugins.clone(), extra: pack.extra.clone() }
}

/// Apply per-locale string tables from enabled packs to game content (M7 i18n). Authored text is
/// the fallback: unknown locales and missing keys change nothing. Later packs override earlier
/// ones, mirroring load order.
pub fn apply_locale_strings(content: GameContent, installs: &[PackInstallation], locale: &str) -> GameContent {
    if locale.is_empty() {
        return content;
    }
    let packs = resolve_pack_order(installs).packs;
    let mut table: IndexMap<String, String> = IndexMap::new();
    for raw_pack in &packs {
        let pack = namespace_pack(raw_pack);
        if let Some(locale_table) = pack.content.strings.get(locale) {
            for (key, value) in locale_table {
                table.insert(key.clone(), value.clone());
            }
        }
    }
    if table.is_empty() {
        return content;
    }

    let lookup = |kind: &str, id: &str, field: &str| table.get(&format!("{kind}:{id}:{field}")).cloned();

    let mut content = content;
    for item in &mut content.items {
        if let Some(name) = lookup("item", &item.id, "name") {
            item.name = name;
        }
        if let Some(description) = lookup("item", &item.id, "description") {
            item.description = description;
        }
    }
    for quest in &mut content.quests {
        if let Some(name) = lookup("quest", &quest.id, "name") {
            quest.name = name;
        }
        if let Some(description) = lookup("quest", &quest.id, "description") {
            quest.description = description;
        }
    }
    for dialogue in &mut content.dialogues {
        if let Some(text) = lookup("dialogue", &dialogue.id, "text") {
            dialogue.text = text;
        }
    }
    for npc in &mut content.npcs {
        if let Some(name) = lookup("npc", &npc.id, "name") {
            npc.name = name;
        }
        for dialogue in &mut npc.dialogue {
            if let Some(text) = lookup("dialogue", &dialogue.id, "text") {
                dialogue.text = text;
            }
        }
    }
    content
}

struct OrderResolver<'a> {
    by_id: IndexMap<&'a str, &'a ContentPack>,
    ordered: Vec<ContentPack>,
    visiting: IndexSet<&'a str>,
    done: IndexSet<&'a str>,
    problems: Vec<PackProblem>,
}

impl<'a> OrderResolver<'a> {
    fn visit(&mut self, pack: &'a ContentPack) {
        let id = pack.manifest.id.as_str();
        if self.done.contains(id) {
            return;
        }
        if self.visiting.contains(id) {
            self.problems.push(PackProblem::error(id, format!("Dependency cycle involving pack '{id}'")));
            return;
        }
        self.visiting.insert(id);
        for dependency in &pack.manifest.dependencies {
            match self.by_id.get(dependency.pack_id.as_str()) {
                Some(target) => self.visit(target),
                None => self.problems.push(PackProblem::error(
                    id,
                    format!("Pack '{id}' depends on '{}' which is not installed/enabled", dependency.pack_id),
                )),
            }
        }
        self.visiting.swap_remove(id);
        self.done.insert(id);
        self.ordered.push(pack.clone());
    }
}

/// Deterministic load order: install order, but a pack's dependencies always load before it.
/// Missing dependencies and cycles surface as problems (the pack still loads so partial setups
/// stay inspectable).
pub fn resolve_pack_order(installs: &[PackInstallation]) -> ResolvePackOrderResult {
    let enabled: Vec<&ContentPack> =
        installs.iter().filter(|install| install.enabled).map(|install| &install.pack).collect();
    // `new Map(entries)`: a later duplicate id replaces the earlier value.
    let mut by_id: IndexMap<&str, &ContentPack> = IndexMap::new();
    for pack in &enabled {
        by_id.insert(pack.manifest.id.as_str(), pack);
    }

    let mut resolver = OrderResolver {
        by_id,
        ordered: Vec::new(),
        visiting: IndexSet::new(),
        done: IndexSet::new(),
        problems: Vec::new(),
    };
    for pack in &enabled {
        resolver.visit(pack);
    }
    let OrderResolver { ordered, mut problems, .. } = resolver;

    for pack in &ordered {
        if !is_engine_compatible(Some(&pack.manifest.engine_compatibility), ENGINE_VERSION) {
            problems.push(PackProblem::warning(
                &pack.manifest.id,
                format!(
                    "Pack '{}' targets engine {}; this engine is {ENGINE_VERSION}",
                    pack.manifest.id, pack.manifest.engine_compatibility
                ),
            ));
        }
    }

    ResolvePackOrderResult { packs: ordered, problems }
}

/// TS `MergeTarget`: where a pack collection's definitions land.
trait MergeTarget<T> {
    fn has(&self, id: &str) -> bool;
    fn set(&mut self, definition: T);
    fn replace(&mut self, id: &str, definition: T);
}

/// An id-keyed array (`array.find(entry => entry.id === id)` semantics).
struct ArrayTarget<'a, T, F: Fn(&T) -> &str> {
    array: &'a mut Vec<T>,
    id_of: F,
}

impl<T, F: Fn(&T) -> &str> MergeTarget<T> for ArrayTarget<'_, T, F> {
    fn has(&self, id: &str) -> bool {
        self.array.iter().any(|entry| (self.id_of)(entry) == id)
    }

    fn set(&mut self, definition: T) {
        self.array.push(definition);
    }

    fn replace(&mut self, id: &str, definition: T) {
        if let Some(index) = self.array.iter().position(|entry| (self.id_of)(entry) == id) {
            self.array[index] = definition;
        }
    }
}

impl MergeTarget<CropDefinition> for IndexMap<String, CropDefinition> {
    fn has(&self, id: &str) -> bool {
        self.contains_key(id)
    }

    fn set(&mut self, definition: CropDefinition) {
        self.insert(definition.id.clone(), definition);
    }

    fn replace(&mut self, _id: &str, definition: CropDefinition) {
        self.insert(definition.id.clone(), definition);
    }
}

/// A CropDefinition stored as a project custom crop (TS structural typing).
struct CustomCropTarget<'a> {
    crops: &'a mut Vec<CustomCropDefinition>,
}

impl MergeTarget<CropDefinition> for CustomCropTarget<'_> {
    fn has(&self, id: &str) -> bool {
        self.crops.iter().any(|entry| entry.id == id)
    }

    fn set(&mut self, definition: CropDefinition) {
        self.crops.push(to_custom_crop_definition(&definition));
    }

    fn replace(&mut self, id: &str, definition: CropDefinition) {
        if let Some(index) = self.crops.iter().position(|entry| entry.id == id) {
            self.crops[index] = to_custom_crop_definition(&definition);
        }
    }
}

/// The per-pack `merge` closure: skips empty/missing collections.
struct PackMerger<'a> {
    pack: &'a ContentPack,
    problems: &'a mut Vec<PackProblem>,
}

impl PackMerger<'_> {
    fn merge<T: Clone>(
        &mut self,
        definitions: &[T],
        id_of: impl Fn(&T) -> &str,
        target: &mut dyn MergeTarget<T>,
        label: &str,
    ) {
        let pack_id = self.pack.manifest.id.as_str();
        for definition in definitions {
            let id = id_of(definition);
            if !target.has(id) {
                target.set(definition.clone());
            } else if self.pack.manifest.overrides.iter().any(|entry| entry == id) {
                target.replace(id, definition.clone());
            } else {
                self.problems.push(PackProblem::warning(
                    pack_id,
                    format!(
                        "Pack '{pack_id}' redefines {label} '{id}' without declaring it in manifest.overrides — keeping the earlier definition"
                    ),
                ));
            }
        }
    }

    fn merge_array<T: Clone>(
        &mut self,
        definitions: &[T],
        id_of: impl Fn(&T) -> &str + Copy,
        array: &mut Vec<T>,
        label: &str,
    ) {
        self.merge(definitions, id_of, &mut ArrayTarget { array, id_of }, label);
    }
}

/// The collections every pack merges into, after the crops (which differ between content and
/// projects). One place so the two merge paths cannot drift.
struct MergeSlots<'a> {
    items: &'a mut Vec<Item>,
    recipes: &'a mut Vec<crate::schema::RecipeDefinition>,
    machine_types: &'a mut Vec<crate::schema::MachineTypeDefinition>,
    node_types: &'a mut Vec<crate::schema::NodeTypeDefinition>,
    animal_species: &'a mut Vec<crate::schema::AnimalSpeciesDefinition>,
    fish_tables: &'a mut Vec<crate::schema::FishTable>,
    weather_types: &'a mut Vec<crate::schema::WeatherTypeDefinition>,
    npcs: &'a mut Vec<crate::schema::Npc>,
    dialogues: &'a mut Vec<crate::schema::Dialogue>,
    scenes: &'a mut Vec<crate::schema::Scene>,
    events: &'a mut Vec<crate::schema::GameEvent>,
    quests: &'a mut Vec<crate::schema::Quest>,
    shops: &'a mut Vec<crate::schema::ShopDefinition>,
    actions: &'a mut Vec<crate::schema::ActionDef>,
    minigames: &'a mut Vec<crate::schema::MinigameDef>,
}

fn merge_pack(
    pack: &ContentPack,
    crops: &mut dyn MergeTarget<CropDefinition>,
    slots: MergeSlots<'_>,
    problems: &mut Vec<PackProblem>,
) {
    let mut merger = PackMerger { pack, problems };
    let c = &pack.content;
    merger.merge(&c.crops, |d| &d.id, crops, "crop");
    merger.merge_array(&c.items, |d| &d.id, slots.items, "item");
    merger.merge_array(&c.recipes, |d| &d.id, slots.recipes, "recipe");
    merger.merge_array(&c.machine_types, |d| &d.id, slots.machine_types, "machine type");
    merger.merge_array(&c.node_types, |d| &d.id, slots.node_types, "node type");
    merger.merge_array(&c.animal_species, |d| &d.id, slots.animal_species, "animal species");
    merger.merge_array(&c.fish_tables, |d| &d.id, slots.fish_tables, "fish table");
    merger.merge_array(&c.weather_types, |d| &d.id, slots.weather_types, "weather type");
    merger.merge_array(&c.npcs, |d| &d.id, slots.npcs, "NPC");
    merger.merge_array(&c.dialogues, |d| &d.id, slots.dialogues, "dialogue");
    merger.merge_array(&c.scenes, |d| &d.id, slots.scenes, "scene");
    merger.merge_array(&c.events, |d| &d.id, slots.events, "event");
    merger.merge_array(&c.quests, |d| &d.id, slots.quests, "quest");
    merger.merge_array(&c.shops, |d| &d.id, slots.shops, "shop");
    merger.merge_array(&c.actions, |d| &d.id, slots.actions, "action");
    merger.merge_array(&c.minigames, |d| &d.id, slots.minigames, "minigame");
}

/// Layer enabled packs (in resolved order) on top of a GameContent. Used at play time so mods
/// apply without touching the authored project fields.
pub fn merge_packs_into_content(base: &GameContent, installs: &[PackInstallation]) -> MergePacksIntoContentResult {
    let ResolvePackOrderResult { packs, mut problems } = resolve_pack_order(installs);
    if packs.is_empty() {
        return MergePacksIntoContentResult { content: base.clone(), problems };
    }

    let mut content = base.clone();
    for raw_pack in &packs {
        let pack = namespace_pack(raw_pack);
        merge_pack(
            &pack,
            &mut content.crops,
            MergeSlots {
                items: &mut content.items,
                recipes: &mut content.recipes,
                machine_types: &mut content.machine_types,
                node_types: &mut content.node_types,
                animal_species: &mut content.animal_species,
                fish_tables: &mut content.fish_tables,
                weather_types: &mut content.weather.types,
                npcs: &mut content.npcs,
                dialogues: &mut content.dialogues,
                scenes: &mut content.scenes,
                events: &mut content.events,
                quests: &mut content.quests,
                shops: &mut content.shops,
                actions: &mut content.actions,
                minigames: &mut content.minigames,
            },
            &mut problems,
        );
    }
    MergePacksIntoContentResult { content, problems }
}

/// Materialize a pack's content into a project (the editor's "import into project" and the
/// starter-game seed). Same namespacing and override rules as play-time merging; returns
/// problems for anything skipped.
pub fn apply_pack_to_project(project: &GameProject, raw_pack: &ContentPack) -> ApplyPackToProjectResult {
    let mut problems = Vec::new();
    let pack = namespace_pack(raw_pack);

    let mut next = project.clone();
    let mut custom_crops = project.custom_crops.clone().unwrap_or_default();
    merge_pack(
        &pack,
        &mut CustomCropTarget { crops: &mut custom_crops },
        MergeSlots {
            items: &mut next.items,
            recipes: &mut next.recipes,
            machine_types: &mut next.machine_types,
            node_types: &mut next.node_types,
            animal_species: &mut next.animal_species,
            fish_tables: &mut next.fish_tables,
            weather_types: &mut next.weather.types,
            npcs: &mut next.npcs,
            dialogues: &mut next.dialogues,
            scenes: &mut next.scenes,
            events: &mut next.events,
            quests: &mut next.quests,
            shops: &mut next.shops,
            actions: &mut next.actions,
            minigames: &mut next.minigames,
        },
        &mut problems,
    );
    next.custom_crops = Some(custom_crops);

    if let Some(start) = &pack.content.player_start {
        // `new Map(...)`: a later duplicate item id wins.
        let mut item_by_id: IndexMap<&str, &Item> = IndexMap::new();
        for item in &next.items {
            item_by_id.insert(item.id.as_str(), item);
        }
        let mut inventory = next.player.inventory.clone();
        for slot in &start.inventory {
            match item_by_id.get(slot.item_id.as_str()) {
                Some(item) => inventory.push(InventorySlot { item: (*item).clone(), quantity: slot.quantity }),
                None => problems.push(PackProblem::error(
                    &pack.manifest.id,
                    format!("playerStart references unknown item '{}'", slot.item_id),
                )),
            }
        }
        next.player.inventory = inventory;
        if let Some(money) = start.money {
            next.player.money = money;
        }
        if let Some(scene_id) = &start.scene_id {
            next.player.scene_id = scene_id.clone();
        }
        if let Some(x) = start.x {
            next.player.x = x;
        }
        if let Some(y) = start.y {
            next.player.y = y;
        }
        if let Some(scene_id) = start.scene_id.as_deref().filter(|scene_id| !scene_id.is_empty()) {
            next.start_scene_id = scene_id.to_owned();
        }
    }

    ApplyPackToProjectResult { project: next, problems }
}

/// All pack problems for a project (Problems panel): order + dry-run merge.
pub fn collect_pack_problems(project: &GameProject, content: &GameContent) -> Vec<PackProblem> {
    merge_packs_into_content(content, &project.content_packs).problems
}

/// The pack stamp written into saves.
pub fn stamp_packs(installs: &[PackInstallation]) -> Vec<SavePackRef> {
    installs
        .iter()
        .filter(|install| install.enabled)
        .map(|install| SavePackRef {
            id: install.pack.manifest.id.clone(),
            version: install.pack.manifest.version.clone(),
        })
        .collect()
}

/// Quarantine inventory items whose owning pack is missing or disabled, and restore quarantined
/// items whose pack came back. Items are never dropped.
pub fn reconcile_pack_items(state: GameState, enabled_pack_ids: &IndexSet<String>) -> GameState {
    let is_available = |item_id: &str| match pack_id_of(item_id) {
        None => true,
        Some(owner) => enabled_pack_ids.contains(owner),
    };

    let keep: Vec<InventorySlot> =
        state.player.inventory.iter().filter(|slot| is_available(&slot.item.id)).cloned().collect();
    let to_quarantine: Vec<InventorySlot> =
        state.player.inventory.iter().filter(|slot| !is_available(&slot.item.id)).cloned().collect();
    let to_restore: Vec<InventorySlot> =
        state.quarantined_items.iter().filter(|slot| is_available(&slot.item.id)).cloned().collect();
    let still_quarantined: Vec<InventorySlot> =
        state.quarantined_items.iter().filter(|slot| !is_available(&slot.item.id)).cloned().collect();

    if to_quarantine.is_empty() && to_restore.is_empty() {
        return state;
    }

    let mut state = state;
    state.player.inventory = keep.into_iter().chain(to_restore).collect();
    state.quarantined_items = still_quarantined.into_iter().chain(to_quarantine).collect();
    state
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::schema::{PackDependency, PackManifest, PlayerState};

    fn pack(id: &str, dependencies: &[&str]) -> PackInstallation {
        PackInstallation {
            pack: ContentPack {
                manifest: PackManifest {
                    id: id.to_owned(),
                    name: id.to_uppercase(),
                    version: "1.0.0".to_owned(),
                    dependencies: dependencies
                        .iter()
                        .map(|dependency| PackDependency { pack_id: (*dependency).to_owned(), version: None })
                        .collect(),
                    ..PackManifest::default()
                },
                ..ContentPack::default()
            },
            enabled: true,
        }
    }

    #[test]
    fn namespaces_local_ids_and_leaves_absolute_ids_alone() {
        assert_eq!(namespaced_id("mod", "wheat"), "mod:wheat");
        assert_eq!(namespaced_id("mod", "other:wheat"), "other:wheat");
        assert_eq!(pack_id_of("mod:wheat"), Some("mod"));
        assert_eq!(pack_id_of("wheat"), None);
        assert_eq!(pack_id_of(":wheat"), None);
    }

    #[test]
    fn resolves_dependencies_before_dependents_and_reports_cycles() {
        let installs = vec![pack("a", &["b"]), pack("b", &[]), pack("c", &["c"]), pack("d", &["missing"])];
        let result = resolve_pack_order(&installs);
        let order: Vec<&str> = result.packs.iter().map(|pack| pack.manifest.id.as_str()).collect();
        assert_eq!(order, ["b", "a", "c", "d"]);
        assert_eq!(
            result.problems,
            [
                PackProblem::error("c", "Dependency cycle involving pack 'c'".to_owned()),
                PackProblem::error("d", "Pack 'd' depends on 'missing' which is not installed/enabled".to_owned()),
            ]
        );
    }

    #[test]
    fn reconcile_returns_the_same_state_when_nothing_moves() {
        let state = GameState {
            player: PlayerState {
                inventory: vec![InventorySlot {
                    item: Item { id: "wheat".to_owned(), ..Item::default() },
                    quantity: 1.0,
                }],
                ..PlayerState::default()
            },
            ..GameState::default()
        };
        let reconciled = reconcile_pack_items(state.clone(), &IndexSet::new());
        assert_eq!(reconciled, state);
    }

    #[test]
    fn reconcile_quarantines_and_restores_pack_items() {
        let slot = |id: &str| InventorySlot { item: Item { id: id.to_owned(), ..Item::default() }, quantity: 1.0 };
        let state = GameState {
            player: PlayerState { inventory: vec![slot("wheat"), slot("gone:thing")], ..PlayerState::default() },
            quarantined_items: vec![slot("back:thing")],
            ..GameState::default()
        };
        let enabled: IndexSet<String> = ["back".to_owned()].into_iter().collect();
        let reconciled = reconcile_pack_items(state, &enabled);
        let inventory: Vec<&str> = reconciled.player.inventory.iter().map(|slot| slot.item.id.as_str()).collect();
        let quarantined: Vec<&str> = reconciled.quarantined_items.iter().map(|slot| slot.item.id.as_str()).collect();
        assert_eq!(inventory, ["wheat", "back:thing"]);
        assert_eq!(quarantined, ["gone:thing"]);
    }
}
