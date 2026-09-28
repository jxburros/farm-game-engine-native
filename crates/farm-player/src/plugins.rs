//! The plugin sandbox (`farm-plugins`) attached to a [`PlaySession`](crate::session::PlaySession).

use crate::session::SessionPlugins;
use farm_cart::CartPlugin;
use farm_plugins::{PluginHostOptions, PluginRuntime, PluginSpec, WasmPluginHost};
use farm_sim::schema::GameProject;
use farm_sim::{Command, HookEvent};

impl SessionPlugins for PluginRuntime<WasmPluginHost> {
    fn dispatch(&mut self, events: &[HookEvent]) {
        // Errors are kept in the runtime's bounded recent-error list.
        self.dispatch_events(events);
    }

    fn drain_commands(&mut self) -> Vec<Command> {
        PluginRuntime::drain_commands(self)
    }

    fn recent_errors(&self) -> Vec<String> {
        PluginRuntime::recent_errors(self)
            .map(|error| format!("{}: [{}] {}", error.plugin_id, error.kind.as_str(), error.message))
            .collect()
    }
}

/// The sandbox for a cartridge's plugins; `None` when the game ships none.
pub fn for_cartridge(plugins: &[CartPlugin], options: PluginHostOptions) -> Option<Box<dyn SessionPlugins>> {
    if plugins.is_empty() {
        return None;
    }
    let specs: Vec<PluginSpec> = plugins
        .iter()
        .map(|plugin| PluginSpec {
            id: plugin.id.clone(),
            pack_id: plugin.pack_id.clone(),
            source: plugin.source.clone(),
            granted_hooks: plugin.granted_hooks.clone(),
        })
        .collect();
    let host = WasmPluginHost::new(specs.clone(), options);
    Some(Box::new(PluginRuntime::new(host, &specs)))
}

/// The sandbox for an editor project's enabled plugins; `None` when no enabled pack ships one.
pub fn for_project(project: &GameProject, options: PluginHostOptions) -> Option<Box<dyn SessionPlugins>> {
    PluginRuntime::from_project(project, options).map(|runtime| Box::new(runtime) as Box<dyn SessionPlugins>)
}
