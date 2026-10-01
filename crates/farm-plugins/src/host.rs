//! The plugin host: one isolated QuickJS instance per plugin (port of `JintPluginHost`).

use crate::capabilities::{check_answer, KeyBudget, MutationGrants};
use crate::sandbox::{self, status, EngineLimits, GuestFault, GuestInstance, Sandbox, WasmiEngine, MAX_RESULT_BYTES};
use crate::{PluginDispatchResult, PluginError, PluginErrorKind, PluginSpec};
use serde_json::Value;
use std::collections::VecDeque;
use std::fmt;

/// A host that dispatches hooks to sandboxed plugins (TS `PluginHostLike`).
pub trait PluginHost {
    /// Dispatch `hook` to every plugin granted it and return validated mutations and isolated
    /// errors. There is a result for every plugin that produced mutations or errors, in spec
    /// order; plugins with nothing to say are left out. Synchronous: results are ready when it
    /// returns. `payload_json` is the hook payload as JSON text.
    fn dispatch(&mut self, hook: &str, payload_json: &str) -> Vec<PluginDispatchResult>;

    /// Plugins that failed to initialize (they are disabled).
    fn init_errors(&self) -> &[PluginError];

    /// Start a new budget window (see [`PluginHostOptions::fuel_per_window`]). Sessions start
    /// one every second of game time ([`crate::BUDGET_WINDOW_TICKS`]).
    fn begin_window(&mut self) {}
}

/// Budgets for [`WasmPluginHost`] (port of `JintPluginHostOptions`).
///
/// Time budgets are counted in *fuel*: one unit per executed wasm instruction of the guest
/// (QuickJS itself), plus one per 8 bytes of bulk memory copying. Whether a handler finishes
/// therefore never depends on the machine, its load or the build profile. That replaces the
/// Jint host's wall-clock timeout and statement limit. A release build runs about 0.5–1.3
/// billion units a second, a trivial handler uses about 80 thousand, and a plain JavaScript
/// loop about 550–1000 per iteration.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginHostOptions {
    /// Fuel per plugin per dispatch. The default, 50 million, is about 40–110 ms in a release
    /// build (the web host's budget is 50 ms), enough for a loop of 50–90 thousand simple
    /// iterations.
    pub fuel_per_call: u64,
    /// Fuel per plugin per budget window (one second of game time in a session, see
    /// [`PluginHost::begin_window`]), all its calls together. A plugin that goes over it gets a
    /// strike, and its handlers are skipped until the next window. The default, 200 million
    /// (four full calls), keeps a plugin on a busy hook from taking over the frame rate.
    pub fuel_per_window: u64,
    /// Fuel for compiling a plugin and running its top level. (The QuickJS runtime every plugin
    /// shares is built once per host and copied, so it is not counted.) A small plugin needs
    /// about 0.6 million; the default, 500 million, is about 0.4–1 s in a release build (the
    /// Jint host allows 1 s). Rebuilding a plugin after an overrun gets twice what its first
    /// start used (at least 10 million), never more than this.
    pub init_fuel: u64,
    /// Init fuel of all plugins together. Plugins that start after it is used up are disabled,
    /// so a pack with many plugins cannot hold up the game's start for long.
    pub init_fuel_total: u64,
    /// Cap on the plugin's JavaScript heap in bytes (the Jint host's `MemoryLimitBytes`).
    pub heap_limit_bytes: usize,
    /// Cap on the guest's linear memory in bytes: the heap, the 1 MiB stack, the guest's
    /// buffers and allocator slack.
    pub memory_limit_bytes: usize,
    /// Cap on the linear memory of all plugins together, checked as each plugin starts.
    pub memory_total_bytes: usize,
    /// Most plugins a host runs; the rest are disabled at start.
    pub max_plugins: usize,
    /// Cap on nested wasm calls, one of the two recursion limits (the other is the guest's
    /// 1 MiB stack). With the default, JavaScript recursion 2000 calls deep works and 5000
    /// is a budget overrun.
    pub max_call_depth: usize,
    /// Max result length of single-call string amplifiers (`repeat`, `padStart`, `padEnd`).
    pub max_string_length: usize,
    /// Budget overruns within [`PluginHostOptions::strike_window`] calls before a plugin is
    /// disabled for the session (TS: 3 consecutive).
    pub max_strikes: u32,
    /// How many of a plugin's handler calls a strike counts for. Successes in between do not
    /// wipe it, so a plugin that overruns every other call is still disabled.
    pub strike_window: u32,
    /// Most times a plugin is rebuilt after an overrun in one session; the next overrun
    /// disables it.
    pub max_rebuilds: u32,
}

impl Default for PluginHostOptions {
    fn default() -> Self {
        Self {
            fuel_per_call: 50_000_000,
            fuel_per_window: 200_000_000,
            init_fuel: 500_000_000,
            init_fuel_total: 2_000_000_000,
            heap_limit_bytes: 16_000_000,
            memory_limit_bytes: 40 << 20,
            memory_total_bytes: 512 << 20,
            max_plugins: 32,
            max_call_depth: 4_096,
            max_string_length: 1_000_000,
            max_strikes: 3,
            strike_window: 100,
            max_rebuilds: 10,
        }
    }
}

/// Least fuel a rebuild gets, however little the plugin's first start used.
const REBUILD_FUEL_FLOOR: u64 = 10_000_000;

struct Entry {
    spec: PluginSpec,
    granted_json: String,
    grants: MutationGrants,
    keys: KeyBudget,
    instance: Option<Box<dyn GuestInstance>>,
    disabled: bool,
    /// Fuel the plugin's last init or handler call used.
    last_fuel_used: Option<u64>,
    /// Handler calls so far.
    calls: u64,
    /// The call numbers of strikes still within the strike window.
    strikes: VecDeque<u64>,
    /// Rebuilds after overruns so far.
    rebuilds: u32,
    /// Init fuel for a rebuild.
    rebuild_fuel: u64,
    /// Fuel used in the current budget window.
    window_fuel: u64,
    /// Over its window budget: skipped until the next window.
    window_exhausted: bool,
}

/// The sandboxed plugin host: QuickJS compiled to WebAssembly, one instance per plugin (the TS
/// worker host's one worker per plugin), run by the wasmi interpreter. No JIT, so it also
/// works where JITs are forbidden.
///
/// Isolation: each plugin has its own linear memory and QuickJS runtime, and talks to the host
/// only through JSON text. Inside, the plugin runs in strict mode with no string compilation
/// (`eval`, `Function`), the web network/storage/worker globals pinned to `undefined`, no raw
/// memory (`ArrayBuffer` and typed arrays), no `Proxy` and no prototype setters. `Date.now()`
/// is constant and `Math.random()` is seeded the same way every time, so a plugin sees the
/// same world on every run.
///
/// Answers: each returned mutation is validated ([`crate::parse_mutation`]) and checked
/// against the pack's mutation capabilities ([`MutationGrants`], which also namespaces the ids
/// it names); at most [`crate::MAX_MUTATIONS_PER_CALL`] are kept per call. Results longer than
/// [`crate::MAX_RESULT_BYTES`] and error texts beyond [`crate::MAX_ERROR_MESSAGE_BYTES`] are
/// never copied out of the guest.
///
/// Budgets ([`PluginHostOptions`]): every call gets a fresh fuel budget; the heap, linear
/// memory and call depth are capped, and so is each plugin's fuel per budget window. A handler
/// that throws yields no mutations and a [`PluginErrorKind::Threw`] error. A handler that
/// overruns a budget also earns a strike, and its instance is rebuilt from source (fresh
/// state, like terminating a worker); three strikes within the strike window, or too many
/// rebuilds, disable the plugin for the session.
///
/// Everything here is deterministic: the same specs and the same dispatches give the same
/// results, on every machine.
pub struct WasmPluginHost {
    options: PluginHostOptions,
    sandbox: Option<Sandbox>,
    entries: Vec<Entry>,
    init_errors: Vec<PluginError>,
}

impl fmt::Debug for WasmPluginHost {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("WasmPluginHost")
            .field("options", &self.options)
            .field("plugins", &self.entries.iter().map(|entry| &entry.spec.id).collect::<Vec<_>>())
            .field("disabled", &self.disabled_plugins())
            .field("init_errors", &self.init_errors)
            .finish()
    }
}

impl WasmPluginHost {
    /// Compile the guest and build its shared runtime once, then initialize every plugin in
    /// spec order, each in its own instance. Plugins that fail to initialize, and those beyond
    /// the plugin, init-fuel and memory caps, are disabled and reported in
    /// [`PluginHost::init_errors`].
    pub fn new(specs: Vec<PluginSpec>, options: PluginHostOptions) -> Self {
        let limits = EngineLimits { memory_bytes: options.memory_limit_bytes, max_call_depth: options.max_call_depth };
        let mut entries: Vec<Entry> = specs
            .into_iter()
            .map(|spec| Entry {
                granted_json: serde_json::to_string(&spec.granted_hooks).expect("strings serialize"),
                grants: MutationGrants::new(spec.granted_mutations.as_deref()),
                keys: KeyBudget::default(),
                spec,
                instance: None,
                disabled: false,
                last_fuel_used: None,
                calls: 0,
                strikes: VecDeque::new(),
                rebuilds: 0,
                rebuild_fuel: options.init_fuel,
                window_fuel: 0,
                window_exhausted: false,
            })
            .collect();
        let mut init_errors = Vec::new();
        let sandbox = if entries.is_empty() {
            None
        } else {
            let sandbox = WasmiEngine::new(limits)
                .and_then(|engine| Sandbox::new(Box::new(engine), options.heap_limit_bytes, options.max_string_length));
            match sandbox {
                Ok(sandbox) => Some(sandbox),
                Err(fault) => {
                    // Fail closed: the guest did not load (a build defect) or the limits are too
                    // small for QuickJS to start.
                    for entry in &mut entries {
                        entry.disabled = true;
                        let message = format!(
                            "plugin {} failed to initialize — disabled: the plugin sandbox did not load: {fault}",
                            entry.spec.id
                        );
                        init_errors.push(init_error(&entry.spec.id, &message));
                    }
                    None
                }
            }
        };
        let mut host = Self { options, sandbox, entries, init_errors };
        let mut init_fuel_used: u64 = 0;
        let mut memory_used: usize = 0;
        for index in 0..host.entries.len() {
            if host.entries[index].disabled {
                continue;
            }
            let refused = if index >= host.options.max_plugins {
                Some(format!("the game has more than {} plugins", host.options.max_plugins))
            } else if init_fuel_used >= host.options.init_fuel_total {
                Some(format!("the plugins' combined startup budget ({} fuel) is used up", host.options.init_fuel_total))
            } else {
                None
            };
            if let Some(reason) = refused {
                host.disable_at_start(index, &reason);
                continue;
            }
            let fuel = host.options.init_fuel.min(host.options.init_fuel_total - init_fuel_used);
            let result = host.initialize(index, fuel);
            let entry = &mut host.entries[index];
            let used = entry.last_fuel_used.unwrap_or(0);
            init_fuel_used = init_fuel_used.saturating_add(used);
            entry.rebuild_fuel = used.saturating_mul(2).max(REBUILD_FUEL_FLOOR).min(host.options.init_fuel);
            match result {
                Err(error) => {
                    entry.disabled = true;
                    host.init_errors.push(error);
                }
                Ok(()) => {
                    memory_used =
                        memory_used.saturating_add(entry.instance.as_ref().map_or(0, |instance| instance.memory_len()));
                    if memory_used > host.options.memory_total_bytes {
                        let reason = format!(
                            "the plugins' combined memory budget ({} bytes) is used up",
                            host.options.memory_total_bytes
                        );
                        host.disable_at_start(index, &reason);
                    }
                }
            }
        }
        host
    }

    fn disable_at_start(&mut self, index: usize, reason: &str) {
        let entry = &mut self.entries[index];
        entry.disabled = true;
        entry.instance = None;
        let id = &entry.spec.id;
        self.init_errors.push(init_error(id, &format!("plugin {id} failed to initialize — disabled: {reason}")));
    }

    /// The budgets this host runs with.
    pub fn options(&self) -> &PluginHostOptions {
        &self.options
    }

    /// Ids of plugins currently disabled (init failure or strike limit), in spec order.
    pub fn disabled_plugins(&self) -> Vec<String> {
        self.entries.iter().filter(|entry| entry.disabled).map(|entry| entry.spec.id.clone()).collect()
    }

    /// The specs this host was built from, in order.
    pub fn specs(&self) -> impl Iterator<Item = &PluginSpec> {
        self.entries.iter().map(|entry| &entry.spec)
    }

    /// Fuel the plugin's most recent call used: its last handler call, or its initialization if
    /// no handler ran since. A creator tool can show it against
    /// [`PluginHostOptions::fuel_per_call`]. `None` for unknown plugins.
    pub fn last_fuel_used(&self, plugin_id: &str) -> Option<u64> {
        self.entries.iter().find(|entry| entry.spec.id == plugin_id).and_then(|entry| entry.last_fuel_used)
    }

    /// Build a fresh instance for the plugin and run its source with `fuel`.
    fn initialize(&mut self, index: usize, fuel: u64) -> Result<(), PluginError> {
        let entry = &mut self.entries[index];
        entry.instance = None;
        let id = entry.spec.id.clone();
        let failed =
            |message: &str| init_error(&id, &format!("plugin {id} failed to initialize — disabled: {message}"));
        let Some(sandbox) = &self.sandbox else {
            return Err(failed("the plugin sandbox is not available"));
        };
        let mut instance = sandbox.instance().map_err(|fault| failed(&describe_fault(&fault)))?;
        instance.set_fuel(fuel);
        let reply = sandbox::init(instance.as_mut(), &entry.granted_json, &entry.spec.source);
        entry.last_fuel_used = Some(instance.fuel_used(fuel));
        let reply = reply.map_err(|fault| failed(&describe_fault(&fault)))?;
        match reply.status {
            status::OK => {
                entry.instance = Some(instance);
                Ok(())
            }
            status::OUT_OF_MEMORY => Err(failed("exceeded its memory budget")),
            status::INIT_FAILED | status::NOT_A_FUNCTION_BODY => Err(failed(&reply.output)),
            status::BAD_STATE => Err(failed(&format!("failed ({})", reply.output))),
            other => Err(failed(&format!("failed (guest status {other}: {})", reply.output))),
        }
    }

    /// Run one plugin's handler. `None` when the plugin has nothing to say.
    fn call(&mut self, index: usize, hook: &str, payload_json: &str) -> Option<PluginDispatchResult> {
        let entry = &mut self.entries[index];
        if entry.disabled || entry.window_exhausted || !entry.spec.is_granted(hook) {
            return None;
        }
        let id = entry.spec.id.clone();
        let instance = entry.instance.as_mut()?;
        entry.calls += 1;
        instance.set_fuel(self.options.fuel_per_call);
        let reply = sandbox::dispatch(instance.as_mut(), hook, payload_json);
        let used = instance.fuel_used(self.options.fuel_per_call);
        entry.last_fuel_used = Some(used);
        entry.window_fuel = entry.window_fuel.saturating_add(used);
        let threw = |message: String| PluginDispatchResult {
            plugin_id: id.clone(),
            mutations: Vec::new(),
            errors: vec![PluginError {
                plugin_id: id.clone(),
                kind: PluginErrorKind::Threw,
                message,
                hook: Some(hook.to_owned()),
            }],
        };
        let mut result = match reply {
            Err(fault) => {
                let kind =
                    if fault == GuestFault::OutOfFuel { PluginErrorKind::Timeout } else { PluginErrorKind::Budget };
                self.strike(index, hook, kind, &describe_fault(&fault))
            }
            Ok(reply) => match reply.status {
                status::OK if reply.oversized => {
                    threw(format!("handler result is larger than {MAX_RESULT_BYTES} bytes"))
                }
                status::OK => match serde_json::from_str::<Value>(&reply.output) {
                    Ok(raw) => {
                        let (mutations, errors) =
                            check_answer(&id, &entry.spec.pack_id, &entry.grants, &mut entry.keys, &raw, hook);
                        PluginDispatchResult { plugin_id: id.clone(), mutations, errors }
                    }
                    Err(_) => threw("handler result could not be serialized".to_owned()),
                },
                // No handler registered for this hook.
                status::NO_HANDLER => PluginDispatchResult { plugin_id: id.clone(), ..PluginDispatchResult::default() },
                // Handler errors yield no mutations and are not strikes (the window budget
                // still counts what they burned).
                status::THREW => threw(format!("threw in {hook}: {}", reply.output)),
                status::NOT_SERIALIZABLE => threw("handler result could not be serialized".to_owned()),
                status::OUT_OF_MEMORY => {
                    self.strike(index, hook, PluginErrorKind::Budget, "exceeded its memory budget")
                }
                // Unknown guest state: treat like an overrun (the instance is suspect).
                other => self.strike(
                    index,
                    hook,
                    PluginErrorKind::Budget,
                    &format!("failed (guest status {other}: {})", reply.output),
                ),
            },
        };
        let entry = &mut self.entries[index];
        if !entry.disabled && !entry.window_exhausted && entry.window_fuel > self.options.fuel_per_window {
            entry.window_exhausted = true;
            result.errors.push(PluginError {
                plugin_id: id.clone(),
                kind: PluginErrorKind::Budget,
                message: format!(
                    "used more than its fuel budget for this second ({} fuel) in {hook}; skipped until the next",
                    self.options.fuel_per_window
                ),
                hook: Some(hook.to_owned()),
            });
            if let Some(disabled) = self.count_strike(index, hook) {
                result.errors.push(disabled);
            }
        }
        Some(result)
    }

    /// Record a strike at the plugin's current call. Disables it, and returns the error saying
    /// so, when it has [`PluginHostOptions::max_strikes`] strikes within the strike window.
    fn count_strike(&mut self, index: usize, hook: &str) -> Option<PluginError> {
        let entry = &mut self.entries[index];
        let window = u64::from(self.options.strike_window.max(1));
        entry.strikes.push_back(entry.calls);
        while entry.strikes.front().is_some_and(|&call| entry.calls - call >= window) {
            entry.strikes.pop_front();
        }
        if entry.strikes.len() < self.options.max_strikes as usize {
            return None;
        }
        entry.disabled = true;
        entry.instance = None;
        let id = &entry.spec.id;
        Some(PluginError {
            plugin_id: id.clone(),
            kind: PluginErrorKind::Disabled,
            message: format!("plugin {id} exceeded its budget {}× — disabled", entry.strikes.len()),
            hook: Some(hook.to_owned()),
        })
    }

    /// Count a budget overrun: throw the instance away (it may be mid-flight in an infinite
    /// loop or a half-built structure), then rebuild it from source, or disable the plugin at
    /// the strike or rebuild limit.
    fn strike(&mut self, index: usize, hook: &str, kind: PluginErrorKind, what: &str) -> PluginDispatchResult {
        let entry = &mut self.entries[index];
        let id = entry.spec.id.clone();
        entry.instance = None;
        let mut errors = vec![PluginError {
            plugin_id: id.clone(),
            kind,
            message: format!("{what} in {hook}"),
            hook: Some(hook.to_owned()),
        }];
        if let Some(disabled) = self.count_strike(index, hook) {
            errors.push(disabled);
        } else if self.entries[index].rebuilds >= self.options.max_rebuilds {
            let entry = &mut self.entries[index];
            entry.disabled = true;
            errors.push(PluginError {
                plugin_id: id.clone(),
                kind: PluginErrorKind::Disabled,
                message: format!("plugin {id} was restarted {}× after overruns — disabled", entry.rebuilds),
                hook: Some(hook.to_owned()),
            });
        } else {
            self.entries[index].rebuilds += 1;
            let fuel = self.entries[index].rebuild_fuel;
            if let Err(error) = self.initialize(index, fuel) {
                self.entries[index].disabled = true;
                errors.push(PluginError { hook: Some(hook.to_owned()), ..error });
            }
        }
        PluginDispatchResult { plugin_id: id, mutations: Vec::new(), errors }
    }
}

impl PluginHost for WasmPluginHost {
    fn dispatch(&mut self, hook: &str, payload_json: &str) -> Vec<PluginDispatchResult> {
        let callers: Vec<usize> = (0..self.entries.len())
            .filter(|&index| {
                let entry = &self.entries[index];
                !entry.disabled && !entry.window_exhausted && entry.spec.is_granted(hook)
            })
            .collect();
        if callers.is_empty() {
            return Vec::new();
        }
        if let Err(error) = serde_json::from_str::<serde::de::IgnoredAny>(payload_json) {
            return callers
                .iter()
                .map(|&index| {
                    let id = self.entries[index].spec.id.clone();
                    PluginDispatchResult {
                        plugin_id: id.clone(),
                        mutations: Vec::new(),
                        errors: vec![PluginError {
                            plugin_id: id,
                            kind: PluginErrorKind::Threw,
                            message: format!("payload is not JSON-serializable: {error}"),
                            hook: Some(hook.to_owned()),
                        }],
                    }
                })
                .collect();
        }
        callers
            .into_iter()
            .filter_map(|index| self.call(index, hook, payload_json))
            .filter(|result| !result.mutations.is_empty() || !result.errors.is_empty())
            .collect()
    }

    fn init_errors(&self) -> &[PluginError] {
        &self.init_errors
    }

    fn begin_window(&mut self) {
        for entry in &mut self.entries {
            entry.window_fuel = 0;
            entry.window_exhausted = false;
        }
    }
}

fn init_error(plugin_id: &str, message: &str) -> PluginError {
    PluginError {
        plugin_id: plugin_id.to_owned(),
        kind: PluginErrorKind::Init,
        message: message.to_owned(),
        hook: None,
    }
}

/// The C# host's `Describe(Exception)` for sandbox faults.
fn describe_fault(fault: &GuestFault) -> String {
    match fault {
        GuestFault::OutOfFuel => "exceeded its fuel budget".to_owned(),
        GuestFault::StackOverflow => "exceeded its recursion budget".to_owned(),
        GuestFault::Aborted => "failed (the sandbox aborted)".to_owned(),
        GuestFault::Other(message) => format!("failed ({message})"),
    }
}
