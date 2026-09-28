//! Guest half of the `farm-plugins` sandbox: QuickJS (quickjs-ng, through `rquickjs-sys`)
//! compiled to `wasm32-wasip1`. The host (`crates/farm-plugins`) runs one instance of this
//! module per plugin in the wasmi interpreter, with a fuel budget per call and a cap on linear
//! memory, and discards the instance after any trap.
//!
//! # ABI
//!
//! All strings are UTF-8. The host writes inputs into the guest's I/O buffer and reads outputs
//! from the output buffer; nothing else crosses the boundary.
//!
//! | Export | Signature | Meaning |
//! |---|---|---|
//! | `fp_buffer` | `(len: i32) -> i32` | Make the I/O buffer `len` bytes long; returns its address. |
//! | `fp_prepare` | `(heap_limit, max_string_len: i32) -> i32` | Build the runtime, harden it and set up the bootstrap: everything plugins share. |
//! | `fp_init` | `(granted_len, source_len: i32) -> i32` | The buffer holds the granted-hooks JSON array, then the plugin source. Compiles the plugin and runs its top level. |
//! | `fp_dispatch` | `(hook_len, payload_len: i32) -> i32` | The buffer holds the hook name, then the payload JSON. Calls the plugin's handler. |
//! | `fp_output_ptr` / `fp_output_len` | `() -> i32` | The output text of the last call. |
//!
//! The status codes are the `STATUS_*` constants below; `farm-plugins` mirrors them. The
//! output is the result JSON for [`STATUS_OK`] and a message for the error statuses.
//!
//! The host calls `fp_prepare` once, copies the linear memory as an image, and starts each
//! plugin's instance from that image, so a plugin's instance only pays for `fp_init`. Nothing
//! outside linear memory changes during `fp_prepare` (the stack pointer is back where it
//! started when it returns), so the image is the whole state.
//!
//! `tools/plugin-guest/meter` adds one more export, the `fp_fuel` counter (see its docs).
//!
//! # Sandbox
//!
//! - The plugin's context never gets QuickJS's `Eval` intrinsic, so `eval`, `Function` and the
//!   async/generator function constructors throw. Our own scripts and the plugin are compiled
//!   in a separate compiler context and moved over as bytecode (`JS_WriteObject` /
//!   `JS_ReadObject`, which binds the functions to the reading context's realm). The compiler
//!   context is freed before plugin code runs.
//! - No `Proxy`, typed arrays, `ArrayBuffer`, `WeakRef` or `performance` intrinsics; the
//!   hardening script pins the web network/storage/worker globals to `undefined`, removes the
//!   prototype setters, caps the string amplifiers and installs a no-op `console` (the same
//!   script as the C# Jint host).
//! - Strict mode for everything.
//! - The JavaScript heap is capped by our allocator (`heap_limit`). A refused allocation raises
//!   QuickJS's out-of-memory error; from then on every allocation fails and the interrupt
//!   handler interrupts at every check (an uncatchable error), so a plugin cannot catch its way
//!   past the cap. The call reports [`STATUS_OUT_OF_MEMORY`] however it ended, and the host
//!   discards the instance.
//! - QuickJS's own stack check is compiled out under WASI. Deep recursion exhausts the 1 MiB
//!   shadow stack (placed first in memory, so it traps) or the host's call-depth limit; the
//!   host counts either trap as a budget overrun.
//! - Time and randomness come from the host's WASI stubs: the clock is constant and
//!   `random_get` is deterministic, so `Date.now()` and `Math.random()` repeat exactly.
//!
//! # Unsafe code
//!
//! Every `unsafe` block is a call into the QuickJS C API or libc's allocator. The rules they
//! rely on: each `JSValue` we own is freed exactly once (the [`Value`] guard), strings handed to
//! `JS_Eval` are NUL-terminated, C strings from `JS_ToCStringLen` are copied and released
//! before returning, and the runtime and sandbox context live for the rest of the instance
//! (the host drops the whole instance instead of tearing QuickJS down). The module is
//! single-threaded.
#![deny(unsafe_op_in_unsafe_fn)]

use rquickjs_sys as q;
use std::cell::{Cell, RefCell};
use std::ffi::{c_char, c_int, c_void};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};

/// The call succeeded; the output is the handler's result as JSON (`"[]"` for a non-array).
pub const STATUS_OK: i32 = 0;
/// No handler is registered for the hook (the dispatcher returned `undefined`).
pub const STATUS_NO_HANDLER: i32 = 1;
/// The handler threw; the output is the error message.
pub const STATUS_THREW: i32 = 2;
/// The handler's result could not be turned into JSON text.
pub const STATUS_NOT_SERIALIZABLE: i32 = 3;
/// The JavaScript heap limit was hit.
pub const STATUS_OUT_OF_MEMORY: i32 = 4;
/// Compiling or running the plugin's top level failed; the output is the message.
pub const STATUS_INIT_FAILED: i32 = 5;
/// The source is not a function body (it closes the wrapper early); the output says so.
pub const STATUS_NOT_A_FUNCTION_BODY: i32 = 6;
/// Calls out of order: `fp_init` before `fp_prepare`, `fp_dispatch` before `fp_init`, or
/// either of those twice.
pub const STATUS_BAD_STATE: i32 = 7;

/// Capability hardening, the C# Jint host's `SandboxScripts.Hardening` (`@MAX@` is replaced by
/// the string cap). Most of these names never exist in QuickJS; they are pinned to `undefined`
/// so a plugin can't shim them into something that looks trusted.
const HARDENING: &str = r#"(function harden() {
  const kill = (obj, name) => {
    try {
      Object.defineProperty(obj, name, { value: undefined, writable: false, configurable: false });
    } catch (e) {
      try { obj[name] = undefined } catch (e2) { /* not reachable in this scope */ }
    }
  };
  [
    'fetch', 'XMLHttpRequest', 'WebSocket', 'importScripts', 'Worker',
    'SharedWorker', 'EventSource', 'indexedDB', 'caches', 'Request',
    'Response', 'Headers', 'BroadcastChannel', 'WebTransport',
    'RTCPeerConnection', 'FileSystemHandle', 'navigator', 'self', 'postMessage',
    'importNamespace', 'System', 'clr', 'require', 'process', 'host',
    'ArrayBuffer', 'SharedArrayBuffer', 'DataView', 'Atomics', 'WeakRef', 'FinalizationRegistry',
    'Int8Array', 'Uint8Array', 'Uint8ClampedArray', 'Int16Array', 'Uint16Array', 'Int32Array',
    'Uint32Array', 'Float16Array', 'Float32Array', 'Float64Array', 'BigInt64Array', 'BigUint64Array',
    'Proxy', 'performance',
  ].forEach(name => kill(globalThis, name));
  kill(Object, 'setPrototypeOf');
  if (typeof Reflect === 'object') kill(Reflect, 'setPrototypeOf');
  try { delete Object.prototype.__proto__; } catch (e) { /* already gone */ }

  const MAX = @MAX@;
  const cap = (proto, name, lengthOf) => {
    const original = proto[name];
    if (typeof original !== 'function') return;
    Object.defineProperty(proto, name, {
      value: function (...args) {
        if (lengthOf(this, args) > MAX) throw new RangeError('String result too long for the plugin sandbox');
        return original.apply(this, args);
      },
      writable: false,
      configurable: false,
    });
  };
  cap(String.prototype, 'repeat', (s, args) => String(s).length * Math.floor(Number(args[0])));
  cap(String.prototype, 'padStart', (s, args) => Number(args[0]));
  cap(String.prototype, 'padEnd', (s, args) => Number(args[0]));

  const noop = function () {};
  Object.defineProperty(globalThis, 'console', {
    value: Object.freeze({ log: noop, info: noop, warn: noop, error: noop, debug: noop }),
    writable: false,
    configurable: false,
  });
})();"#;

/// Evaluates to `bootstrap(granted, factory) → dispatch(hook, payloadJson)`, the C# host's
/// `SandboxScripts.Bootstrap` (minus its JIT warm-up line). The JSON and object helpers are
/// captured when this script runs, before any plugin code; grants are checked in a
/// null-prototype map the plugin can't reach. `dispatch` returns `undefined` when no handler is
/// registered, else the JSON of the handler's array result (`"[]"` for anything else).
const BOOTSTRAP: &str = r#"(function () {
  const parse = JSON.parse, stringify = JSON.stringify, isArray = Array.isArray;
  const create = Object.create, freeze = Object.freeze;
  return function bootstrap(grantedJson, factory) {
    const granted = create(null);
    const list = parse(grantedJson);
    for (let i = 0; i < list.length; i++) granted[list[i]] = true;
    const handlers = create(null);
    const api = freeze({
      on: function on(hook, fn) {
        if (typeof hook === 'string' && granted[hook] === true) handlers[hook] = fn;
      },
    });
    factory(api);
    return function dispatch(hook, payloadJson) {
      const handler = handlers[hook];
      if (handler === undefined) return undefined;
      const result = handler(parse(payloadJson));
      return isArray(result) ? stringify(result) : '[]';
    };
  };
})()"#;

const NOT_A_FUNCTION_BODY: &str = "plugin source must be a function body (unbalanced braces?)";

// ---------------------------------------------------------------------------------------------
// Heap accounting. QuickJS allocates through these functions (JS_NewRuntime2). The limit is
// ours rather than JS_SetMemoryLimit's so that we see the refusal and can make it stick.

static HEAP_LIMIT: AtomicUsize = AtomicUsize::new(usize::MAX);
static HEAP_USED: AtomicUsize = AtomicUsize::new(0);
static OUT_OF_MEMORY: AtomicBool = AtomicBool::new(false);

extern "C" {
    fn malloc(size: usize) -> *mut c_void;
    fn calloc(count: usize, size: usize) -> *mut c_void;
    fn realloc(ptr: *mut c_void, size: usize) -> *mut c_void;
    fn free(ptr: *mut c_void);
    fn malloc_usable_size(ptr: *const c_void) -> usize;
}

/// Whether `size` more bytes fit under the limit; records a refusal. Once the limit was hit,
/// every allocation fails until the host discards the instance, so the plugin cannot catch the
/// error and carry on.
fn within_limit(size: usize) -> bool {
    if OUT_OF_MEMORY.load(Ordering::Relaxed) {
        return false;
    }
    let used = HEAP_USED.load(Ordering::Relaxed);
    if used.saturating_add(size) > HEAP_LIMIT.load(Ordering::Relaxed) {
        OUT_OF_MEMORY.store(true, Ordering::Relaxed);
        return false;
    }
    true
}

fn usable(ptr: *const c_void) -> usize {
    if ptr.is_null() {
        return 0;
    }
    // SAFETY: `ptr` came from libc's allocator and has not been freed.
    unsafe { malloc_usable_size(ptr) }
}

/// Count a fresh block, or record that libc itself ran out (the host's cap on linear memory).
fn allocated(ptr: *mut c_void) -> *mut c_void {
    if ptr.is_null() {
        OUT_OF_MEMORY.store(true, Ordering::Relaxed);
    } else {
        HEAP_USED.fetch_add(usable(ptr), Ordering::Relaxed);
    }
    ptr
}

unsafe extern "C" fn heap_calloc(_opaque: *mut c_void, count: q::size_t, size: q::size_t) -> *mut c_void {
    let (count, size) = (count as usize, size as usize);
    let Some(total) = count.checked_mul(size) else { return std::ptr::null_mut() };
    if !within_limit(total) {
        return std::ptr::null_mut();
    }
    // SAFETY: plain libc allocation.
    allocated(unsafe { calloc(count, size) })
}

unsafe extern "C" fn heap_malloc(_opaque: *mut c_void, size: q::size_t) -> *mut c_void {
    let size = size as usize;
    if !within_limit(size) {
        return std::ptr::null_mut();
    }
    // SAFETY: plain libc allocation.
    allocated(unsafe { malloc(size) })
}

unsafe extern "C" fn heap_free(_opaque: *mut c_void, ptr: *mut c_void) {
    HEAP_USED.fetch_sub(usable(ptr), Ordering::Relaxed);
    // SAFETY: QuickJS frees only blocks it got from these functions.
    unsafe { free(ptr) }
}

unsafe extern "C" fn heap_realloc(_opaque: *mut c_void, ptr: *mut c_void, size: q::size_t) -> *mut c_void {
    let size = size as usize;
    let old = usable(ptr);
    if size > old && !within_limit(size - old) {
        return std::ptr::null_mut();
    }
    // SAFETY: `ptr` is null or a live block from these functions.
    let moved = unsafe { realloc(ptr, size) };
    if moved.is_null() && size != 0 {
        OUT_OF_MEMORY.store(true, Ordering::Relaxed);
        return moved; // the old block is untouched
    }
    HEAP_USED.fetch_sub(old, Ordering::Relaxed);
    HEAP_USED.fetch_add(usable(moved), Ordering::Relaxed);
    moved
}

unsafe extern "C" fn heap_usable_size(ptr: *const c_void) -> q::size_t {
    usable(ptr) as q::size_t
}

static HEAP: q::JSMallocFunctions = q::JSMallocFunctions {
    js_calloc: Some(heap_calloc),
    js_malloc: Some(heap_malloc),
    js_free: Some(heap_free),
    js_realloc: Some(heap_realloc),
    js_malloc_usable_size: Some(heap_usable_size),
};

/// Once the heap limit was hit, interrupt (uncatchably) at every check until the call ends.
unsafe extern "C" fn interrupt(_rt: *mut q::JSRuntime, _opaque: *mut c_void) -> c_int {
    c_int::from(OUT_OF_MEMORY.load(Ordering::Relaxed))
}

// ---------------------------------------------------------------------------------------------
// Values.

/// An owned `JSValue`, freed on drop.
struct Value {
    ctx: *mut q::JSContext,
    raw: q::JSValue,
}

impl Value {
    fn new(ctx: *mut q::JSContext, raw: q::JSValue) -> Self {
        Self { ctx, raw }
    }

    fn is_exception(&self) -> bool {
        // SAFETY: tag inspection only.
        unsafe { q::JS_IsException(self.raw) }
    }

    /// Give up ownership (the value moves into a QuickJS call that consumes it, or into
    /// long-lived state).
    fn into_raw(self) -> q::JSValue {
        let raw = self.raw;
        std::mem::forget(self);
        raw
    }
}

impl Drop for Value {
    fn drop(&mut self) {
        // SAFETY: we own one reference to `raw` in `ctx`.
        unsafe { q::JS_FreeValue(self.ctx, self.raw) }
    }
}

/// A JS string from UTF-8 text.
fn new_string(ctx: *mut q::JSContext, text: &str) -> Value {
    // SAFETY: `text` is valid for `len` bytes; QuickJS copies it.
    Value::new(ctx, unsafe { q::JS_NewStringLen(ctx, text.as_ptr().cast::<c_char>(), text.len() as q::size_t) })
}

/// Drop the pending exception, if any.
fn clear_exception(ctx: *mut q::JSContext) {
    // SAFETY: fetching and freeing the pending exception.
    unsafe { q::JS_FreeValue(ctx, q::JS_GetException(ctx)) }
}

/// `String(value)` as Rust text (lossy for lone surrogates). `None` if the conversion threw.
fn to_text(ctx: *mut q::JSContext, value: q::JSValue) -> Option<String> {
    let mut len: usize = 0;
    // SAFETY: `value` is live in `ctx`; the returned C string is released below.
    let ptr = unsafe { q::JS_ToCStringLen(ctx, &mut len, value) };
    if ptr.is_null() {
        clear_exception(ctx);
        return None;
    }
    // SAFETY: QuickJS returned `len` readable bytes at `ptr`.
    let bytes = unsafe { std::slice::from_raw_parts(ptr.cast::<u8>(), len) };
    let text = String::from_utf8_lossy(bytes).into_owned();
    // SAFETY: `ptr` came from JS_ToCStringLen in `ctx`.
    unsafe { q::JS_FreeCString(ctx, ptr) };
    Some(text)
}

/// Take the pending exception and describe it the way the C# host does: an Error's `message`,
/// otherwise `String(value)`.
fn take_exception_message(ctx: *mut q::JSContext) -> String {
    // SAFETY: takes ownership of the pending exception.
    let exception = Value::new(ctx, unsafe { q::JS_GetException(ctx) });
    // SAFETY: class inspection only.
    if unsafe { q::JS_IsError(exception.raw) } {
        // SAFETY: property read on a live object; the key is NUL-terminated.
        let message = Value::new(ctx, unsafe { q::JS_GetPropertyStr(ctx, exception.raw, c"message".as_ptr()) });
        if message.is_exception() {
            clear_exception(ctx);
        // SAFETY: tag inspection only.
        } else if unsafe { q::JS_IsString(message.raw) } {
            if let Some(text) = to_text(ctx, message.raw) {
                return text;
            }
        }
    }
    to_text(ctx, exception.raw).unwrap_or_else(|| "(exception could not be converted to a string)".to_owned())
}

// ---------------------------------------------------------------------------------------------
// Compiling without an Eval intrinsic in the plugin's realm.

/// A failed step: a status plus its message.
struct Failure {
    status: i32,
    message: String,
}

impl Failure {
    fn new(status: i32, message: &str) -> Self {
        Self { status, message: message.to_owned() }
    }

    /// The pending exception of `ctx` as an init failure (or out of memory).
    fn from_exception(ctx: *mut q::JSContext) -> Self {
        let message = take_exception_message(ctx);
        if OUT_OF_MEMORY.load(Ordering::Relaxed) {
            return Self::new(STATUS_OUT_OF_MEMORY, "out of memory");
        }
        Self { status: STATUS_INIT_FAILED, message }
    }
}

#[derive(Clone, Copy)]
struct Contexts {
    compiler: *mut q::JSContext,
    sandbox: *mut q::JSContext,
}

impl Contexts {
    /// Compile `source` as a strict global script in the compiler context (bytecode only).
    fn compile(&self, source: &str) -> Result<Value, Failure> {
        let mut text = Vec::with_capacity(source.len() + 1);
        text.extend_from_slice(source.as_bytes());
        text.push(0); // JS_Eval wants a NUL after the input
        let flags = (q::JS_EVAL_TYPE_GLOBAL | q::JS_EVAL_FLAG_STRICT | q::JS_EVAL_FLAG_COMPILE_ONLY) as c_int;
        // SAFETY: `text` is NUL-terminated and `source.len()` bytes long before the NUL.
        let bytecode = Value::new(self.compiler, unsafe {
            q::JS_Eval(
                self.compiler,
                text.as_ptr().cast::<c_char>(),
                source.len() as q::size_t,
                c"plugin.js".as_ptr(),
                flags,
            )
        });
        if bytecode.is_exception() {
            return Err(Failure::from_exception(self.compiler));
        }
        Ok(bytecode)
    }

    /// Move compiled bytecode into the sandbox realm and run it; returns the completion value.
    fn load(&self, bytecode: Value) -> Result<Value, Failure> {
        let mut size: q::size_t = 0;
        // SAFETY: `bytecode` is a live function-bytecode value in the compiler context.
        let bytes =
            unsafe { q::JS_WriteObject(self.compiler, &mut size, bytecode.raw, q::JS_WRITE_OBJ_BYTECODE as c_int) };
        drop(bytecode);
        if bytes.is_null() {
            return Err(Failure::from_exception(self.compiler));
        }
        // SAFETY: `bytes` holds `size` bytes we just wrote; reading binds the functions to the
        // sandbox realm. The buffer is freed right after.
        let function = unsafe {
            let function = q::JS_ReadObject(self.sandbox, bytes, size, q::JS_READ_OBJ_BYTECODE as c_int);
            q::js_free(self.compiler, bytes.cast::<c_void>());
            Value::new(self.sandbox, function)
        };
        if function.is_exception() {
            return Err(Failure::from_exception(self.sandbox));
        }
        // SAFETY: JS_EvalFunction consumes the function value.
        let result = Value::new(self.sandbox, unsafe { q::JS_EvalFunction(self.sandbox, function.into_raw()) });
        if result.is_exception() {
            return Err(Failure::from_exception(self.sandbox));
        }
        Ok(result)
    }

    /// Compile `source` and run it in the sandbox context.
    fn run(&self, source: &str) -> Result<Value, Failure> {
        self.load(self.compile(source)?)
    }
}

// ---------------------------------------------------------------------------------------------
// Instance state and exports.

/// What `fp_prepare` builds: the runtime's two contexts and the bootstrap function (owned).
#[derive(Clone, Copy)]
struct Prepared {
    contexts: Contexts,
    bootstrap: q::JSValue,
}

/// A plugin ready for `fp_dispatch`.
#[derive(Clone, Copy)]
struct Guest {
    ctx: *mut q::JSContext,
    dispatch: q::JSValue,
}

/// How far the instance got: nothing, `fp_prepare` called, `fp_init` called.
#[derive(Clone, Copy, PartialEq, Eq)]
enum Stage {
    Fresh,
    Prepared,
    Initialized,
}

thread_local! {
    static IO: RefCell<Vec<u8>> = const { RefCell::new(Vec::new()) };
    static OUTPUT: RefCell<Vec<u8>> = const { RefCell::new(Vec::new()) };
    static STAGE: Cell<Stage> = const { Cell::new(Stage::Fresh) };
    static PREPARED: Cell<Option<Prepared>> = const { Cell::new(None) };
    static GUEST: Cell<Option<Guest>> = const { Cell::new(None) };
}

fn set_output(text: &str) {
    OUTPUT.with(|out| {
        let mut out = out.borrow_mut();
        out.clear();
        out.extend_from_slice(text.as_bytes());
    });
}

/// The I/O buffer split after `first` bytes into two UTF-8 strings of `first` and `second` bytes.
fn read_inputs(first: usize, second: usize) -> Option<(String, String)> {
    IO.with(|io| {
        let io = io.borrow();
        let a = io.get(..first)?;
        let b = io.get(first..first.checked_add(second)?)?;
        Some((std::str::from_utf8(a).ok()?.to_owned(), std::str::from_utf8(b).ok()?.to_owned()))
    })
}

fn report(status: i32, output: &str) -> i32 {
    set_output(output);
    status
}

/// Resize the I/O buffer to `len` bytes and return its address for the host to fill.
#[no_mangle]
pub extern "C" fn fp_buffer(len: usize) -> *mut u8 {
    IO.with(|io| {
        let mut io = io.borrow_mut();
        io.clear();
        io.resize(len, 0);
        io.as_mut_ptr()
    })
}

/// Address of the last call's output text.
#[no_mangle]
pub extern "C" fn fp_output_ptr() -> *const u8 {
    OUTPUT.with(|out| out.borrow().as_ptr())
}

/// Length of the last call's output text.
#[no_mangle]
pub extern "C" fn fp_output_len() -> usize {
    OUTPUT.with(|out| out.borrow().len())
}

/// Build the part of the sandbox every plugin shares: the QuickJS runtime with the heap limit,
/// the compiler and sandbox contexts, the hardening and the bootstrap. The host calls this
/// once, keeps the resulting linear memory as an image, and starts every plugin's instance
/// from a copy of it.
#[no_mangle]
pub extern "C" fn fp_prepare(heap_limit: usize, max_string_len: usize) -> i32 {
    if STAGE.replace(Stage::Prepared) != Stage::Fresh {
        return report(STATUS_BAD_STATE, "already prepared");
    }
    HEAP_LIMIT.store(heap_limit, Ordering::Relaxed);
    match prepare(max_string_len) {
        Ok(prepared) => {
            PREPARED.set(Some(prepared));
            report(STATUS_OK, "")
        }
        Err(failure) => report(failure.status, &failure.message),
    }
}

/// Load one plugin into a prepared instance. Inputs in the I/O buffer: the granted-hooks JSON
/// array (`granted_len` bytes), then the plugin source (`source_len` bytes).
#[no_mangle]
pub extern "C" fn fp_init(granted_len: usize, source_len: usize) -> i32 {
    match STAGE.get() {
        Stage::Fresh => return report(STATUS_BAD_STATE, "the sandbox is not prepared"),
        Stage::Initialized => return report(STATUS_BAD_STATE, "already initialized"),
        Stage::Prepared => STAGE.set(Stage::Initialized),
    }
    let Some(prepared) = PREPARED.take() else {
        return report(STATUS_BAD_STATE, "the sandbox failed to prepare");
    };
    let Some((granted, source)) = read_inputs(granted_len, source_len) else {
        return report(STATUS_INIT_FAILED, "init input is not UTF-8");
    };
    match init(prepared, &granted, &source) {
        Ok(guest) => {
            GUEST.set(Some(guest));
            report(STATUS_OK, "")
        }
        Err(failure) => report(failure.status, &failure.message),
    }
}

fn oom() -> Failure {
    Failure::new(STATUS_OUT_OF_MEMORY, "out of memory")
}

fn prepare(max_string_len: usize) -> Result<Prepared, Failure> {
    // SAFETY: plain QuickJS setup; every pointer is checked before use, and the runtime and
    // sandbox context stay alive for the rest of the instance.
    let contexts = unsafe {
        let rt = q::JS_NewRuntime2(&HEAP, std::ptr::null_mut());
        if rt.is_null() {
            return Err(oom());
        }
        q::JS_SetInterruptHandler(rt, Some(interrupt), std::ptr::null_mut());
        let compiler = q::JS_NewContextRaw(rt);
        let sandbox = q::JS_NewContextRaw(rt);
        if compiler.is_null() || sandbox.is_null() {
            return Err(oom());
        }
        // The compiler context can compile; the sandbox context cannot (no Eval intrinsic).
        let compiler_ok = q::JS_AddIntrinsicBaseObjects(compiler) == 0
            && q::JS_AddIntrinsicEval(compiler) == 0
            && q::JS_AddIntrinsicRegExp(compiler) == 0;
        let sandbox_ok = q::JS_AddIntrinsicBaseObjects(sandbox) == 0
            && q::JS_AddIntrinsicDate(sandbox) == 0
            && q::JS_AddIntrinsicRegExp(sandbox) == 0
            && q::JS_AddIntrinsicJSON(sandbox) == 0
            && q::JS_AddIntrinsicMapSet(sandbox) == 0
            && q::JS_AddIntrinsicPromise(sandbox) == 0;
        if !compiler_ok || !sandbox_ok {
            return Err(oom());
        }
        Contexts { compiler, sandbox }
    };

    drop(contexts.run(&HARDENING.replace("@MAX@", &max_string_len.to_string()))?);
    let bootstrap = contexts.run(BOOTSTRAP)?;
    Ok(Prepared { contexts, bootstrap: bootstrap.into_raw() })
}

fn init(prepared: Prepared, granted: &str, source: &str) -> Result<Guest, Failure> {
    let contexts = prepared.contexts;
    let bootstrap = Value::new(contexts.sandbox, prepared.bootstrap);

    // The plugin source is the body of `function (api) { … }`. It is parsed a second time
    // inside an array literal inside another function: the token stream of the source is the
    // same in both, so a source that closes the wrapper early to reach the top level ends up
    // closing `(` against `[` in the second form, a syntax error. (After an early `}` both forms
    // continue in the same expression context, so a `/` can't read as a regular expression in
    // one and as division in the other, which a block-statement form would allow.) The wrapper
    // only runs when both parse, and it has to evaluate to a function.
    let factory = contexts.compile(&format!("(function (api) {{\n{source}\n}})"))?;
    if contexts.compile(&format!("(function () {{ [function (api) {{\n{source}\n}}] }})")).is_err() {
        return Err(Failure::new(STATUS_NOT_A_FUNCTION_BODY, NOT_A_FUNCTION_BODY));
    }
    let factory = contexts.load(factory)?;
    // SAFETY: type check on a live value.
    if !unsafe { q::JS_IsFunction(contexts.sandbox, factory.raw) } {
        return Err(Failure::new(STATUS_NOT_A_FUNCTION_BODY, NOT_A_FUNCTION_BODY));
    }
    // SAFETY: nothing compiled in the compiler context is still referenced (only bytecode
    // copies read into the sandbox), so it can go before plugin code runs.
    unsafe { q::JS_FreeContext(contexts.compiler) };

    let ctx = contexts.sandbox;
    let granted = new_string(ctx, granted);
    let mut args = [granted.raw, factory.raw];
    // SAFETY: `bootstrap` is a function; the arguments stay owned by their guards.
    let dispatch = Value::new(ctx, unsafe { q::JS_Call(ctx, bootstrap.raw, q::JS_UNDEFINED, 2, args.as_mut_ptr()) });
    if dispatch.is_exception() {
        return Err(Failure::from_exception(ctx));
    }
    if OUT_OF_MEMORY.load(Ordering::Relaxed) {
        return Err(oom());
    }
    Ok(Guest { ctx, dispatch: dispatch.into_raw() })
}

/// Call the plugin's handler. Inputs in the I/O buffer: the hook name (`hook_len` bytes), then
/// the payload JSON (`payload_len` bytes).
#[no_mangle]
pub extern "C" fn fp_dispatch(hook_len: usize, payload_len: usize) -> i32 {
    let Some(Guest { ctx, dispatch }) = GUEST.get() else {
        return report(STATUS_BAD_STATE, "the plugin is not initialized");
    };
    let Some((hook, payload)) = read_inputs(hook_len, payload_len) else {
        return report(STATUS_THREW, "dispatch input is not UTF-8");
    };
    let hook = new_string(ctx, &hook);
    let payload = new_string(ctx, &payload);
    let mut args = [hook.raw, payload.raw];
    // SAFETY: `dispatch` is the live dispatcher function; the arguments stay owned by their guards.
    let result = Value::new(ctx, unsafe { q::JS_Call(ctx, dispatch, q::JS_UNDEFINED, 2, args.as_mut_ptr()) });
    // A call that hit the heap limit is an overrun however it ended.
    if OUT_OF_MEMORY.load(Ordering::Relaxed) {
        if result.is_exception() {
            clear_exception(ctx);
        }
        return report(STATUS_OUT_OF_MEMORY, "out of memory");
    }
    if result.is_exception() {
        return report(STATUS_THREW, &take_exception_message(ctx));
    }
    // SAFETY: tag inspection only.
    if unsafe { q::JS_IsUndefined(result.raw) } {
        return report(STATUS_NO_HANDLER, "");
    }
    // SAFETY: tag inspection only.
    if !unsafe { q::JS_IsString(result.raw) } {
        return report(STATUS_NOT_SERIALIZABLE, "handler result could not be serialized");
    }
    match to_text(ctx, result.raw) {
        Some(json) => report(STATUS_OK, &json),
        None => report(STATUS_NOT_SERIALIZABLE, "handler result could not be serialized"),
    }
}
