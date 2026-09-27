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
            return JsonDocument.Parse(json).RootElement.Clone();
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
