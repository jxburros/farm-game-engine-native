//! `farm-plugins`: the plugin sandbox (port of the retired C# `Plugins.cs`, itself a port
//! of the web `packages/engine-runtime/src/plugins.ts`).
//!
//! A plugin is JavaScript from a content pack. Its source is the body of `function (api)`, and
//! it registers hook handlers with `api.on(hook, fn)`. A handler receives the hook's payload
//! (plain JSON) and returns an array of *mutations* such as
//! `{ type: 'giveMoney', amount: 10 }`. Plugins never touch game state: mutations are
//! validated ([`validate_mutations`]) and replayed through the engine as
//! [`Command::PluginMutation`](farm_sim::Command::PluginMutation), so the command log stays the
//! replay artifact and replays need no plugins.
//!
//! # Pieces
//!
//! - [`PluginSpec`], [`plugin_specs_from_packs`], [`plugin_specs_from_project`]: what to load
//!   (grants are the plugin's `hooks` that the pack's `permissions.hooks` also lists).
//! - [`WasmPluginHost`] implements [`PluginHost`]: QuickJS compiled to WebAssembly (the
//!   checked-in guest, see `guest/README.md`), one instance per plugin, run by the wasmi
//!   interpreter with deterministic fuel budgets ([`PluginHostOptions`]).
//! - [`validate_mutations`] / [`parse_mutation`]: the zod mutation schema, with the C# host's
//!   error texts.
//! - [`PluginMutationQueue`] and [`PluginRuntime`]: feed a step's hook events to the host,
//!   queue the results and hand them back as commands at one fixed point per frame.
//!
//! # Example
//!
//! ```
//! use farm_plugins::{PluginHost, PluginHostOptions, PluginSpec, WasmPluginHost};
//! use farm_sim::schema::PluginMutation;
//!
//! let spec = PluginSpec {
//!     id: "demo:tips".to_owned(),
//!     pack_id: "demo".to_owned(),
//!     source: "api.on('onDayStart', p => [{ type: 'giveMoney', amount: p.day * 10 }])".to_owned(),
//!     granted_hooks: vec!["onDayStart".to_owned()],
//! };
//! let mut host = WasmPluginHost::new(vec![spec], PluginHostOptions::default());
//! let results = host.dispatch("onDayStart", r#"{"day":2,"season":"spring","year":1}"#);
//! assert_eq!(results[0].mutations, [PluginMutation::GiveMoney { amount: 20 }]);
//! ```
//!
//! # Determinism
//!
//! Budgets are fuel, not time; the guest's clock is constant and its randomness is seeded the
//! same way on every run; payloads are serialized in engine order. The same specs and the same
//! dispatches therefore give the same results on every machine.
//!
//! This crate has no `unsafe` code. The guest (`crates/farm-plugin-guest`) needs `unsafe` for
//! the QuickJS C API, and runs inside wasm.
#![forbid(unsafe_code)]
#![deny(clippy::disallowed_types, clippy::disallowed_methods)]
#![warn(missing_docs)]

mod host;
mod mutations;
mod payload;
mod queue;
mod runtime;
mod sandbox;
mod spec;

pub use host::{PluginHost, PluginHostOptions, WasmPluginHost};
pub use mutations::{parse_mutation, validate_mutations};
pub use payload::{effect_events, hook_payload_json, to_engine_json};
pub use queue::{PluginMutationQueue, QueuedPluginMutation};
pub use runtime::{PluginRuntime, MAX_RECENT_ERRORS};
pub use sandbox::GUEST_WASM;
pub use spec::{plugin_specs_from_packs, plugin_specs_from_project, PluginSpec};

use farm_sim::schema::PluginMutation;
use serde::{Deserialize, Serialize};
use std::fmt;

/// What went wrong in a [`PluginError`]. Serializes as the C# `PluginErrorKinds` strings.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum PluginErrorKind {
    /// `"init"`: the plugin failed to compile or initialize, and is disabled for the session.
    Init,
    /// `"threw"`: a handler threw, or returned something that is not JSON-serializable.
    Threw,
    /// `"timeout"`: a handler ran out of fuel, the deterministic stand-in for the web host's
    /// 50 ms budget (counts as a strike).
    Timeout,
    /// `"budget"`: a handler exceeded its memory or recursion budget (counts as a strike).
    Budget,
    /// `"invalidMutation"`: a returned mutation failed validation and was dropped.
    InvalidMutation,
    /// `"disabled"`: the plugin hit its strike limit and is disabled for the session.
    Disabled,
}

impl PluginErrorKind {
    /// The kind string shared with the C# and web hosts.
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Init => "init",
            Self::Threw => "threw",
            Self::Timeout => "timeout",
            Self::Budget => "budget",
            Self::InvalidMutation => "invalidMutation",
            Self::Disabled => "disabled",
        }
    }
}

impl fmt::Display for PluginErrorKind {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(self.as_str())
    }
}

/// A per-plugin failure. Errors are isolated: they never reach the simulation or other
/// plugins.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PluginError {
    /// `packId:pluginId`.
    pub plugin_id: String,
    /// What went wrong.
    pub kind: PluginErrorKind,
    /// Human-readable detail, e.g. `threw in onDayStart: kaput`.
    pub message: String,
    /// The hook being dispatched (`None` for init errors).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub hook: Option<String>,
}

/// One plugin's answer to a dispatch (TS `PluginDispatchResult`, plus the errors). Hosts
/// return a result for every plugin that produced mutations or errors, in spec order.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct PluginDispatchResult {
    /// `packId:pluginId`.
    pub plugin_id: String,
    /// Validated mutations, in the order the handler returned them.
    pub mutations: Vec<PluginMutation>,
    /// Isolated failures of this plugin during the dispatch (dropped mutations, a throw, an
    /// overrun, being disabled).
    pub errors: Vec<PluginError>,
}
