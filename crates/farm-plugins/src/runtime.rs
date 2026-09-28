//! Wiring a plugin host to an engine session (port of the C# `PluginBridge`).

use crate::host::{PluginHost, PluginHostOptions, WasmPluginHost};
use crate::payload::{effect_events, hook_payload_json};
use crate::queue::PluginMutationQueue;
use crate::spec::{plugin_specs_from_project, PluginSpec};
use crate::PluginError;
use farm_sim::effects::Effect;
use farm_sim::schema::GameProject;
use farm_sim::{Command, HookEvent};
use std::collections::VecDeque;

/// How many recent errors [`PluginRuntime::recent_errors`] keeps.
pub const MAX_RECENT_ERRORS: usize = 100;

/// Connects a plugin host to the hook events an engine step produced, the way the web app,
/// the exported game shell and the C# `PluginBridge` do.
///
/// Per frame, the host:
///
/// 1. calls [`PluginRuntime::drain_commands`] and applies each command through
///    [`farm_sim::apply_command`] (and the command log), before the frame's input commands and
///    ticks;
/// 2. runs the frame's commands and ticks;
/// 3. after each step, passes the step's hook events (`EngineContext::drain_hook_events`) and
///    effects to [`PluginRuntime::handle_step`].
///
/// Plugins answer only through the queue, so their mutations enter the command log at one
/// well-defined point and replays of that log need no plugins. Only hooks some plugin is
/// granted are dispatched.
///
/// `onWeatherRoll` is dispatched like any other hook, after the step: plugins never answer it
/// synchronously (the web and C# bridges return nothing from their listeners), so no
/// `WeatherRollListener` is installed.
#[derive(Debug)]
pub struct PluginRuntime<H: PluginHost = WasmPluginHost> {
    host: H,
    hook_names: Vec<String>,
    queue: PluginMutationQueue,
    recent_errors: VecDeque<PluginError>,
}

impl PluginRuntime<WasmPluginHost> {
    /// A sandboxed runtime for a project's enabled plugins (C# `PluginBridge.CreateEngineContext`).
    /// `None` when no enabled pack ships plugins.
    pub fn from_project(project: &GameProject, options: PluginHostOptions) -> Option<Self> {
        let specs = plugin_specs_from_project(project);
        if specs.is_empty() {
            return None;
        }
        let host = WasmPluginHost::new(specs.clone(), options);
        Some(Self::new(host, &specs))
    }
}

impl<H: PluginHost> PluginRuntime<H> {
    /// Wrap `host`, which was built from `specs`. The host's init errors become the first
    /// recent errors.
    pub fn new(host: H, specs: &[PluginSpec]) -> Self {
        // TS: [...new Set(specs.flatMap(spec => spec.grantedHooks))], first-appearance order.
        let mut hook_names: Vec<String> = Vec::new();
        for hook in specs.iter().flat_map(|spec| &spec.granted_hooks) {
            if !hook_names.contains(hook) {
                hook_names.push(hook.clone());
            }
        }
        let mut runtime = Self { host, hook_names, queue: PluginMutationQueue::new(), recent_errors: VecDeque::new() };
        let init_errors = runtime.host.init_errors().to_vec();
        runtime.record(&init_errors);
        runtime
    }

    /// The hooks some plugin is granted, in first-appearance order. Nothing else is dispatched.
    pub fn hook_names(&self) -> &[String] {
        &self.hook_names
    }

    /// Whether some plugin is granted `hook`.
    pub fn listens_to(&self, hook: &str) -> bool {
        self.hook_names.iter().any(|name| name == hook)
    }

    /// The underlying host.
    pub fn host(&self) -> &H {
        &self.host
    }

    /// The underlying host, mutably (to dispatch a hook without queueing, for instance).
    pub fn host_mut(&mut self) -> &mut H {
        &mut self.host
    }

    /// Mutations waiting for [`PluginRuntime::drain_commands`].
    pub fn queue(&self) -> &PluginMutationQueue {
        &self.queue
    }

    /// The most recent errors (init failures, throws, overruns, dropped mutations), oldest
    /// first, at most [`MAX_RECENT_ERRORS`].
    pub fn recent_errors(&self) -> impl ExactSizeIterator<Item = &PluginError> {
        self.recent_errors.iter()
    }

    /// Dispatch one hook with its payload JSON and queue the results. Returns the errors this
    /// dispatch reported (the C# `ErrorReported` event). Hooks no plugin is granted are skipped.
    pub fn dispatch(&mut self, hook: &str, payload_json: &str) -> Vec<PluginError> {
        if !self.listens_to(hook) {
            return Vec::new();
        }
        let results = self.host.dispatch(hook, payload_json);
        self.queue.enqueue(&results);
        let errors: Vec<PluginError> = results.into_iter().flat_map(|result| result.errors).collect();
        self.record(&errors);
        errors
    }

    /// Dispatch hook events in order (the output of `EngineContext::drain_hook_events` or
    /// `HookBus::drain`). Payloads are the engine-order JSON the other hosts send.
    pub fn dispatch_events(&mut self, events: &[HookEvent]) -> Vec<PluginError> {
        let mut errors = Vec::new();
        for event in events {
            if self.listens_to(event.hook()) {
                errors.extend(self.dispatch(event.hook(), &hook_payload_json(event)));
            }
        }
        errors
    }

    /// Everything a step tells plugins, in the order the other hosts use: the engine's hook
    /// events, then one `onEffect` per effect.
    pub fn handle_step(&mut self, events: &[HookEvent], effects: &[Effect]) -> Vec<PluginError> {
        let mut errors = self.dispatch_events(events);
        if self.listens_to(farm_sim::hooks::hook_names::ON_EFFECT) {
            errors.extend(self.dispatch_events(&effect_events(effects)));
        }
        errors
    }

    /// Drain queued mutations as `pluginMutation` commands, in arrival order. Call once per
    /// frame, before the frame's input commands and ticks, and run each through
    /// [`farm_sim::apply_command`] (and the command log).
    pub fn drain_commands(&mut self) -> Vec<Command> {
        self.queue.drain().into_iter().map(|queued| queued.into_command()).collect()
    }

    fn record(&mut self, errors: &[PluginError]) {
        for error in errors {
            if self.recent_errors.len() == MAX_RECENT_ERRORS {
                self.recent_errors.pop_front();
            }
            self.recent_errors.push_back(error.clone());
        }
    }
}
