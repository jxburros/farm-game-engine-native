using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FarmEngine.Core;
using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Interop;

/// <summary>
/// A running game inside the Rust engine (<c>farm-sim</c> through <c>farm-ffi</c>): the native
/// twin of <see cref="Engine"/> + <see cref="EngineState"/>. Commands go in and effects come
/// out; the state lives in Rust and is read back as JSON only when the host asks (debug
/// drawer, saves, tests). One thread at a time. Dispose it to free the Rust session.
/// </summary>
public sealed class RustSession : IDisposable
{
    private unsafe NativeMethods.FeSession* _handle;
    private bool _poisoned;

    private unsafe RustSession(NativeMethods.FeSession* handle)
    {
        _handle = handle;
    }

    /// <summary>True after a Rust panic: the state is no longer trustworthy and every call throws.</summary>
    public bool IsPoisoned => _poisoned;

    /// <summary>
    /// Creates a session from a (migrated) project, like <c>EngineState.CreateGameState</c> +
    /// optionally <c>Quests.AutoStartQuests</c>.
    /// </summary>
    public static RustSession Create(GameProject project, string? seed = null, bool autoStartQuests = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!FarmFfi.IsAvailable)
        {
            throw new FarmFfiException("The Rust engine library (farm_ffi) is not available in this build.");
        }

        var projectJson = JsonSerializer.SerializeToUtf8Bytes(project, JsonDefaults.Options);
        return CreateFromBytes(projectJson, seed, autoStartQuests);
    }

    /// <summary>Starts a session from a compiled FlatBuffers cartridge.</summary>
    public static RustSession CreateCartridge(byte[] cartridge, string? seed = null, bool autoStartQuests = false)
    {
        ArgumentNullException.ThrowIfNull(cartridge);
        if (!FarmFfi.IsAvailable)
        {
            throw new FarmFfiException("The Rust engine library (farm_ffi) is not available in this build.");
        }

        return CreateFromBytes(cartridge, seed, autoStartQuests);
    }

    private static RustSession CreateFromBytes(byte[] projectJson, string? seed, bool autoStartQuests)
    {
        var seedBytes = Encoding.UTF8.GetBytes(seed ?? "");
        unsafe
        {
            fixed (byte* projectPtr = projectJson)
            fixed (byte* seedPtr = seedBytes)
            {
                NativeMethods.FeSession* handle;
                NativeMethods.FeBytes error;
                var result = NativeMethods.fe_session_new(projectPtr, (nuint)projectJson.Length, seedPtr, (nuint)seedBytes.Length, autoStartQuests, &handle, &error);
                var message = Take(error);
                if (result != NativeMethods.FeResult.Ok)
                {
                    throw new FarmFfiException($"fe_session_new failed: {result}. {message}");
                }

                return new RustSession(handle);
            }
        }
    }

    /// <summary>Applies the commands in order and returns the effects of all of them.</summary>
    public List<Effect> Apply(params Command[] commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var json = JsonSerializer.SerializeToUtf8Bytes(commands, JsonDefaults.Options);
        unsafe
        {
            fixed (byte* ptr = json)
            {
                NativeMethods.FeBytes output;
                var result = NativeMethods.fe_session_apply(_handle, ptr, (nuint)json.Length, &output);
                return JsonSerializer.Deserialize<List<Effect>>(Check(result, output, nameof(Apply)), JsonDefaults.Options) ?? [];
            }
        }
    }

    /// <summary>Advances simulation ticks and returns their effects.</summary>
    public List<Effect> Tick(uint ticks)
    {
        unsafe
        {
            NativeMethods.FeBytes output;
            var result = NativeMethods.fe_session_tick(_handle, ticks, &output);
            return JsonSerializer.Deserialize<List<Effect>>(Check(result, output, nameof(Tick)), JsonDefaults.Options) ?? [];
        }
    }

    /// <summary>The state hash (<c>hashState</c>), comparable with <see cref="Hash.HashState{T}"/>.</summary>
    public string StateHash()
    {
        unsafe
        {
            NativeMethods.FeBytes output;
            return Check(NativeMethods.fe_session_hash(_handle, &output), output, nameof(StateHash));
        }
    }

    /// <summary>The whole state as stable JSON.</summary>
    public string StateJson()
    {
        unsafe
        {
            NativeMethods.FeBytes output;
            return Check(NativeMethods.fe_session_state_json(_handle, &output), output, nameof(StateJson));
        }
    }

    /// <summary>The state copied into managed records (debug drawer, tests). Costs a JSON round trip.</summary>
    public GameState State() => JsonSerializer.Deserialize<GameState>(StateJson(), JsonDefaults.Options)!;

    /// <summary>One read of the Rust rule queries used by the play overlays.</summary>
    public string OverlayJson()
    {
        unsafe
        {
            NativeMethods.FeBytes output;
            return Check(NativeMethods.fe_session_overlay_json(_handle, &output), output, nameof(OverlayJson));
        }
    }

    /// <summary>Send raw host input to Rust's runtime and copy its plain view model.</summary>
    public T Runtime<T>(object request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonDefaults.Options);
        unsafe
        {
            ObjectDisposedException.ThrowIf(_handle == null, this);
            fixed (byte* ptr = bytes)
            {
                NativeMethods.FeBytes output;
                var result = NativeMethods.fe_session_runtime_json(_handle, ptr, (nuint)bytes.Length, &output);
                return JsonSerializer.Deserialize<T>(Check(result, output, nameof(Runtime)), JsonDefaults.Options)!;
            }
        }
    }

    /// <summary>Creator debug action: run the overnight pass without a bed check.</summary>
    public void SkipDay()
    {
        unsafe
        {
            NativeMethods.FeBytes output;
            Check(NativeMethods.fe_session_skip_day(_handle, &output), output, nameof(SkipDay));
        }
    }

    /// <summary>The project with the live state written back (<c>applyStateToProject</c>).</summary>
    public GameProject SyncedProject()
    {
        unsafe
        {
            NativeMethods.FeBytes output;
            var json = Check(NativeMethods.fe_session_project_json(_handle, &output), output, nameof(SyncedProject));
            return JsonSerializer.Deserialize<GameProject>(json, JsonDefaults.Options)!;
        }
    }

    /// <summary>Hook events emitted since the last call, as raw JSON (<c>[{"hook":…,"payload":…}]</c>).</summary>
    public JsonElement DrainHookEvents()
    {
        unsafe
        {
            NativeMethods.FeBytes output;
            var json = Check(NativeMethods.fe_session_hook_events(_handle, &output), output, nameof(DrainHookEvents));
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }

    /// <summary>
    /// Replaces the live state as it is (creator debug tools): unlike <see cref="LoadSave"/>
    /// nothing is migrated or quarantined. A state Rust cannot read throws
    /// <see cref="FarmFfiException"/> and leaves the state as it was.
    /// </summary>
    public void SetState(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var json = JsonSerializer.SerializeToUtf8Bytes(state, JsonDefaults.Options);
        unsafe
        {
            fixed (byte* ptr = json)
            {
                NativeMethods.FeBytes output;
                Check(NativeMethods.fe_session_set_state(_handle, ptr, (nuint)json.Length, &output), output, nameof(SetState));
            }
        }
    }

    /// <summary>
    /// The top-level state sections that changed since the previous call, as one UTF-8 JSON
    /// object (<c>{"clock":{…},"player":{…}}</c>, <c>{}</c> when nothing changed), in engine
    /// order. <paramref name="full"/> sends every section. <see cref="GameStateMirror"/> turns
    /// these into a <see cref="GameState"/>.
    /// </summary>
    public byte[] StateChanges(bool full = false)
    {
        unsafe
        {
            NativeMethods.FeBytes output;
            var result = NativeMethods.fe_session_state_changes(_handle, full, &output);
            var bytes = TakeBytes(output);
            if (result != NativeMethods.FeResult.Ok)
            {
                Fail(result, nameof(StateChanges));
            }

            return bytes;
        }
    }

    /// <summary>
    /// A save file for the live state: a header (game id, game version, content hash) plus the
    /// state, as stable JSON. Load it with <see cref="LoadSave"/>.
    /// </summary>
    public string Save()
    {
        unsafe
        {
            NativeMethods.FeBytes output;
            return Check(NativeMethods.fe_session_save(_handle, &output), output, nameof(Save));
        }
    }

    /// <summary>
    /// Replaces the state with a save file (or a bare web <c>GameState</c>). Old saves are
    /// migrated and items the game no longer has are quarantined. A save from another game, or
    /// one that fails validation, throws <see cref="FarmFfiException"/> and leaves the state as
    /// it was.
    /// </summary>
    public SaveLoadReport LoadSave(string save)
    {
        ArgumentNullException.ThrowIfNull(save);
        var bytes = Encoding.UTF8.GetBytes(save);
        unsafe
        {
            fixed (byte* ptr = bytes)
            {
                NativeMethods.FeBytes output;
                var json = Check(NativeMethods.fe_session_load_save(_handle, ptr, (nuint)bytes.Length, &output), output, nameof(LoadSave));
                return JsonSerializer.Deserialize<SaveLoadReport>(json, JsonDefaults.Options)!;
            }
        }
    }

    public void Dispose()
    {
        unsafe
        {
            if (_handle != null)
            {
                NativeMethods.fe_session_free(_handle);
                _handle = null;
            }
        }
    }

    private unsafe string LastError()
    {
        NativeMethods.FeBytes output;
        NativeMethods.fe_session_last_error(_handle, &output);
        return Take(output);
    }

    private unsafe string Check(NativeMethods.FeResult result, NativeMethods.FeBytes output, string call)
    {
        var text = Take(output);
        if (result == NativeMethods.FeResult.Ok)
        {
            return text;
        }

        Fail(result, call);
        return text;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void Fail(NativeMethods.FeResult result, string call)
    {
        if (result is NativeMethods.FeResult.Panic or NativeMethods.FeResult.Poisoned)
        {
            _poisoned = true;
        }

        throw new FarmFfiException($"{call} failed: {result}. {LastError()}");
    }

    /// <summary>Copies a Rust buffer into a managed array and frees it.</summary>
    private static unsafe byte[] TakeBytes(NativeMethods.FeBytes bytes)
    {
        try
        {
            return bytes.Ptr == null ? [] : new ReadOnlySpan<byte>(bytes.Ptr, (int)bytes.Len).ToArray();
        }
        finally
        {
            NativeMethods.fe_bytes_free(bytes);
        }
    }

    /// <summary>Copies a Rust buffer into a string and frees it.</summary>
    private static unsafe string Take(NativeMethods.FeBytes bytes)
    {
        try
        {
            return bytes.Ptr == null ? "" : Encoding.UTF8.GetString(bytes.Ptr, (int)bytes.Len);
        }
        finally
        {
            NativeMethods.fe_bytes_free(bytes);
        }
    }
}

/// <summary>What <see cref="RustSession.LoadSave"/> did besides loading the state.</summary>
/// <param name="Warnings">Things the player should know (a newer game version wrote the save, items set aside).</param>
/// <param name="Quarantined">Ids of items the game no longer has, moved to quarantine.</param>
/// <param name="Restored">Ids of quarantined items that came back.</param>
/// <param name="FromVersion">The save version before migration.</param>
/// <param name="Migrated">The save went through save migrations.</param>
public sealed record SaveLoadReport(
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Quarantined,
    IReadOnlyList<string> Restored,
    double FromVersion,
    bool Migrated);
