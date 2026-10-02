//! Helpers shared by the farm-plugins integration tests.
#![allow(dead_code)]

use farm_plugins::{plugin_specs_from_packs, PluginDispatchResult, PluginHostOptions, WasmPluginHost};
use farm_sim::hooks::hook_names;
use farm_sim::schema::{ContentPack, GameProject, PackManifest, PackPermissions, PackPlugin, PluginMutation};
use serde_json::Value;
use std::path::PathBuf;

/// The `onDayStart` payload of day 3 (C# `Day3`).
pub const DAY3: &str = r#"{"day":3,"season":"spring","year":1}"#;

/// A pack whose manifest grants `granted` hooks and every mutation (`"*"`), so sandbox tests
/// see answers unfiltered; the capability tests build their own manifests.
pub fn pack(id: &str, granted: &[&str], plugins: Vec<PackPlugin>) -> ContentPack {
    ContentPack {
        manifest: PackManifest {
            id: id.to_owned(),
            name: id.to_owned(),
            version: "1.0.0".to_owned(),
            permissions: PackPermissions {
                hooks: granted.iter().map(|h| (*h).to_owned()).collect(),
                mutations: Some(vec!["*".to_owned()]),
                ..Default::default()
            },
            ..Default::default()
        },
        plugins,
        ..Default::default()
    }
}

/// A plugin asking for `hooks` (`onDayStart` when empty).
pub fn plugin(id: &str, source: &str, hooks: &[&str]) -> PackPlugin {
    let hooks =
        if hooks.is_empty() { vec!["onDayStart".to_owned()] } else { hooks.iter().map(|h| (*h).to_owned()).collect() };
    PackPlugin { id: id.to_owned(), hooks, source: source.to_owned(), ..Default::default() }
}

/// A host for `plugins` in pack `pack-a`, which is granted every hook.
pub fn host_with(options: PluginHostOptions, plugins: Vec<PackPlugin>) -> WasmPluginHost {
    WasmPluginHost::new(plugin_specs_from_packs([&pack("pack-a", hook_names::ALL, plugins)]), options)
}

pub fn host(plugins: Vec<PackPlugin>) -> WasmPluginHost {
    host_with(PluginHostOptions::default(), plugins)
}

/// Every `message` mutation's text, joined with `|` (C# `Texts`).
pub fn texts(results: &[PluginDispatchResult]) -> String {
    results
        .iter()
        .flat_map(|result| &result.mutations)
        .filter_map(|mutation| match mutation {
            PluginMutation::Message { text } => Some(text.as_str()),
            _ => None,
        })
        .collect::<Vec<_>>()
        .join("|")
}

pub fn repo_path(relative: &str) -> PathBuf {
    [env!("CARGO_MANIFEST_DIR"), "..", ".."].iter().collect::<PathBuf>().join(relative)
}

pub fn read_json(relative: &str) -> Value {
    let path = repo_path(relative);
    let text = std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("read {}: {e}", path.display()));
    serde_json::from_str(&text).unwrap_or_else(|e| panic!("parse {}: {e}", path.display()))
}

/// The golden starter farm's project (npc-farmer at (3, 6), player at (8, 9), 100 money,
/// spring day 1): the Rust tests' stand-in for C# `EngineTests.MakeProject()`.
pub fn starter_farm_project() -> GameProject {
    serde_json::from_value(read_json("fixtures/golden/content/starter-farm.json")["project"].clone())
        .expect("starter-farm project is a GameProject")
}

/// The web version's Glow Farm demo mod (C# test fixture `demo-mod.json`).
pub fn demo_mod() -> ContentPack {
    serde_json::from_value(read_json("fixtures/projects/demo-mod.json")).expect("demo mod is a pack")
}
