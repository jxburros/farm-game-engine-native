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
/// Calls are serialized; dispose it to free the Rust session (its finalizer does when nobody
/// did), after which every call throws <see cref="ObjectDisposedException"/>.
/// </summary>
public sealed unsafe class RustSession : IDisposable
{
    private readonly NativeHandle<NativeMethods.SessionHandle> _native;

    private RustSession(NativeMethods.SessionHandle handle)
    {
        _native = new NativeHandle<NativeMethods.SessionHandle>(handle, this, prefixMessages: true);
    }

    /// <summary>True after a Rust panic: the state is no longer trustworthy and every call throws.</summary>
    public bool IsPoisoned => _native.IsPoisoned;

    /// <summary>
    /// Creates a session from a (migrated) project (<c>createGameState</c>, then optionally
    /// <c>autoStartQuests</c>).
    /// </summary>
    public static RustSession Create(GameProject project, string? seed = null, bool autoStartQuests = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        FarmFfi.EnsureAvailable();
        var projectJson = RecordJson.ToUtf8(project);
        return CreateFromBytes(projectJson, seed, autoStartQuests);
    }

    /// <summary>Starts a session from a compiled FlatBuffers cartridge.</summary>
    public static RustSession CreateCartridge(byte[] cartridge, string? seed = null, bool autoStartQuests = false)
    {
        ArgumentNullException.ThrowIfNull(cartridge);
        FarmFfi.EnsureAvailable();
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
                NativeMethods.FeBytes error = default;
                var result = NativeMethods.fe_session_new(projectPtr, (nuint)projectJson.Length, seedPtr, (nuint)seedBytes.Length, autoStartQuests, out var handle, &error);
                var message = Encoding.UTF8.GetString(RustRender.TakeBytes(error));
                if (result != NativeMethods.FeResult.Ok)
                {
                    handle.Dispose();
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
        var json = _native.CallText(nameof(Apply), Encoding.UTF8.GetBytes(commandsJson), NativeMethods.fe_session_apply);
        return JsonNode.Parse(json)!.AsArray();
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
            var json = _native.CallText(nameof(Tick), (handle, output) => NativeMethods.fe_session_tick(handle, ticks, output));
            return JsonNode.Parse(json)!.AsArray();
        }
    }

    /// <summary>
    /// Whether commands apply wherever the player stands (<paramref name="scripted"/> true:
    /// scripts, test harnesses, replays) or only where a player could give them (false, the
    /// default; <c>farm_sim::CommandRules</c>).
    /// </summary>
    public void SetScripted(bool scripted)
    {
        unsafe
        {
            _native.Call(nameof(SetScripted), (handle, output) => NativeMethods.fe_session_set_scripted(handle, scripted, output));
        }
    }

    /// <summary>The state hash (<c>hashState</c>).</summary>
    public string StateHash()
    {
        unsafe
        {
            return _native.CallText(nameof(StateHash), NativeMethods.fe_session_hash);
        }
    }

    /// <summary>The whole state as stable JSON.</summary>
    public string StateJson()
    {
        unsafe
        {
            return _native.CallText(nameof(StateJson), NativeMethods.fe_session_state_json);
        }
    }

    /// <summary>The whole state as a JSON node (tests, tools).</summary>
    public JsonObject State() => JsonNode.Parse(StateJson())!.AsObject();

    /// <summary>Creator debug action: run the overnight pass without a bed check.</summary>
    public void SkipDay()
    {
        unsafe
        {
            _native.Call(nameof(SkipDay), NativeMethods.fe_session_skip_day);
        }
    }

    /// <summary>The project with the live state written back (<c>applyStateToProject</c>).</summary>
    public GameProject SyncedProject()
    {
        unsafe
        {
            return RecordJson.Parse<GameProject>(_native.CallText(nameof(SyncedProject), NativeMethods.fe_session_project_json));
        }
    }

    /// <summary>Hook events emitted since the last call, as raw JSON (<c>[{"hook":…,"payload":…}]</c>).</summary>
    public JsonElement DrainHookEvents()
    {
        unsafe
        {
            using var document = JsonDocument.Parse(_native.Call(nameof(DrainHookEvents), NativeMethods.fe_session_hook_events));
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
        _native.Call(nameof(SetState), Encoding.UTF8.GetBytes(stateJson), NativeMethods.fe_session_set_state);
    }

    /// <summary>
    /// A save file for the live state: a header (game id, game version, content hash) plus the
    /// state, as stable JSON. Load it with <see cref="LoadSave"/>.
    /// </summary>
    public string Save()
    {
        unsafe
        {
            return _native.CallText(nameof(Save), NativeMethods.fe_session_save);
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
        var json = _native.Call(nameof(LoadSave), Encoding.UTF8.GetBytes(save), NativeMethods.fe_session_load_save);
        return JsonSerializer.Deserialize<SaveLoadReport>(json, InteropJson.Options)!;
    }

    public void Dispose() => _native.Dispose();
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
