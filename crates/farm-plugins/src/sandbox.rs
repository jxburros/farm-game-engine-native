//! The WebAssembly side of the host: the guest ABI protocol, and the engine that runs guests.
//!
//! The protocol (writing inputs, calling `fp_prepare` / `fp_init` / `fp_dispatch`, reading the
//! output, and starting instances from a prepared memory image) is written once against
//! [`GuestEngine`] and [`GuestInstance`], a handful of primitives any wasm engine provides.
//! [`WasmiEngine`] implements them with wasmi. A wasmtime backend (JIT or Pulley) could be added
//! behind a feature by implementing the same two traits; nothing else would change.
//!
//! Fuel is the guest's own: `tools/plugin-guest/meter` compiled a counter into the guest (the
//! exported `fp_fuel` global, one unit per executed wasm instruction), so budgets mean the
//! same on every engine. wasmi's built-in fuel is off; it charges a loop's whole body up
//! front, which overcharges QuickJS's interpreter loop by a factor of a few hundred.

use std::fmt;
use wasmi::{
    Caller, CompilationMode, Config, Engine, Extern, Global, Instance, Linker, Memory, Module, Store, StoreLimits,
    StoreLimitsBuilder, TrapCode, TypedFunc, Val,
};

/// The checked-in guest: QuickJS compiled to `wasm32-wasip1` (see `guest/README.md`).
pub const GUEST_WASM: &[u8] = include_bytes!("../guest/farm_plugin_guest.wasm");

/// Fuel for `fp_prepare`, which runs our own code once per host (about 8 million).
const PREPARE_FUEL: u64 = 1 << 32;

/// Bytes per wasm page.
const PAGE: usize = 65_536;

/// Guest status codes (`STATUS_*` in `crates/farm-plugin-guest/src/lib.rs`).
pub(crate) mod status {
    pub const OK: i32 = 0;
    pub const NO_HANDLER: i32 = 1;
    pub const THREW: i32 = 2;
    pub const NOT_SERIALIZABLE: i32 = 3;
    pub const OUT_OF_MEMORY: i32 = 4;
    pub const INIT_FAILED: i32 = 5;
    pub const NOT_A_FUNCTION_BODY: i32 = 6;
    pub const BAD_STATE: i32 = 7;
}

/// The guest functions the protocol calls.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum GuestExport {
    /// `fp_buffer(len) -> address`
    Buffer,
    /// `fp_prepare(heap_limit, max_string_len) -> status`
    Prepare,
    /// `fp_init(granted_len, source_len) -> status`
    Init,
    /// `fp_dispatch(hook_len, payload_len) -> status`
    Dispatch,
    /// `fp_output_ptr() -> address`
    OutputPtr,
    /// `fp_output_len() -> len`
    OutputLen,
}

/// Why a guest call did not return. Any fault leaves the instance unusable.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) enum GuestFault {
    /// The call used up its fuel budget.
    OutOfFuel,
    /// The wasm call stack or the guest's shadow stack overflowed (deep recursion).
    StackOverflow,
    /// The guest aborted (`unreachable`, `proc_exit`), e.g. its own allocator failed.
    Aborted,
    /// Anything else (a different trap, a missing export, an input too large for the ABI).
    Other(String),
}

impl fmt::Display for GuestFault {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::OutOfFuel => f.write_str("out of fuel"),
            Self::StackOverflow => f.write_str("stack overflow"),
            Self::Aborted => f.write_str("the sandbox aborted"),
            Self::Other(message) => f.write_str(message),
        }
    }
}

/// Limits applied to every instance of an engine.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) struct EngineLimits {
    /// Cap on the guest's linear memory, in bytes.
    pub memory_bytes: usize,
    /// Cap on nested wasm calls.
    pub max_call_depth: usize,
}

/// A compiled guest module that can make fresh, isolated instances.
pub(crate) trait GuestEngine: Send {
    /// A new instance in the module's initial state.
    fn instantiate(&self) -> Result<Box<dyn GuestInstance>, GuestFault>;
}

/// One guest instance: the primitives the ABI protocol needs.
pub(crate) trait GuestInstance: Send {
    /// Set the fuel available to the following calls.
    fn set_fuel(&mut self, fuel: u64);
    /// Fuel left from the last [`GuestInstance::set_fuel`]. Negative after an overrun: the
    /// counter is checked at loop heads and function entries, so it can dip below zero.
    fn fuel(&self) -> i64;
    /// Call an export that takes and returns `i32`s.
    fn call(&mut self, export: GuestExport, args: &[i32]) -> Result<i32, GuestFault>;
    /// Copy `bytes` into linear memory at `address`.
    fn write(&mut self, address: u32, bytes: &[u8]) -> Result<(), GuestFault>;
    /// Copy `len` bytes out of linear memory at `address`.
    fn read(&self, address: u32, len: u32) -> Result<Vec<u8>, GuestFault>;
    /// Current size of linear memory in bytes.
    fn memory_len(&self) -> usize;
    /// Grow linear memory to at least `len` bytes.
    fn grow_memory_to(&mut self, len: usize) -> Result<(), GuestFault>;

    /// Fuel used since `set_fuel(budget)`.
    fn fuel_used(&self, budget: u64) -> u64 {
        let budget = i64::try_from(budget).unwrap_or(i64::MAX);
        u64::try_from(budget.saturating_sub(self.fuel())).unwrap_or(0)
    }
}

/// A guest call's answer: a status and the output text.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct GuestReply {
    pub status: i32,
    pub output: String,
}

fn to_i32(len: usize) -> Result<i32, GuestFault> {
    i32::try_from(len).map_err(|_| GuestFault::Other("input too large for the plugin sandbox".to_owned()))
}

/// Read the output of the last call.
fn reply(instance: &mut dyn GuestInstance, status: i32) -> Result<GuestReply, GuestFault> {
    let output_ptr = instance.call(GuestExport::OutputPtr, &[])? as u32;
    let output_len = instance.call(GuestExport::OutputLen, &[])? as u32;
    let output = instance.read(output_ptr, output_len)?;
    Ok(GuestReply { status, output: String::from_utf8_lossy(&output).into_owned() })
}

/// Write `parts` back to back into the guest's I/O buffer, call `export` with their lengths,
/// and read the output.
fn call_with_input(
    instance: &mut dyn GuestInstance,
    parts: [&[u8]; 2],
    export: GuestExport,
) -> Result<GuestReply, GuestFault> {
    let first = to_i32(parts[0].len())?;
    let second = to_i32(parts[1].len())?;
    let total = to_i32(parts[0].len() + parts[1].len())?;
    let address = instance.call(GuestExport::Buffer, &[total])? as u32;
    instance.write(address, parts[0])?;
    instance.write(address.wrapping_add(first as u32), parts[1])?;
    let status = instance.call(export, &[first, second])?;
    reply(instance, status)
}

/// `fp_prepare`: build the runtime every plugin shares.
pub(crate) fn prepare(
    instance: &mut dyn GuestInstance,
    heap_limit: usize,
    max_string_length: usize,
) -> Result<GuestReply, GuestFault> {
    let heap = i32::try_from(heap_limit).unwrap_or(i32::MAX);
    let max_string = i32::try_from(max_string_length).unwrap_or(i32::MAX);
    let status = instance.call(GuestExport::Prepare, &[heap, max_string])?;
    reply(instance, status)
}

/// `fp_init`: compile the plugin and run its top level.
pub(crate) fn init(
    instance: &mut dyn GuestInstance,
    granted_json: &str,
    source: &str,
) -> Result<GuestReply, GuestFault> {
    call_with_input(instance, [granted_json.as_bytes(), source.as_bytes()], GuestExport::Init)
}

/// `fp_dispatch`: run the plugin's handler for `hook`.
pub(crate) fn dispatch(
    instance: &mut dyn GuestInstance,
    hook: &str,
    payload_json: &str,
) -> Result<GuestReply, GuestFault> {
    call_with_input(instance, [hook.as_bytes(), payload_json.as_bytes()], GuestExport::Dispatch)
}

/// An engine plus the memory image of a prepared guest: `fp_prepare` runs once, and every
/// plugin's instance starts from a copy of the result. Instances made this way are exactly
/// what calling `fp_prepare` in each would give (the image is the guest's whole state), in a
/// fraction of the time.
pub(crate) struct Sandbox {
    engine: Box<dyn GuestEngine>,
    image: Vec<u8>,
}

impl fmt::Debug for Sandbox {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Sandbox").field("image_bytes", &self.image.len()).finish_non_exhaustive()
    }
}

impl Sandbox {
    /// Prepare the guest with these limits and keep the image.
    pub(crate) fn new(
        engine: Box<dyn GuestEngine>,
        heap_limit: usize,
        max_string_length: usize,
    ) -> Result<Self, GuestFault> {
        let mut template = engine.instantiate()?;
        template.set_fuel(PREPARE_FUEL);
        let reply = prepare(template.as_mut(), heap_limit, max_string_length)?;
        if reply.status != status::OK {
            return Err(GuestFault::Other(format!("the sandbox did not start ({})", reply.output)));
        }
        let image = template.read(0, u32::try_from(template.memory_len()).unwrap_or(u32::MAX))?;
        Ok(Self { engine, image })
    }

    /// A prepared instance, ready for `fp_init`.
    pub(crate) fn instance(&self) -> Result<Box<dyn GuestInstance>, GuestFault> {
        let mut instance = self.engine.instantiate()?;
        instance.grow_memory_to(self.image.len())?;
        instance.write(0, &self.image)?;
        Ok(instance)
    }
}

// ---------------------------------------------------------------------------------------------
// wasmi

/// Store data: the resource limits wasmi enforces on memory growth.
struct StoreData {
    limits: StoreLimits,
}

/// The guest module compiled once for wasmi, shared by all of a host's instances.
pub(crate) struct WasmiEngine {
    engine: Engine,
    module: Module,
    linker: Linker<StoreData>,
    limits: EngineLimits,
}

impl fmt::Debug for WasmiEngine {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("WasmiEngine").field("limits", &self.limits).finish_non_exhaustive()
    }
}

impl WasmiEngine {
    /// Compile the checked-in guest.
    pub(crate) fn new(limits: EngineLimits) -> Result<Self, GuestFault> {
        let mut config = Config::default();
        config
            // The guest meters itself (see the module docs).
            .consume_fuel(false)
            // Validate everything up front, translate each function on first use.
            .compilation_mode(CompilationMode::LazyTranslation)
            .set_max_recursion_depth(limits.max_call_depth)
            .wasm_multi_memory(false)
            .allow_start_fn(false);
        let engine = Engine::new(&config);
        let module = Module::new(&engine, GUEST_WASM).map_err(|error| GuestFault::Other(error.to_string()))?;
        let mut linker = Linker::new(&engine);
        wasi::define_stubs(&mut linker).map_err(|error| GuestFault::Other(error.to_string()))?;
        Ok(Self { engine, module, linker, limits })
    }
}

impl GuestEngine for WasmiEngine {
    fn instantiate(&self) -> Result<Box<dyn GuestInstance>, GuestFault> {
        let limits =
            StoreLimitsBuilder::new().memory_size(self.limits.memory_bytes).memories(1).tables(4).instances(1).build();
        let mut store = Store::new(&self.engine, StoreData { limits });
        store.limiter(|data| &mut data.limits);
        let instance =
            self.linker.instantiate_and_start(&mut store, &self.module).map_err(|error| classify(&error, false))?;
        let memory = instance
            .get_memory(&store, "memory")
            .ok_or_else(|| GuestFault::Other("the guest exports no memory".to_owned()))?;
        let fuel = instance
            .get_global(&store, "fp_fuel")
            .ok_or_else(|| GuestFault::Other("the guest exports no fuel counter".to_owned()))?;
        let exports = Exports::new(&store, &instance)?;
        Ok(Box::new(WasmiInstance { store, memory, fuel, exports }))
    }
}

struct Exports {
    buffer: TypedFunc<i32, i32>,
    prepare: TypedFunc<(i32, i32), i32>,
    init: TypedFunc<(i32, i32), i32>,
    dispatch: TypedFunc<(i32, i32), i32>,
    output_ptr: TypedFunc<(), i32>,
    output_len: TypedFunc<(), i32>,
}

impl Exports {
    fn new(store: &Store<StoreData>, instance: &Instance) -> Result<Self, GuestFault> {
        fn missing(name: &str) -> impl FnOnce(wasmi::Error) -> GuestFault + '_ {
            move |error| GuestFault::Other(format!("guest export {name}: {error}"))
        }
        Ok(Self {
            buffer: instance.get_typed_func(store, "fp_buffer").map_err(missing("fp_buffer"))?,
            prepare: instance.get_typed_func(store, "fp_prepare").map_err(missing("fp_prepare"))?,
            init: instance.get_typed_func(store, "fp_init").map_err(missing("fp_init"))?,
            dispatch: instance.get_typed_func(store, "fp_dispatch").map_err(missing("fp_dispatch"))?,
            output_ptr: instance.get_typed_func(store, "fp_output_ptr").map_err(missing("fp_output_ptr"))?,
            output_len: instance.get_typed_func(store, "fp_output_len").map_err(missing("fp_output_len"))?,
        })
    }
}

struct WasmiInstance {
    store: Store<StoreData>,
    memory: Memory,
    fuel: Global,
    exports: Exports,
}

/// Map a wasmi error to a fault. `out_of_fuel` says whether the guest's counter went negative
/// (its metering traps with `unreachable` then).
fn classify(error: &wasmi::Error, out_of_fuel: bool) -> GuestFault {
    if error.i32_exit_status().is_some() {
        return GuestFault::Aborted;
    }
    match error.as_trap_code() {
        Some(TrapCode::UnreachableCodeReached) if out_of_fuel => GuestFault::OutOfFuel,
        Some(TrapCode::UnreachableCodeReached) => GuestFault::Aborted,
        // The shadow stack sits below the heap (`--stack-first`): overflowing it walks off the
        // bottom of linear memory.
        Some(TrapCode::StackOverflow | TrapCode::MemoryOutOfBounds) => GuestFault::StackOverflow,
        Some(code) => GuestFault::Other(format!("wasm trap: {code}")),
        None => GuestFault::Other(error.to_string()),
    }
}

impl GuestInstance for WasmiInstance {
    fn set_fuel(&mut self, fuel: u64) {
        let fuel = i64::try_from(fuel).unwrap_or(i64::MAX);
        // The global is a mutable i64 (the meter adds it), so this cannot fail.
        let _ = self.fuel.set(&mut self.store, Val::I64(fuel));
    }

    fn fuel(&self) -> i64 {
        match self.fuel.get(&self.store) {
            Val::I64(fuel) => fuel,
            _ => 0,
        }
    }

    fn call(&mut self, export: GuestExport, args: &[i32]) -> Result<i32, GuestFault> {
        let arg = |index: usize| args.get(index).copied().unwrap_or(0);
        let result = match export {
            GuestExport::Buffer => self.exports.buffer.call(&mut self.store, arg(0)),
            GuestExport::Prepare => self.exports.prepare.call(&mut self.store, (arg(0), arg(1))),
            GuestExport::Init => self.exports.init.call(&mut self.store, (arg(0), arg(1))),
            GuestExport::Dispatch => self.exports.dispatch.call(&mut self.store, (arg(0), arg(1))),
            GuestExport::OutputPtr => self.exports.output_ptr.call(&mut self.store, ()),
            GuestExport::OutputLen => self.exports.output_len.call(&mut self.store, ()),
        };
        result.map_err(|error| classify(&error, self.fuel() < 0))
    }

    fn write(&mut self, address: u32, bytes: &[u8]) -> Result<(), GuestFault> {
        self.memory
            .write(&mut self.store, address as usize, bytes)
            .map_err(|_| GuestFault::Other("guest buffer out of bounds".to_owned()))
    }

    fn read(&self, address: u32, len: u32) -> Result<Vec<u8>, GuestFault> {
        let data = self.memory.data(&self.store);
        let start = address as usize;
        data.get(start..start.saturating_add(len as usize))
            .map(<[u8]>::to_vec)
            .ok_or_else(|| GuestFault::Other("guest output out of bounds".to_owned()))
    }

    fn memory_len(&self) -> usize {
        self.memory.data_size(&self.store)
    }

    fn grow_memory_to(&mut self, len: usize) -> Result<(), GuestFault> {
        let current = self.memory.data_size(&self.store);
        if len <= current {
            return Ok(());
        }
        let pages = (len - current).div_ceil(PAGE) as u64;
        self.memory
            .grow(&mut self.store, pages)
            .map(|_| ())
            .map_err(|error| GuestFault::Other(format!("guest memory could not grow: {error}")))
    }
}

/// The WASI preview 1 functions the guest imports, as inert stubs: no files, no environment,
/// a clock stuck at the Unix epoch, deterministic "random" bytes, stdout/stderr discarded, and
/// `proc_exit` ends the call.
mod wasi {
    use super::{Caller, Extern, Linker, StoreData};

    const MODULE: &str = "wasi_snapshot_preview1";
    const SUCCESS: i32 = 0;
    /// `EBADF`: no file descriptors exist.
    const BADF: i32 = 8;
    /// `EFAULT`: a pointer outside linear memory.
    const FAULT: i32 = 21;

    fn memory_of(caller: &Caller<'_, StoreData>) -> Option<wasmi::Memory> {
        caller.get_export("memory").and_then(Extern::into_memory)
    }

    fn write_bytes(caller: &mut Caller<'_, StoreData>, address: i32, bytes: &[u8]) -> i32 {
        match memory_of(caller) {
            Some(memory) if memory.write(&mut *caller, address as u32 as usize, bytes).is_ok() => SUCCESS,
            _ => FAULT,
        }
    }

    fn read_u32(caller: &Caller<'_, StoreData>, address: u32) -> Option<u32> {
        let memory = memory_of(caller)?;
        let data = memory.data(caller);
        let start = address as usize;
        let bytes = data.get(start..start.checked_add(4)?)?;
        Some(u32::from_le_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]))
    }

    pub(super) fn define_stubs(linker: &mut Linker<StoreData>) -> Result<(), wasmi::Error> {
        linker.func_wrap(MODULE, "environ_sizes_get", |mut caller: Caller<'_, StoreData>, count: i32, size: i32| {
            let first = write_bytes(&mut caller, count, &0u32.to_le_bytes());
            if first != SUCCESS {
                return first;
            }
            write_bytes(&mut caller, size, &0u32.to_le_bytes())
        })?;
        linker.func_wrap(MODULE, "environ_get", |_: Caller<'_, StoreData>, _environ: i32, _buf: i32| SUCCESS)?;
        linker.func_wrap(MODULE, "args_sizes_get", |mut caller: Caller<'_, StoreData>, count: i32, size: i32| {
            let first = write_bytes(&mut caller, count, &0u32.to_le_bytes());
            if first != SUCCESS {
                return first;
            }
            write_bytes(&mut caller, size, &0u32.to_le_bytes())
        })?;
        linker.func_wrap(MODULE, "args_get", |_: Caller<'_, StoreData>, _argv: i32, _buf: i32| SUCCESS)?;
        // Every clock reads 0 (1970-01-01T00:00:00Z): `Date.now()` and QuickJS's `Math.random`
        // seed are the same on every run and machine.
        linker.func_wrap(
            MODULE,
            "clock_time_get",
            |mut caller: Caller<'_, StoreData>, _id: i32, _precision: i64, time: i32| {
                write_bytes(&mut caller, time, &0u64.to_le_bytes())
            },
        )?;
        linker.func_wrap(MODULE, "clock_res_get", |mut caller: Caller<'_, StoreData>, _id: i32, resolution: i32| {
            write_bytes(&mut caller, resolution, &1u64.to_le_bytes())
        })?;
        // Deterministic bytes (a fixed xorshift sequence restarted on every call).
        linker.func_wrap(MODULE, "random_get", |mut caller: Caller<'_, StoreData>, buf: i32, len: i32| {
            let mut state: u32 = 0x9E37_79B9;
            let bytes: Vec<u8> = (0..len.max(0))
                .map(|_| {
                    state ^= state << 13;
                    state ^= state >> 17;
                    state ^= state << 5;
                    state.to_le_bytes()[0]
                })
                .collect();
            write_bytes(&mut caller, buf, &bytes)
        })?;
        // stdout/stderr are discarded; report everything as written so writers don't retry.
        linker.func_wrap(
            MODULE,
            "fd_write",
            |mut caller: Caller<'_, StoreData>, _fd: i32, iovs: i32, iovs_len: i32, written: i32| {
                let mut total: u32 = 0;
                for index in 0..iovs_len.max(0) as u32 {
                    let entry = (iovs as u32).wrapping_add(index.wrapping_mul(8));
                    match read_u32(&caller, entry.wrapping_add(4)) {
                        Some(len) => total = total.wrapping_add(len),
                        None => return FAULT,
                    }
                }
                write_bytes(&mut caller, written, &total.to_le_bytes())
            },
        )?;
        linker.func_wrap(MODULE, "fd_close", |_: Caller<'_, StoreData>, _fd: i32| BADF)?;
        linker.func_wrap(MODULE, "fd_fdstat_get", |_: Caller<'_, StoreData>, _fd: i32, _stat: i32| BADF)?;
        linker.func_wrap(
            MODULE,
            "fd_seek",
            |_: Caller<'_, StoreData>, _fd: i32, _offset: i64, _whence: i32, _new_offset: i32| BADF,
        )?;
        linker.func_wrap(MODULE, "fd_prestat_get", |_: Caller<'_, StoreData>, _fd: i32, _prestat: i32| BADF)?;
        linker.func_wrap(
            MODULE,
            "fd_prestat_dir_name",
            |_: Caller<'_, StoreData>, _fd: i32, _path: i32, _len: i32| BADF,
        )?;
        linker.func_wrap(MODULE, "proc_exit", |_: Caller<'_, StoreData>, code: i32| -> Result<(), wasmi::Error> {
            Err(wasmi::Error::i32_exit(code))
        })?;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const LIMITS: EngineLimits = EngineLimits { memory_bytes: 40 << 20, max_call_depth: 4096 };

    fn engine() -> WasmiEngine {
        WasmiEngine::new(LIMITS).expect("the guest loads")
    }

    /// A prepared instance with plenty of fuel.
    fn fresh() -> Box<dyn GuestInstance> {
        let sandbox = Sandbox::new(Box::new(engine()), 16_000_000, 1_000_000).expect("the guest prepares");
        let mut instance = sandbox.instance().expect("the guest instantiates");
        instance.set_fuel(1 << 40);
        instance
    }

    #[test]
    fn every_guest_import_is_a_stub() {
        let engine = engine();
        let imports: Vec<String> =
            engine.module.imports().map(|import| format!("{}.{}", import.module(), import.name())).collect();
        assert!(!imports.is_empty());
        assert!(imports.iter().all(|name| name.starts_with("wasi_snapshot_preview1.")), "{imports:?}");
        // Instantiation resolves every import against the stubs.
        engine.instantiate().expect("all imports are defined");
    }

    #[test]
    fn the_protocol_rejects_calls_out_of_order() {
        let mut instance = engine().instantiate().unwrap();
        instance.set_fuel(1 << 40);
        let bad = |output: &str| GuestReply { status: status::BAD_STATE, output: output.to_owned() };
        assert_eq!(dispatch(instance.as_mut(), "onDayStart", "{}"), Ok(bad("the plugin is not initialized")));
        assert_eq!(init(instance.as_mut(), "[]", ""), Ok(bad("the sandbox is not prepared")));
        assert_eq!(prepare(instance.as_mut(), 16_000_000, 1_000_000).map(|r| r.status), Ok(status::OK));
        assert_eq!(prepare(instance.as_mut(), 16_000_000, 1_000_000), Ok(bad("already prepared")));
        assert_eq!(init(instance.as_mut(), "[]", "").map(|r| r.status), Ok(status::OK));
        assert_eq!(init(instance.as_mut(), "[]", ""), Ok(bad("already initialized")));
    }

    #[test]
    fn a_prepared_image_behaves_like_preparing_in_place() {
        let source = "var n = 0; api.on('onDayStart', p => [{ type: 'message', text: (++n) + ':' + Math.random() + ':' + p.day }]);";
        let run = |mut instance: Box<dyn GuestInstance>| {
            instance.set_fuel(1 << 40);
            let init_reply = init(instance.as_mut(), r#"["onDayStart"]"#, source).unwrap();
            let used = instance.fuel_used(1 << 40);
            let replies: Vec<GuestReply> =
                (0..3).map(|_| dispatch(instance.as_mut(), "onDayStart", r#"{"day":2}"#).unwrap()).collect();
            (init_reply, used, replies)
        };
        let in_place = {
            let mut instance = engine().instantiate().unwrap();
            instance.set_fuel(1 << 40);
            assert_eq!(prepare(instance.as_mut(), 16_000_000, 1_000_000).unwrap().status, status::OK);
            run(instance)
        };
        let sandbox = Sandbox::new(Box::new(engine()), 16_000_000, 1_000_000).unwrap();
        assert_eq!(run(sandbox.instance().unwrap()), in_place);
        assert_eq!(run(sandbox.instance().unwrap()), in_place);
        let third = &in_place.2[2].output;
        assert!(third.starts_with(r#"[{"type":"message","text":"3:0."#) && third.ends_with(r#":2"}]"#), "{third}");
    }

    #[test]
    fn a_heap_limit_too_small_for_quickjs_fails_to_prepare() {
        let error = Sandbox::new(Box::new(engine()), 10_000, 1_000_000).expect_err("QuickJS needs more than 10 kB");
        assert_eq!(error, GuestFault::Other("the sandbox did not start (out of memory)".to_owned()));
    }

    #[test]
    fn statuses_and_outputs() {
        let mut instance = fresh();
        let source = "api.on('onDayStart', p => [{ type: 'message', text: 'day ' + p.day }]);\
                      api.on('onAction', () => { throw new TypeError('bad action'); });";
        let reply = init(instance.as_mut(), r#"["onDayStart","onAction"]"#, source).unwrap();
        assert_eq!(reply, GuestReply { status: status::OK, output: String::new() });
        let reply = dispatch(instance.as_mut(), "onDayStart", r#"{"day":2}"#).unwrap();
        assert_eq!(
            reply,
            GuestReply { status: status::OK, output: r#"[{"type":"message","text":"day 2"}]"#.to_owned() }
        );
        let reply = dispatch(instance.as_mut(), "onAction", "{}").unwrap();
        assert_eq!(reply, GuestReply { status: status::THREW, output: "bad action".to_owned() });
        let reply = dispatch(instance.as_mut(), "onCropHarvest", "{}").unwrap();
        assert_eq!(reply.status, status::NO_HANDLER);
    }

    #[test]
    fn running_out_of_fuel_is_its_own_fault() {
        let mut instance = fresh();
        init(instance.as_mut(), r#"["onDayStart"]"#, "api.on('onDayStart', () => { for (;;) {} })").unwrap();
        instance.set_fuel(100_000);
        assert_eq!(dispatch(instance.as_mut(), "onDayStart", "{}"), Err(GuestFault::OutOfFuel));
        assert!(instance.fuel() < 0);
        assert!(instance.fuel_used(100_000) > 100_000);
    }

    #[test]
    fn fuel_is_counted_the_same_way_every_time() {
        let used = || {
            let mut instance = fresh();
            let source = "api.on('onDayStart', p => { var s = 0; for (var i = 0; i < 100; i++) s += i; return [{ type: 'message', text: String(s) }]; })";
            init(instance.as_mut(), r#"["onDayStart"]"#, source).unwrap();
            let init_used = instance.fuel_used(1 << 40);
            instance.set_fuel(1 << 40);
            dispatch(instance.as_mut(), "onDayStart", "{}").unwrap();
            (init_used, instance.fuel_used(1 << 40))
        };
        let first = used();
        assert!(first.0 > 0 && first.1 > 0);
        assert_eq!(first, used());
    }
}
