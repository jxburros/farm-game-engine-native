//! proptest strategies for random play: replay logs of every [`Command`] variant, with arguments
//! drawn from a project's content (its items, shops, recipes, machines, NPCs, …) plus a few
//! unknown ids, interleaved with ticks.
//!
//! Shared by the property tests of farm-sim (`properties.rs`) and farm-cart (`properties.rs`,
//! through `#[path]`), so it depends on nothing but proptest, farm-sim and serde_json.
#![allow(dead_code)]

use farm_sim::replay::ReplayInput;
use farm_sim::schema::{tool_types, GameProject, PluginMutation, SKILL_NAMES};
use farm_sim::Command;
use proptest::prelude::*;
use proptest::sample::select;
use serde_json::Value;
use std::path::PathBuf;

/// A project of `fixtures/golden/content`.
pub fn golden_project(name: &str) -> GameProject {
    let path: PathBuf =
        [env!("CARGO_MANIFEST_DIR"), "..", "..", "fixtures", "golden", "content", &format!("{name}.json")]
            .iter()
            .collect();
    let text = std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("read {}: {e}", path.display()));
    let fixture: Value = serde_json::from_str(&text).unwrap_or_else(|e| panic!("parse {}: {e}", path.display()));
    serde_json::from_value(fixture["project"].clone()).unwrap_or_else(|e| panic!("project {}: {e}", path.display()))
}

/// The ids a project's commands can name, each list with one id no content has.
#[derive(Debug, Clone)]
pub struct Ids {
    pub items: Vec<String>,
    pub tools: Vec<String>,
    pub shops: Vec<String>,
    pub recipes: Vec<String>,
    pub machines: Vec<String>,
    pub npcs: Vec<String>,
    pub actions: Vec<String>,
    pub minigames: Vec<String>,
    pub weather: Vec<String>,
    pub quests: Vec<String>,
    pub scenes: Vec<String>,
}

fn with_unknown(ids: impl Iterator<Item = String>) -> Vec<String> {
    let mut ids: Vec<String> = ids.collect();
    ids.push("no-such-id".to_owned());
    ids
}

impl Ids {
    pub fn of(project: &GameProject) -> Self {
        Self {
            items: with_unknown(project.items.iter().map(|i| i.id.clone())),
            tools: with_unknown(project.items.iter().filter(|i| i.id.starts_with("tool-")).map(|i| i.id.clone())),
            shops: with_unknown(project.shops.iter().map(|s| s.id.clone())),
            recipes: with_unknown(project.recipes.iter().map(|r| r.id.clone())),
            machines: with_unknown(project.machine_types.iter().map(|m| m.id.clone())),
            npcs: with_unknown(project.npcs.iter().map(|n| n.id.clone())),
            actions: with_unknown(project.actions.iter().map(|a| a.id.clone())),
            minigames: with_unknown(project.minigames.iter().map(|m| m.id.clone())),
            weather: with_unknown(project.weather.types.iter().map(|w| w.id.clone())),
            quests: with_unknown(project.quests.iter().map(|q| q.id.clone())),
            scenes: with_unknown(project.scenes.iter().map(|s| s.id.clone())),
        }
    }
}

fn int(range: std::ops::RangeInclusive<i32>) -> impl Strategy<Value = i32> {
    range
}

fn count(range: std::ops::RangeInclusive<u32>) -> impl Strategy<Value = u32> {
    range
}

fn money(range: std::ops::RangeInclusive<i64>) -> impl Strategy<Value = i64> {
    range
}

fn direction() -> impl Strategy<Value = String> {
    select(vec!["up", "down", "left", "right"]).prop_map(str::to_owned)
}

fn plugin_mutation(ids: &Ids) -> impl Strategy<Value = PluginMutation> {
    prop_oneof![
        (select(ids.items.clone()), count(1..=20))
            .prop_map(|(item_id, quantity)| PluginMutation::GiveItem { item_id, quantity }),
        (select(ids.items.clone()), count(1..=5))
            .prop_map(|(item_id, quantity)| PluginMutation::TakeItem { item_id, quantity }),
        money(1..=5_000).prop_map(|amount| PluginMutation::GiveMoney { amount }),
        money(1..=500).prop_map(|amount| PluginMutation::TakeMoney { amount }),
        (
            select(vec!["flag-a", "flag-b"]),
            prop_oneof![
                any::<bool>().prop_map(Value::from),
                (0..100i32).prop_map(Value::from),
                // Doubles, whole ones too: a save writes `2.0` as `2` (#142).
                (0..100i32).prop_map(|n| Value::from(f64::from(n))),
                (-1000.0..1000.0f64).prop_map(Value::from),
                Just(Value::from("text")),
            ]
        )
            .prop_map(|(flag, value)| PluginMutation::SetFlag { flag: flag.to_owned(), value }),
        Just(PluginMutation::Message { text: "hello".to_owned() }),
        select(ids.weather.clone()).prop_map(|weather_id| PluginMutation::SetWeather { weather_id }),
        (select(ids.npcs.clone()), int(-300..=300))
            .prop_map(|(npc_id, delta)| PluginMutation::ModifyFriendship { npc_id, delta }),
        (select(SKILL_NAMES.to_vec()), count(1..=500))
            .prop_map(|(skill, amount)| PluginMutation::GrantXp { skill: skill.to_owned(), amount }),
        int(-60..=60).prop_map(|delta| PluginMutation::ModifyEnergy { delta: farm_sim::units::points(delta) }),
        select(ids.quests.clone()).prop_map(|quest_id| PluginMutation::StartQuest { quest_id }),
        (select(ids.scenes.clone()), int(0..=15), int(0..=11))
            .prop_map(|(scene_id, x, y)| PluginMutation::WarpPlayer { scene_id, x, y }),
        select(ids.npcs.clone()).prop_map(|npc_id| PluginMutation::StartDialogue { npc_id, dialogue_id: None }),
        select(ids.actions.clone()).prop_map(|action_id| PluginMutation::PerformAction { action_id }),
    ]
}

/// Any command, with plausible arguments for `ids`' project. Movement, tools and interaction
/// are the most frequent, as in play.
pub fn command(ids: &Ids) -> impl Strategy<Value = Command> {
    prop_oneof![
        6 => (int(-1..=1), int(-1..=1)).prop_map(|(dx, dy)| Command::SetMoveIntent { dx, dy }),
        6 => direction().prop_map(|dir| Command::Move { dir }),
        6 => select(tool_types::ALL.to_vec()).prop_map(|tool| Command::UseTool { tool: tool.to_owned() }),
        4 => Just(Command::Interact),
        2 => int(0..=3).prop_map(|index| Command::ChooseDialogueOption { index }),
        1 => Just(Command::CloseDialogue),
        1 => Just(Command::Sleep),
        1 => select(ids.shops.clone()).prop_map(|shop_id| Command::OpenShop { shop_id }),
        1 => Just(Command::CloseShop),
        2 => (select(ids.items.clone()), count(1..=5))
            .prop_map(|(item_id, quantity)| Command::BuyItem { item_id, quantity }),
        2 => (select(ids.items.clone()), count(1..=5))
            .prop_map(|(item_id, quantity)| Command::SellItem { item_id, quantity }),
        1 => select(ids.tools.clone()).prop_map(|item_id| Command::RepairTool { item_id }),
        2 => select(ids.recipes.clone()).prop_map(|recipe_id| Command::Craft { recipe_id }),
        1 => select(ids.machines.clone()).prop_map(|machine_type_id| Command::PlaceMachine { machine_type_id }),
        1 => select(ids.recipes.clone()).prop_map(|recipe_id| Command::MachineLoad { recipe_id }),
        1 => select(ids.items.clone()).prop_map(|item_id| Command::GiveGift { item_id }),
        1 => count(1..=4).prop_map(|floor| Command::DescendMine { floor }),
        1 => Just(Command::ExitMine),
        1 => select(ids.actions.clone()).prop_map(|action_id| Command::PerformAction { action_id }),
        1 => select(ids.items.clone()).prop_map(|item_id| Command::UseItem { item_id }),
        1 => select(ids.minigames.clone()).prop_map(|minigame_id| Command::StartMinigame { minigame_id }),
        1 => (0..=farm_sim::units::PROBABILITY_ONE).prop_map(|score| Command::ResolveMinigame { score }),
        1 => Just(Command::CancelMinigame),
        3 => (select(vec!["plugin-a", "plugin-b"]), plugin_mutation(ids)).prop_map(|(plugin_id, mutation)| {
            Command::PluginMutation { plugin_id: plugin_id.to_owned(), mutation }
        }),
    ]
}

/// One replay input: a command, or up to `max_ticks` ticks (a few seconds of play).
pub fn input(ids: &Ids, max_ticks: u32) -> impl Strategy<Value = ReplayInput> {
    prop_oneof![
        4 => command(ids).prop_map(|command| ReplayInput::Command { command }),
        1 => (1..=max_ticks).prop_map(|ticks| ReplayInput::Tick { ticks: u64::from(ticks) }),
    ]
}

/// A replay log of up to `max_len` inputs.
pub fn inputs(ids: &Ids, max_len: usize, max_ticks: u32) -> impl Strategy<Value = Vec<ReplayInput>> {
    prop::collection::vec(input(ids, max_ticks), 0..=max_len)
}

/// A seed for a new game.
pub fn seed() -> impl Strategy<Value = String> {
    any::<u32>().prop_map(|n| format!("seed-{n:08x}"))
}
