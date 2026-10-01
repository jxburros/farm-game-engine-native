//! Wiring a plugin host to an engine session (port of the C# `PluginBridge`).

use crate::host::{PluginHost, PluginHostOptions, WasmPluginHost};
use crate::payload::{effect_events, hook_payload_json};
use crate::queue::{PluginMutationQueue, QueuedPluginMutation};
use crate::spec::{plugin_specs_from_project, PluginSpec};
use crate::{PluginError, PluginErrorKind};
use farm_sim::effects::Effect;
use farm_sim::hooks::hook_names;
use farm_sim::schema::GameProject;
use farm_sim::{Command, HookEvent};
use std::collections::VecDeque;

/// How many recent errors [`PluginRuntime::recent_errors`] keeps.
pub const MAX_RECENT_ERRORS: usize = 100;

/// Ticks per plugin budget window ([`crate::PluginHostOptions::fuel_per_window`]): one second
/// of game time. Windows follow the game clock, not frames, so budgets do not depend on the
/// frame rate.
pub const BUDGET_WINDOW_TICKS: u64 = farm_sim::engine::TICKS_PER_SECOND as u64;

/// Connects a plugin host to the hook events an engine step produced, the way the web app,
/// the exported game shell and the C# `PluginBridge` do.
///
/// A session:
///
/// 1. before each tick, calls [`PluginRuntime::begin_tick`] and [`PluginRuntime::drain`], and
///    applies each drained command through [`farm_sim::apply_command`] (and the command log);
/// 2. after each step (a tick, an input command, a drained command), passes the step's hook
///    events (`EngineContext::drain_hook_events`) and effects to
///    [`PluginRuntime::handle_step_at`], with the step's depth: 0 for ticks and input, the
///    drained mutation's depth + 1 for a drained command.
///
/// So a mutation answering tick `k` applies right before tick `k + 1`, whatever the frame
/// size, and the same input reaches the same state at 30 fps and at 144 fps. Plugins answer
/// only through the queue, so their mutations enter the command log at well-defined points
/// and replays of that log need no plugins. Only hooks some plugin is granted are dispatched.
///
/// Feedback loops are bounded: steps caused by plugin mutations do not dispatch `onCommand` or
/// `onEffect`, and mutations [`PluginRuntime::MAX_MUTATION_DEPTH`] levels deep are dropped.
///
/// `onWeatherRoll` is dispatched like any other hook, after the step: plugins never answer it
/// synchronously (the web and C# bridges return nothing from their listeners), so no
/// `WeatherRollListener` is installed. A `setWeather` answer changes the weather after the
/// night's watering and storm damage already used the rolled one.
#[derive(Debug)]
pub struct PluginRuntime<H: PluginHost = WasmPluginHost> {
    host: H,
    hook_names: Vec<String>,
    queue: PluginMutationQueue,
    recent_errors: VecDeque<PluginError>,
    window: Option<u64>,
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
    /// Mutations at this depth are dropped: a game step's answers are depth 0, answers to the
    /// steps those cause are depth 1, and so on, so a chain of plugin reactions ends after
    /// three links.
    pub const MAX_MUTATION_DEPTH: u32 = 3;

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
        let mut runtime =
            Self { host, hook_names, queue: PluginMutationQueue::new(), recent_errors: VecDeque::new(), window: None };
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

    /// Mutations waiting for [`PluginRuntime::drain`].
    pub fn queue(&self) -> &PluginMutationQueue {
        &self.queue
    }

    /// The most recent errors (init failures, throws, overruns, dropped mutations), oldest
    /// first, at most [`MAX_RECENT_ERRORS`].
    pub fn recent_errors(&self) -> impl ExactSizeIterator<Item = &PluginError> {
        self.recent_errors.iter()
    }

    /// Tell the runtime the game is about to run tick `tick` (the clock's tick before it
    /// advances). Starts a new budget window every [`BUDGET_WINDOW_TICKS`] ticks.
    pub fn begin_tick(&mut self, tick: u64) {
        let window = tick / BUDGET_WINDOW_TICKS;
        if self.window != Some(window) {
            self.window = Some(window);
            self.host.begin_window();
        }
    }

    /// Dispatch one hook with its payload JSON and queue the results as answers to a game step
    /// (depth 0). Returns the errors this dispatch reported (the C# `ErrorReported` event).
    /// Hooks no plugin is granted are skipped.
    pub fn dispatch(&mut self, hook: &str, payload_json: &str) -> Vec<PluginError> {
        self.dispatch_at(hook, payload_json, 0)
    }

    /// [`PluginRuntime::dispatch`] for a step at `depth`: a step caused by a plugin mutation
    /// (`depth > 0`) does not dispatch `onCommand` or `onEffect`, and its answers are queued
    /// at `depth`, or dropped at [`PluginRuntime::MAX_MUTATION_DEPTH`].
    pub fn dispatch_at(&mut self, hook: &str, payload_json: &str, depth: u32) -> Vec<PluginError> {
        if !self.listens_to(hook) || (depth > 0 && (hook == hook_names::ON_COMMAND || hook == hook_names::ON_EFFECT)) {
            return Vec::new();
        }
        let mut results = self.host.dispatch(hook, payload_json);
        if depth >= Self::MAX_MUTATION_DEPTH {
            for result in &mut results {
                if !result.mutations.is_empty() {
                    result.mutations.clear();
                    result.errors.push(PluginError {
                        plugin_id: result.plugin_id.clone(),
                        kind: PluginErrorKind::InvalidMutation,
                        message: format!(
                            "mutations dropped: plugin mutations may set off at most {} more steps",
                            Self::MAX_MUTATION_DEPTH
                        ),
                        hook: Some(hook.to_owned()),
                    });
                }
            }
        }
        self.queue.enqueue_at(&results, depth);
        let errors: Vec<PluginError> = results.into_iter().flat_map(|result| result.errors).collect();
        self.record(&errors);
        errors
    }

    /// Dispatch hook events in order (the output of `EngineContext::drain_hook_events` or
    /// `HookBus::drain`). Payloads are the engine-order JSON the other hosts send.
    pub fn dispatch_events(&mut self, events: &[HookEvent]) -> Vec<PluginError> {
        self.dispatch_events_at(events, 0)
    }

    /// [`PluginRuntime::dispatch_events`] for a step at `depth`.
    pub fn dispatch_events_at(&mut self, events: &[HookEvent], depth: u32) -> Vec<PluginError> {
        let mut errors = Vec::new();
        for event in events {
            if self.listens_to(event.hook()) {
                errors.extend(self.dispatch_at(event.hook(), &hook_payload_json(event), depth));
            }
        }
        errors
    }

    /// Everything a game step tells plugins, in the order the other hosts use: the engine's
    /// hook events, then one `onEffect` per effect.
    pub fn handle_step(&mut self, events: &[HookEvent], effects: &[Effect]) -> Vec<PluginError> {
        self.handle_step_at(events, effects, 0)
    }

    /// [`PluginRuntime::handle_step`] for a step at `depth` (see [`PluginRuntime::dispatch_at`]).
    pub fn handle_step_at(&mut self, events: &[HookEvent], effects: &[Effect], depth: u32) -> Vec<PluginError> {
        let mut errors = self.dispatch_events_at(events, depth);
        if depth == 0 && self.listens_to(hook_names::ON_EFFECT) {
            errors.extend(self.dispatch_events_at(&effect_events(effects), depth));
        }
        errors
    }

    /// Drain queued mutations, with their depth, in arrival order. Run each as a
    /// `pluginMutation` command ([`QueuedPluginMutation::into_command`]) and hand its step to
    /// [`PluginRuntime::handle_step_at`] at the mutation's depth + 1.
    pub fn drain(&mut self) -> Vec<QueuedPluginMutation> {
        self.queue.drain()
    }

    /// Drain queued mutations as `pluginMutation` commands, in arrival order, without their
    /// depth (every step they cause is then a game step for [`PluginRuntime::handle_step`]).
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
