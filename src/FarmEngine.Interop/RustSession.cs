using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FarmEngine.Authoring.Net;
using FarmEngine.Schemas;

namespace FarmEngine.Interop;

/// <summary>
/// A headless game inside the Rust engine (<c>farm-sim</c> through <c>fe_session_*</c>) for tools
/// and tests: commands go in and effects come out, all as JSON; the state lives in Rust and is
/// read back only when asked. (Play Mode runs the graphical <see cref="RustPlayer"/> instead.)
/// One thread at a time. Dispose it to free the Rust session.
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
    /// Creates a session from a (migrated) project (<c>createGameState</c>, then optionally
    /// <c>autoStartQuests</c>).
    /// </summary>
    public static RustSession Create(GameProject project, string? seed = null, bool autoStartQuests = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!FarmFfi.IsAvailable)
        {
            throw new FarmFfiException("The Rust engine library (farm_ffi) is not available in this build.");
        }

        var projectJson = RecordJson.ToUtf8(project);
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

    /// <summary>
    /// Applies a JSON array of commands (<c>[{"type":"sleep"}]</c>) in order and returns the
    /// effects of all of them as a JSON array.
    /// </summary>
    public JsonArray Apply(string commandsJson)
    {
        ArgumentNullException.ThrowIfNull(commandsJson);
        var json = Encoding.UTF8.GetBytes(commandsJson);
        unsafe
        {
            ObjectDisposedException.ThrowIf(_handle == null, this);
            fixed (byte* ptr = json)
            {
                NativeMethods.FeBytes output;
                var result = NativeMethods.fe_session_apply(_handle, ptr, (nuint)json.Length, &output);
                return JsonNode.Parse(Check(result, output, nameof(Apply)))!.AsArray();
            }
        }
    }

    /// <summary>Applies commands given as objects (serialized with the schema's JSON options).</summary>
    public JsonArray Apply(params object[] commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        return Apply(JsonSerializer.Serialize(commands, InteropJson.Options));
    }

    /// <summary>Advances simulation ticks and returns their effects as a JSON array.</summary>
    public JsonArray Tick(uint ticks)
    {
        unsafe
        {
            ObjectDisposedException.ThrowIf(_handle == null, this);
            NativeMethods.FeBytes output;
            var result = NativeMethods.fe_session_tick(_handle, ticks, &output);
            return JsonNode.Parse(Check(result, output, nameof(Tick)))!.AsArray();
        }
    }

    /// <summary>The state hash (<c>hashState</c>).</summary>
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

    /// <summary>The whole state as a JSON node (tests, tools).</summary>
    public JsonObject State() => JsonNode.Parse(StateJson())!.AsObject();

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
            return RecordJson.Parse<GameProject>(json);
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
    /// Replaces the live state with <paramref name="stateJson"/> as it is (creator tools): unlike
    /// <see cref="LoadSave"/> nothing is migrated or quarantined. A state Rust cannot read throws
    /// <see cref="FarmFfiException"/> and leaves the state as it was.
    /// </summary>
    public void SetState(string stateJson)
    {
        ArgumentNullException.ThrowIfNull(stateJson);
        var json = Encoding.UTF8.GetBytes(stateJson);
        unsafe
        {
            ObjectDisposedException.ThrowIf(_handle == null, this);
            fixed (byte* ptr = json)
            {
                NativeMethods.FeBytes output;
                Check(NativeMethods.fe_session_set_state(_handle, ptr, (nuint)json.Length, &output), output, nameof(SetState));
            }
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
                return JsonSerializer.Deserialize<SaveLoadReport>(json, InteropJson.Options)!;
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
