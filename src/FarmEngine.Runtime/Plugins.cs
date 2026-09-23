using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using FarmEngine.Core;
using FarmEngine.Json;
using FarmEngine.Schemas;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Engine = Jint.Engine;

namespace FarmEngine.Runtime;

// Port of plugins.ts.
//
// Sandboxed plugin runtime (M5). A plugin is a script from a content pack
// that registers hook handlers via `api.on(hook, fn)`. Handlers receive a
// JSON (structured-clone-safe) payload and return an array of mutations —
// they never touch game state, the tick loop or save internals. Mutations
// are validated (PluginMutationSchema) and flow through the engine's command
// pipeline, so replays stay deterministic with mods enabled.
//
// The browser version runs one Web Worker per plugin. Here JintPluginHost runs
// one Jint engine per plugin — no CLR interop, strict mode, string
// compilation (eval / Function) disabled, per-call time / statement / memory /
// recursion / native-stack budgets — on a dedicated large-stack thread.

/// <summary>A plugin ready to load (TS <c>PluginSpec</c>).</summary>
/// <param name="Id"><c>packId:pluginId</c> — globally unique.</param>
/// <param name="GrantedHooks">Hooks the plugin may receive = plugin.hooks ∩ manifest permissions.hooks.</param>
public sealed record PluginSpec(string Id, string PackId, string Source, List<string> GrantedHooks);

/// <summary><see cref="PluginError.Kind"/> values.</summary>
public static class PluginErrorKinds
{
    /// <summary>The plugin failed to compile or initialize — disabled for the session.</summary>
    public const string Init = "init";
    /// <summary>A handler threw (or returned something that is not JSON-serializable).</summary>
    public const string Threw = "threw";
    /// <summary>A handler exceeded its time budget (counts as a strike).</summary>
    public const string Timeout = "timeout";
    /// <summary>A handler exceeded its statement / memory / recursion / stack budget (counts as a strike).</summary>
    public const string Budget = "budget";
    /// <summary>A returned mutation failed validation and was dropped.</summary>
    public const string InvalidMutation = "invalidMutation";
    /// <summary>The plugin hit its strike limit and was disabled for the session.</summary>
    public const string Disabled = "disabled";
}

/// <summary>A per-plugin failure. Errors are isolated: they never reach the simulation or other plugins.</summary>
/// <param name="Kind">One of <see cref="PluginErrorKinds"/>.</param>
/// <param name="Hook">The hook being dispatched (null for init errors).</param>
public sealed record PluginError(string PluginId, string Kind, string Message, string? Hook = null);

/// <summary>
/// One plugin's answer to a dispatch (TS <c>PluginDispatchResult</c>, plus
/// <see cref="Errors"/>). Hosts return a result for every plugin that produced
/// mutations or errors, in spec order.
/// </summary>
public sealed record PluginDispatchResult(string PluginId, List<PluginMutation> Mutations, List<PluginError>? Errors = null)
{
    public List<PluginError> Errors { get; init; } = Errors ?? [];
}

/// <summary>TS <c>PluginHostLike</c>.</summary>
public interface IPluginHost : IDisposable
{
    /// <summary>
    /// Dispatch a hook to every plugin granted it; returns validated mutations
    /// (and isolated errors). Synchronous: results are ready when it returns.
    /// </summary>
    /// <param name="payload">Any JSON-serializable value (hook payload records, <see cref="JsonElement"/>, …).</param>
    List<PluginDispatchResult> Dispatch(string hook, object? payload);

    /// <summary>Plugins that failed to initialize (they are disabled).</summary>
    IReadOnlyList<PluginError> InitErrors { get; }
}

/// <summary>A drained mutation with the plugin that produced it (TS <c>QueuedPluginMutation</c>).</summary>
public sealed record QueuedPluginMutation(string PluginId, PluginMutation Mutation)
{
    /// <summary>The command that replays this mutation through the engine.</summary>
    public PluginMutationCommand ToCommand() => new(PluginId, Mutation);
}

/// <summary>
/// Ordering buffer between plugin dispatch and the simulation (TS
/// <c>PluginMutationQueue</c>).
///
/// Dispatch happens inside hook emission — in the middle of a reducer step.
/// Applying results the instant they arrive would put them at arbitrary
/// positions in the command stream. Hosts enqueue results as they resolve and
/// drain the queue at ONE fixed point in the frame (before the frame's
/// commands/ticks), so mutations enter the command log at a well-defined
/// position in arrival order. Replaying that command log is then fully
/// deterministic — the log, not live plugin timing, is the replay artifact.
/// Thread-safe.
/// </summary>
public sealed class PluginMutationQueue
{
    private readonly object _gate = new();
    private List<QueuedPluginMutation> _queue = [];

    public void Enqueue(IEnumerable<PluginDispatchResult> results)
    {
        lock (_gate)
        {
            foreach (var result in results)
            {
                foreach (var mutation in result.Mutations) _queue.Add(new QueuedPluginMutation(result.PluginId, mutation));
            }
        }
    }

    /// <summary>Remove and return everything queued, in arrival order.</summary>
    public List<QueuedPluginMutation> Drain()
    {
        lock (_gate)
        {
            if (_queue.Count == 0) return [];
            var drained = _queue;
            _queue = [];
            return drained;
        }
    }

    public int Size
    {
        get
        {
            lock (_gate) return _queue.Count;
        }
    }
}

/// <summary>Spec extraction, mutation validation and host creation.</summary>
public static class Plugins
{
    /// <summary>Extract plugin specs (with capability grants applied) from enabled packs.</summary>
    public static List<PluginSpec> PluginSpecsFromPacks(IEnumerable<ContentPack> packs)
    {
        var specs = new List<PluginSpec>();
        foreach (var pack in packs)
        {
            var granted = new HashSet<string>(pack.Manifest.Permissions.Hooks ?? [], StringComparer.Ordinal);
            foreach (var plugin in pack.Plugins ?? [])
            {
                specs.Add(new PluginSpec(
                    $"{pack.Manifest.Id}:{plugin.Id}",
                    pack.Manifest.Id,
                    plugin.Source,
                    (plugin.Hooks ?? []).Where(granted.Contains).ToList()));
            }
        }
        return specs;
    }

    /// <summary>Specs from a project's enabled installed packs, in load order.</summary>
    public static List<PluginSpec> PluginSpecsFromProject(GameProject project) =>
        PluginSpecsFromPacks((project.ContentPacks ?? []).Where(install => install.Enabled).Select(install => install.Pack));

    /// <summary>TS <c>createPluginHost</c>: the sandboxed Jint host (serves browser-worker and in-process roles).</summary>
    public static IPluginHost CreatePluginHost(IReadOnlyList<PluginSpec> specs, JintPluginHostOptions? options = null) =>
        new JintPluginHost(specs, options);

    /// <summary>
    /// Validate a handler's return value (TS <c>validateMutations</c>): a
    /// non-array yields nothing; each entry is parsed against the mutation
    /// schema and invalid ones are dropped with an error entry.
    /// </summary>
    public static (List<PluginMutation> Mutations, List<PluginError> Errors) ValidateMutations(string pluginId, JsonElement raw, string? hook = null)
    {
        var mutations = new List<PluginMutation>();
        var errors = new List<PluginError>();
        if (raw.ValueKind != JsonValueKind.Array) return (mutations, errors);
        var index = 0;
        foreach (var entry in raw.EnumerateArray())
        {
            if (TryParseMutation(entry, out var mutation, out var error)) mutations.Add(mutation);
            else errors.Add(new PluginError(pluginId, PluginErrorKinds.InvalidMutation, $"mutation [{index}] dropped: {error}", hook));
            index++;
        }
        return (mutations, errors);
    }

    /// <summary>
    /// Parse one mutation exactly like zod's <c>PluginMutationSchema.safeParse</c>:
    /// discriminated on <c>type</c>, required fields type-checked, integer and
    /// range rules enforced, unknown keys stripped.
    /// </summary>
    public static bool TryParseMutation(JsonElement entry, out PluginMutation mutation, out string error)
    {
        mutation = null!;
        error = "";
        if (entry.ValueKind != JsonValueKind.Object)
        {
            error = $"expected object, received {Describe(entry)}";
            return false;
        }
        if (!entry.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
        {
            error = "invalid discriminator value (type)";
            return false;
        }
        var failure = new List<string>();
        string Str(string key)
        {
            if (entry.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString()!;
            failure.Add($"{key}: expected string");
            return "";
        }
        string? OptStr(string key)
        {
            if (!entry.TryGetProperty(key, out var v)) return null;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            failure.Add($"{key}: expected string");
            return null;
        }
        double Int(string key, double min, double max)
        {
            if (!entry.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Number)
            {
                failure.Add($"{key}: expected number");
                return 0;
            }
            var n = v.GetDouble();
            if (!Js.IsInteger(n)) failure.Add($"{key}: expected integer");
            else if (n < min) failure.Add($"{key}: must be >= {Js.Num(min)}");
            else if (n > max) failure.Add($"{key}: must be <= {Js.Num(max)}");
            return n;
        }

        PluginMutation? parsed = typeElement.GetString() switch
        {
            "giveItem" => new GiveItemMutation { ItemId = Str("itemId"), Quantity = Int("quantity", 1, 999) },
            "takeItem" => new TakeItemMutation { ItemId = Str("itemId"), Quantity = Int("quantity", 1, 999) },
            "giveMoney" => new GiveMoneyMutation { Amount = Int("amount", 1, 1_000_000) },
            "takeMoney" => new TakeMoneyMutation { Amount = Int("amount", 1, 1_000_000) },
            "setFlag" => new SetFlagMutation { Flag = Str("flag"), Value = FlagValue(entry, failure) },
            "message" => new MessageMutation { Text = MaxLength(Str("text"), 500, "text", failure) },
            "setWeather" => new SetWeatherMutation { WeatherId = Str("weatherId") },
            "modifyFriendship" => new ModifyFriendshipMutation { NpcId = Str("npcId"), Delta = Int("delta", -1000, 1000) },
            "grantXp" => new GrantXpMutation { Skill = Str("skill"), Amount = Int("amount", 1, 10_000) },
            "modifyEnergy" => new ModifyEnergyMutation { Delta = Int("delta", -1000, 1000) },
            "startQuest" => new StartQuestMutation { QuestId = Str("questId") },
            "warpPlayer" => new WarpPlayerMutation { SceneId = Str("sceneId"), X = Int("x", 0, double.PositiveInfinity), Y = Int("y", 0, double.PositiveInfinity) },
            "startDialogue" => new StartDialogueMutation { NpcId = Str("npcId"), DialogueId = OptStr("dialogueId") },
            "playSound" => new PlaySoundMutation { SoundId = Str("soundId") },
            "performAction" => new PerformActionMutation { ActionId = Str("actionId") },
            "startMinigame" => new StartMinigameMutation { MinigameId = Str("minigameId") },
            _ => null,
        };
        if (parsed is null)
        {
            error = $"invalid discriminator value (type '{typeElement.GetString()}')";
            return false;
        }
        if (failure.Count > 0)
        {
            error = $"{typeElement.GetString()}: {string.Join("; ", failure)}";
            return false;
        }
        mutation = parsed;
        return true;
    }

    private static JsonElement FlagValue(JsonElement entry, List<string> failure)
    {
        if (entry.TryGetProperty("value", out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number or JsonValueKind.String)
        {
            return v.Clone();
        }
        failure.Add("value: expected boolean | number | string");
        return default;
    }

    private static string MaxLength(string value, int max, string key, List<string> failure)
    {
        if (value.Length > max) failure.Add($"{key}: at most {max} characters");
        return value;
    }

    private static string Describe(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => "array",
        JsonValueKind.Null => "null",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => element.ValueKind.ToString().ToLowerInvariant(),
    };

    /// <summary>Serialize a hook payload to JSON (camelCase, like the TS payload objects).</summary>
    public static string SerializePayload(object? payload) =>
        payload is null ? "null" : JsonSerializer.Serialize(payload, payload.GetType(), JsonDefaults.Options);
}

/// <summary>Budgets for <see cref="JintPluginHost"/>. Each dispatch call gets a fresh budget.</summary>
public sealed record JintPluginHostOptions
{
    /// <summary>Wall-clock budget per plugin per dispatch (TS worker host: 50 ms).</summary>
    public double TimeoutMs { get; init; } = 50;
    /// <summary>Budget for compiling + initializing a plugin (runs its top-level code).</summary>
    public double InitTimeoutMs { get; init; } = 1000;
    /// <summary>Max statements per call.</summary>
    public int MaxStatements { get; init; } = 2_000_000;
    /// <summary>Max bytes allocated per call.</summary>
    public long MemoryLimitBytes { get; init; } = 16_000_000;
    /// <summary>Max JS call depth.</summary>
    public int RecursionLimit { get; init; } = 512;
    /// <summary>Max JS array length.</summary>
    public uint MaxArraySize { get; init; } = 100_000;
    /// <summary>Max result length of single-call string amplifiers (repeat / padStart / padEnd).</summary>
    public int MaxStringLength { get; init; } = 1_000_000;
    /// <summary>Consecutive budget overruns before a plugin is disabled for the session (TS: 3).</summary>
    public int MaxStrikes { get; init; } = 3;
    /// <summary>
    /// Native stack of the sandbox thread. Native recursion inside the
    /// interpreter (prototype chains, JSON of deep objects, …) is bounded by the
    /// memory budget; a large stack keeps that bound well inside the stack.
    /// </summary>
    public int ThreadStackBytes { get; init; } = 256 * 1024 * 1024;
}

/// <summary>
/// Sandboxed plugin host: one Jint engine per plugin (TS WorkerPluginHost's
/// one-worker-per-plugin), message passing by JSON only, per-call budgets.
///
/// Isolation: no CLR interop (<c>Interop.Enabled = false</c>, no <c>AllowClr</c>);
/// strict mode; <c>eval</c> / <c>Function</c> disabled (string compilation off);
/// the network/storage/worker globals the TS worker removes are defined as
/// <c>undefined</c> (plus raw-memory globals, <c>Proxy</c> and prototype
/// setters); each call is bounded by time, statements, memory, recursion depth
/// and native stack. A handler that throws yields no mutations and an error
/// entry; a handler that exceeds a budget also earns a strike and its engine
/// is rebuilt from source (fresh state, like terminating the worker) — the
/// third consecutive strike disables the plugin for the session.
///
/// All engines live on one dedicated thread, so callers may dispatch from any
/// thread; dispatches are serialized.
/// </summary>
public sealed class JintPluginHost : IPluginHost
{
    private sealed class Entry(PluginSpec spec)
    {
        public PluginSpec Spec { get; } = spec;
        public Engine? Engine { get; set; }
        public JsValue? DispatchFn { get; set; }
        public DeadlineConstraint? Deadline { get; set; }
        public int Strikes { get; set; }
        public bool Disabled { get; set; }
    }

    private readonly JintPluginHostOptions _options;
    private readonly List<Entry> _entries;
    private readonly List<PluginError> _initErrors = [];
    private readonly SandboxThread _thread;
    private readonly object _gate = new();
    private bool _disposed;

    public JintPluginHost(IReadOnlyList<PluginSpec> specs, JintPluginHostOptions? options = null)
    {
        _options = options ?? new JintPluginHostOptions();
        _entries = specs.Select(spec => new Entry(spec)).ToList();
        _thread = new SandboxThread(_options.ThreadStackBytes);
        _thread.Run(() =>
        {
            foreach (var entry in _entries)
            {
                var error = Initialize(entry);
                if (error is not null)
                {
                    entry.Disabled = true;
                    _initErrors.Add(error);
                }
            }
            return 0;
        }, WatchdogFor(_options.InitTimeoutMs * Math.Max(1, _entries.Count)));
    }

    public JintPluginHostOptions Options => _options;

    public IReadOnlyList<PluginError> InitErrors
    {
        get
        {
            lock (_gate) return [.. _initErrors];
        }
    }

    /// <summary>Ids of plugins currently disabled (init failure or strike limit).</summary>
    public IReadOnlyList<string> DisabledPlugins
    {
        get
        {
            lock (_gate) return _entries.Where(e => e.Disabled).Select(e => e.Spec.Id).ToList();
        }
    }

    public List<PluginDispatchResult> Dispatch(string hook, object? payload)
    {
        lock (_gate)
        {
            if (_disposed || _entries.Count == 0) return [];
            string payloadJson;
            try
            {
                payloadJson = Plugins.SerializePayload(payload);
            }
            catch (Exception error)
            {
                return [.. _entries.Where(e => !e.Disabled && e.Spec.GrantedHooks.Contains(hook))
                    .Select(e => new PluginDispatchResult(e.Spec.Id, [], [new PluginError(e.Spec.Id, PluginErrorKinds.Threw, $"payload is not JSON-serializable: {error.Message}", hook)]))];
            }

            var callers = _entries.Count(e => !e.Disabled && e.Spec.GrantedHooks.Contains(hook));
            if (callers == 0) return [];
            try
            {
                return _thread.Run(() =>
                {
                    var results = new List<PluginDispatchResult>();
                    foreach (var entry in _entries)
                    {
                        var result = Call(entry, hook, payloadJson);
                        if (result is not null && (result.Mutations.Count > 0 || result.Errors.Count > 0)) results.Add(result);
                    }
                    return results;
                }, WatchdogFor((_options.TimeoutMs + _options.InitTimeoutMs) * callers));
            }
            catch (TimeoutException)
            {
                // The sandbox thread itself is wedged (should be impossible: every
                // call is budgeted). Fail closed: disable everything.
                foreach (var entry in _entries) entry.Disabled = true;
                _disposed = true;
                return [.. _entries.Select(e => new PluginDispatchResult(e.Spec.Id, [], [new PluginError(e.Spec.Id, PluginErrorKinds.Disabled, "plugin sandbox stopped responding — all plugins disabled", hook)]))];
            }
        }
    }

    private static TimeSpan WatchdogFor(double budgetMs) => TimeSpan.FromMilliseconds(Math.Max(10_000, budgetMs * 10));

    /// <summary>Run one plugin's handler (sandbox thread). Null when the plugin has nothing to say.</summary>
    private PluginDispatchResult? Call(Entry entry, string hook, string payloadJson)
    {
        if (entry.Disabled || entry.Engine is null || entry.DispatchFn is null || !entry.Spec.GrantedHooks.Contains(hook)) return null;
        var id = entry.Spec.Id;
        try
        {
            entry.Deadline!.Budget = TimeSpan.FromMilliseconds(_options.TimeoutMs);
            entry.Engine.Constraints.Reset();
            var value = entry.Engine.Invoke(entry.DispatchFn, hook, payloadJson);
            entry.Strikes = 0;
            if (value.IsUndefined()) return null; // no handler registered for this hook
            if (!value.IsString())
            {
                return new PluginDispatchResult(id, [], [new PluginError(id, PluginErrorKinds.Threw, "handler result could not be serialized", hook)]);
            }
            using var document = JsonDocument.Parse(value.AsString());
            var (mutations, errors) = Plugins.ValidateMutations(id, document.RootElement, hook);
            return new PluginDispatchResult(id, mutations, errors);
        }
        catch (JavaScriptException error)
        {
            // Handler errors yield no mutations (and do not count as strikes).
            entry.Strikes = 0;
            return new PluginDispatchResult(id, [], [new PluginError(id, PluginErrorKinds.Threw, $"threw in {hook}: {error.Message}", hook)]);
        }
        catch (Exception error) when (IsBudgetOverrun(error))
        {
            return Strike(entry, hook, error);
        }
        catch (Exception error)
        {
            // Unknown engine failure: treat like an overrun (engine state is suspect).
            return Strike(entry, hook, error);
        }
    }

    private PluginDispatchResult Strike(Entry entry, string hook, Exception error)
    {
        var id = entry.Spec.Id;
        entry.Strikes += 1;
        var kind = error is TimeoutException ? PluginErrorKinds.Timeout : PluginErrorKinds.Budget;
        var errors = new List<PluginError> { new(id, kind, $"{Describe(error)} in {hook}", hook) };
        // An overrun may leave the engine mid-flight (an infinite loop, a
        // half-built structure): throw it away. Well-behaved-but-slow plugins
        // get a fresh engine until the strike limit disables them.
        entry.Engine = null;
        entry.DispatchFn = null;
        if (entry.Strikes >= _options.MaxStrikes)
        {
            entry.Disabled = true;
            errors.Add(new PluginError(id, PluginErrorKinds.Disabled, $"plugin {id} exceeded its budget {entry.Strikes}× — disabled", hook));
        }
        else
        {
            var initError = Initialize(entry);
            if (initError is not null)
            {
                entry.Disabled = true;
                errors.Add(initError with { Hook = hook });
            }
        }
        return new PluginDispatchResult(id, [], errors);
    }

    private static bool IsBudgetOverrun(Exception error) => error is TimeoutException
        or StatementsCountOverflowException
        or MemoryLimitExceededException
        or RecursionDepthOverflowException
        or InsufficientExecutionStackException
        or OperationCanceledException
        or System.Text.RegularExpressions.RegexMatchTimeoutException
        or OutOfMemoryException;

    private static string Describe(Exception error) => error switch
    {
        System.Text.RegularExpressions.RegexMatchTimeoutException => "exceeded its regular-expression time budget",
        TimeoutException => "exceeded its time budget",
        StatementsCountOverflowException => "exceeded its statement budget",
        MemoryLimitExceededException => "exceeded its memory budget",
        RecursionDepthOverflowException or InsufficientExecutionStackException => "exceeded its recursion budget",
        OutOfMemoryException => "exceeded its memory budget",
        _ => $"failed ({error.GetType().Name}: {error.Message})",
    };

    /// <summary>Build a fresh engine for the plugin and run its source (sandbox thread). Returns an error on failure.</summary>
    private PluginError? Initialize(Entry entry)
    {
        var deadline = new DeadlineConstraint { Budget = TimeSpan.FromMilliseconds(_options.InitTimeoutMs) };
        try
        {
            var engine = new Engine(options =>
            {
                options.Strict = true;
                options.Interop.Enabled = false;
                options.DisableStringCompilation();
                options.Constraints.Constraints.Add(deadline);
                options.MaxStatements(_options.MaxStatements);
                options.LimitMemory(_options.MemoryLimitBytes);
                options.LimitRecursion(_options.RecursionLimit);
                options.Constraints.StackOverflowGuard = true;
                options.Constraints.MaxArraySize = _options.MaxArraySize;
                options.Constraints.RegexTimeout = TimeSpan.FromMilliseconds(Math.Max(1, _options.TimeoutMs));
                options.Constraints.PromiseTimeout = TimeSpan.FromMilliseconds(Math.Max(1, _options.TimeoutMs));
                options.Json.MaxParseDepth = 64;
            });
            engine.Constraints.Reset();
            engine.Execute(SandboxScripts.Hardening(_options.MaxStringLength));
            var factory = SandboxScripts.CompilePlugin(engine, entry.Spec.Source);
            var bootstrap = engine.Evaluate(SandboxScripts.Bootstrap);
            engine.Constraints.Reset();
            var dispatch = engine.Invoke(bootstrap, JsonSerializer.Serialize(entry.Spec.GrantedHooks), factory);
            if (dispatch is not Jint.Native.Function.Function)
            {
                return new PluginError(entry.Spec.Id, PluginErrorKinds.Init, "plugin bootstrap did not return a dispatcher");
            }
            entry.Engine = engine;
            entry.DispatchFn = dispatch;
            entry.Deadline = deadline;
            return null;
        }
        catch (Exception error)
        {
            entry.Engine = null;
            entry.DispatchFn = null;
            var message = error is JavaScriptException or Acornima.ParseErrorException or SandboxException
                ? error.Message
                : Describe(error);
            return new PluginError(entry.Spec.Id, PluginErrorKinds.Init, $"plugin {entry.Spec.Id} failed to initialize — disabled: {message}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed && _entries.Count == 0) return;
            _disposed = true;
            foreach (var entry in _entries)
            {
                entry.Engine = null;
                entry.DispatchFn = null;
                entry.Disabled = true;
            }
            _entries.Clear();
        }
        _thread.Dispose();
    }

    /// <summary>
    /// A wall-clock budget whose length can change between calls (init gets
    /// longer than a dispatch) — Jint's TimeoutInterval with a mutable interval.
    /// </summary>
    private sealed class DeadlineConstraint : Constraint
    {
        private long _deadline = long.MaxValue;

        public TimeSpan Budget { get; set; } = TimeSpan.FromMilliseconds(50);

        public override bool IsAmortizable => true;

        public override void Check()
        {
            if (Stopwatch.GetTimestamp() > _deadline) throw new TimeoutException("The plugin exceeded its time budget.");
        }

        public override void Reset() =>
            _deadline = Stopwatch.GetTimestamp() + (long)(Budget.TotalSeconds * Stopwatch.Frequency);
    }
}

/// <summary>Thrown for plugin sources that do not have the shape of a function body.</summary>
public sealed class SandboxException(string message) : Exception(message);

/// <summary>The JavaScript that frames plugin code inside each engine.</summary>
internal static class SandboxScripts
{
    /// <summary>
    /// Capability hardening, mirroring the TS worker bootstrap: no network, no
    /// storage, no cross-context channels, no nested workers (Jint has none of
    /// these, but the names are pinned to <c>undefined</c> so a plugin can't
    /// shim them into something that looks trusted); plus no raw memory
    /// (ArrayBuffer & co.), no Proxy (native recursion through proxy chains)
    /// and no prototype setters. Single-call string amplifiers are capped so
    /// one call can't allocate past the memory budget. A no-op <c>console</c>
    /// keeps browser-written plugins from throwing.
    /// </summary>
    public static string Hardening(int maxStringLength) => $$"""
        (function harden() {
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
            'Proxy',
          ].forEach(name => kill(globalThis, name));
          kill(Object, 'setPrototypeOf');
          if (typeof Reflect === 'object') kill(Reflect, 'setPrototypeOf');
          try { delete Object.prototype.__proto__; } catch (e) { /* already gone */ }

          const MAX = {{maxStringLength}};
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
        })();
        """;

    /// <summary>
    /// <c>(granted, factory) → dispatch(hook, payloadJson)</c>. JSON helpers are
    /// captured before any plugin code runs; grants are checked in a
    /// null-prototype map the plugin can't reach. <c>dispatch</c> returns
    /// <c>undefined</c> when no handler is registered, else the JSON of the
    /// handler's array result (<c>"[]"</c> for anything that is not an array).
    /// </summary>
    public const string Bootstrap = """
        (function bootstrap(grantedJson, factory) {
          const parse = JSON.parse, stringify = JSON.stringify, isArray = Array.isArray;
          const granted = Object.create(null);
          const list = parse(grantedJson);
          for (let i = 0; i < list.length; i++) granted[list[i]] = true;
          const handlers = Object.create(null);
          const api = Object.freeze({
            on: function on(hook, fn) {
              if (typeof hook === 'string' && granted[hook] === true) handlers[hook] = fn;
            },
          });
          // Warm the JSON paths once under the (longer) init budget.
          stringify(parse('[{"type":"message","text":"warm-up"}]'));
          factory(api);
          return function dispatch(hook, payloadJson) {
            const handler = handlers[hook];
            if (handler === undefined) return undefined;
            const result = handler(parse(payloadJson));
            return isArray(result) ? stringify(result) : '[]';
          };
        })
        """;

    /// <summary>
    /// Compile plugin source as the body of <c>function (api) { … }</c> (TS:
    /// <c>new Function('api', source)</c>, which is disabled in the sandbox). The
    /// wrapped text must parse as exactly one function expression, so a source
    /// can't close the wrapper early and smuggle top-level statements.
    /// </summary>
    public static JsValue CompilePlugin(Engine engine, string source)
    {
        var wrapped = $"(function (api) {{\n{source}\n}})";
        var script = new Acornima.Parser().ParseScript(wrapped, "plugin.js", strict: true);
        if (script.Body.Count != 1
            || script.Body[0] is not Acornima.Ast.ExpressionStatement { Expression: Acornima.Ast.FunctionExpression })
        {
            throw new SandboxException("plugin source must be a function body (unbalanced braces?)");
        }
        return engine.Evaluate(wrapped);
    }
}

/// <summary>
/// One dedicated large-stack thread that runs every engine call of a host.
/// Jint engines are single-threaded; running them on their own thread also
/// gives native recursion inside the interpreter a known, generous stack.
/// </summary>
internal sealed class SandboxThread : IDisposable
{
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;

    public SandboxThread(int stackBytes)
    {
        _thread = new Thread(Loop, stackBytes) { IsBackground = true, Name = "farm-plugin-sandbox" };
        _thread.Start();
    }

    private void Loop()
    {
        try
        {
            foreach (var action in _work.GetConsumingEnumerable()) action();
        }
        catch (ObjectDisposedException)
        {
            // disposed while waiting
        }
    }

    /// <summary>Run <paramref name="work"/> on the sandbox thread and wait for it (watchdog-bounded).</summary>
    public T Run<T>(Func<T> work, TimeSpan watchdog)
    {
        if (Thread.CurrentThread == _thread) return work();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception error)
            {
                completion.SetException(error);
            }
        });
        if (!completion.Task.Wait(watchdog)) throw new TimeoutException("plugin sandbox thread did not respond");
        return completion.Task.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _work.CompleteAdding();
    }
}

/// <summary>An engine context wired to a project's enabled plugins.</summary>
/// <param name="Plugins">Null when no enabled pack ships plugins.</param>
public sealed record PluginEngineContext(EngineContext Ctx, PluginBridge? Plugins);

/// <summary>
/// Wires a plugin host to a Core <see cref="HookBus"/> the way the web app and
/// the exported game shell do: every hook some plugin is granted gets a
/// listener that dispatches to the host; results are QUEUED, not applied, and
/// the host loop drains them at one fixed point per frame (before the frame's
/// commands/ticks) as <see cref="PluginMutationCommand"/>s — so plugin
/// mutations re-enter the command log at a well-defined position and replays
/// stay deterministic.
/// </summary>
public sealed class PluginBridge : IDisposable
{
    private readonly List<Action> _unsubscribe = [];
    private readonly bool _ownsHost;
    private readonly object _gate = new();
    private readonly List<PluginError> _errors = [];

    public PluginBridge(HookBus hooks, IPluginHost host, IEnumerable<PluginSpec> specs, bool ownsHost = true)
    {
        Host = host;
        _ownsHost = ownsHost;
        // TS: [...new Set(specs.flatMap(spec => spec.grantedHooks))] — first-appearance order.
        var hookNames = new List<string>();
        foreach (var hook in specs.SelectMany(spec => spec.GrantedHooks))
        {
            if (!hookNames.Contains(hook)) hookNames.Add(hook);
        }
        HookNames = hookNames;
        foreach (var hook in hookNames)
        {
            _unsubscribe.Add(hooks.On(hook, payload =>
            {
                var results = Host.Dispatch(hook, payload);
                Queue.Enqueue(results);
                Report(results.SelectMany(result => result.Errors));
                return null;
            }));
        }
        Report(host.InitErrors);
    }

    public IPluginHost Host { get; }

    /// <summary>Hooks this bridge listens to.</summary>
    public IReadOnlyList<string> HookNames { get; }

    public PluginMutationQueue Queue { get; } = new();

    /// <summary>Raised for every plugin error (init failures, throws, overruns, dropped mutations).</summary>
    public event Action<PluginError>? ErrorReported;

    /// <summary>The most recent errors (bounded), oldest first.</summary>
    public IReadOnlyList<PluginError> RecentErrors
    {
        get
        {
            lock (_gate) return [.. _errors];
        }
    }

    private void Report(IEnumerable<PluginError> errors)
    {
        foreach (var error in errors)
        {
            lock (_gate)
            {
                _errors.Add(error);
                if (_errors.Count > 100) _errors.RemoveAt(0);
            }
            Debug.WriteLine($"plugin {error.PluginId}: [{error.Kind}] {error.Message}");
            ErrorReported?.Invoke(error);
        }
    }

    /// <summary>
    /// Drain queued mutations as commands, in arrival order. Call once per
    /// frame, BEFORE the frame's input commands and ticks, and run each through
    /// <see cref="FarmEngine.Core.Engine.ApplyCommand"/> (and the command log).
    /// </summary>
    public List<PluginMutationCommand> DrainCommands() => Queue.Drain().Select(queued => queued.ToCommand()).ToList();

    public void Dispose()
    {
        foreach (var off in _unsubscribe) off();
        _unsubscribe.Clear();
        Queue.Drain();
        if (_ownsHost) Host.Dispose();
    }

    /// <summary>
    /// Content ctx + hook bus + sandboxed plugin host for a project (App.tsx
    /// <c>buildPlayEngine</c> / game-shell <c>buildEngineContext</c>). Dispose the
    /// previous bridge before building a new one (e.g. after loading a save
    /// with different enabled packs).
    /// </summary>
    public static PluginEngineContext CreateEngineContext(GameProject project, JintPluginHostOptions? options = null)
    {
        var hooks = new HookBus();
        var specs = Plugins.PluginSpecsFromProject(project);
        PluginBridge? bridge = null;
        if (specs.Count > 0)
        {
            bridge = new PluginBridge(hooks, Plugins.CreatePluginHost(specs, options), specs);
        }
        return new PluginEngineContext(new EngineContext(EngineState.CreateContentFromProject(project), hooks), bridge);
    }
}
