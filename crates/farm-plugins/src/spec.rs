//! Plugin specs: what a host needs to load a plugin (port of `PluginSpec`,
//! `Plugins.PluginSpecsFromPacks` and `Plugins.PluginSpecsFromProject`).

use farm_sim::schema::{ContentPack, GameProject};
use serde::{Deserialize, Serialize};

/// A plugin ready to load (TS `PluginSpec`).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PluginSpec {
    /// `packId:pluginId`, globally unique.
    pub id: String,
    /// The pack that ships the plugin.
    pub pack_id: String,
    /// The body of `function (api) { … }`.
    pub source: String,
    /// Hooks the plugin may receive: the plugin's `hooks` that the pack manifest's
    /// `permissions.hooks` also lists (the ones the player approved at install), in the
    /// plugin's order.
    pub granted_hooks: Vec<String>,
    /// The pack manifest's `permissions.mutations`: what the plugin's answers may do (see
    /// [`MutationGrants`](crate::MutationGrants)). `None` when the manifest declares none: the
    /// defaults apply and answers to `onEffect` / `onCommand` are dropped.
    #[serde(default)]
    pub granted_mutations: Option<Vec<String>>,
}

impl PluginSpec {
    /// Whether the plugin may receive `hook`.
    pub fn is_granted(&self, hook: &str) -> bool {
        self.granted_hooks.iter().any(|granted| granted == hook)
    }
}

/// Plugin specs, with capability grants applied, from packs in load order.
pub fn plugin_specs_from_packs<'a>(packs: impl IntoIterator<Item = &'a ContentPack>) -> Vec<PluginSpec> {
    let mut specs = Vec::new();
    for pack in packs {
        let permitted = &pack.manifest.permissions.hooks;
        for plugin in &pack.plugins {
            specs.push(PluginSpec {
                id: format!("{}:{}", pack.manifest.id, plugin.id),
                pack_id: pack.manifest.id.clone(),
                source: plugin.source.clone(),
                granted_hooks: plugin.hooks.iter().filter(|hook| permitted.contains(hook)).cloned().collect(),
                granted_mutations: pack.manifest.permissions.mutations.clone(),
            });
        }
    }
    specs
}

/// Specs from a project's enabled installed packs, in load order.
pub fn plugin_specs_from_project(project: &GameProject) -> Vec<PluginSpec> {
    plugin_specs_from_packs(project.content_packs.iter().filter(|install| install.enabled).map(|install| &install.pack))
}
