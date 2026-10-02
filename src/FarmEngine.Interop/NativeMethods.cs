using System.Reflection;
using System.Runtime.InteropServices;

// No P/Invoke in this assembly searches the OS library path: farm_ffi is loaded from the
// application's folder only (see NativeMethods.Resolve), never from the current directory or PATH.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]

namespace FarmEngine.Interop;

/// <summary>
/// Raw P/Invoke surface of <c>crates/farm-ffi</c>. Every entry point mirrors a
/// <c>#[no_mangle] extern "C"</c> function in <c>crates/farm-ffi/src</c>; keep the two in
/// step and bump <see cref="AbiVersion"/> with <c>FE_ABI_VERSION</c>. Callers go through
/// <see cref="FarmFfi"/> and the handle wrappers, which free Rust buffers and turn result codes
/// into exceptions. Handles are <see cref="SafeHandle"/>s: the generated marshalling keeps a
/// handle alive while a call uses it, and a disposed handle throws
/// <see cref="ObjectDisposedException"/> instead of reaching Rust.
/// </summary>
internal static unsafe partial class NativeMethods
{
    /// <summary>Library name without prefix/extension; the runtime probes <c>farm_ffi.dll</c>, <c>libfarm_ffi.so</c> and <c>libfarm_ffi.dylib</c>.</summary>
    public const string Library = "farm_ffi";

    /// <summary>The <c>FE_ABI_VERSION</c> these declarations were written for (<c>crates/farm-ffi/src/lib.rs</c>).</summary>
    public const uint AbiVersion = 2;

    /// <summary>A Rust-allocated buffer (<c>FeBytes</c>). Free with <see cref="fe_bytes_free"/>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FeBytes
    {
        public byte* Ptr;
        public nuint Len;
        public nuint Cap;
    }

    /// <summary>Result codes (<c>FeResult</c>).</summary>
    public enum FeResult
    {
        Ok = 0,
        InvalidArgument = 1,
        Panic = 2,
        /// <summary>A previous call on this handle panicked (or the game stopped); its state is not trustworthy.</summary>
        Poisoned = 3,
    }

    /// <summary>
    /// Loads farm_ffi from the folder of this assembly (the application's folder) and nowhere
    /// else; <see cref="FarmFfi"/> installs it before the first call. Other libraries fall
    /// back to the default probing.
    /// </summary>
    internal static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != Library)
        {
            return 0;
        }

        // Throws DllNotFoundException with the loader's own message (a missing dependency such
        // as libasound.so.2, a wrong architecture), which FarmFfi keeps for the status it shows.
        return NativeLibrary.Load(LibraryPath(assembly));
    }

    /// <summary>Where farm_ffi must be: next to this assembly.</summary>
    internal static string LibraryPath(Assembly assembly)
    {
        var folder = Path.GetDirectoryName(assembly.Location) is { Length: > 0 } directory ? directory : AppContext.BaseDirectory;
        var file = OperatingSystem.IsWindows() ? "farm_ffi.dll" : OperatingSystem.IsMacOS() ? "libfarm_ffi.dylib" : "libfarm_ffi.so";
        return Path.Combine(folder, file);
    }

    [LibraryImport(Library, EntryPoint = "fe_version")]
    public static partial byte* fe_version();

    [LibraryImport(Library, EntryPoint = "fe_abi_version")]
    public static partial uint fe_abi_version();

    [LibraryImport(Library, EntryPoint = "fe_bytes_free")]
    public static partial void fe_bytes_free(FeBytes bytes);

    [LibraryImport(Library, EntryPoint = "fe_hash_text")]
    public static partial FeResult fe_hash_text(byte* text, nuint len, FeBytes* output);

    // ---- crates/farm-ffi/src/session.rs -----------------------------------------------------

    /// <summary>An engine session (<c>FeSession*</c>), freed with <c>fe_session_free</c>.</summary>
    public sealed class SessionHandle() : SafeHandle(0, ownsHandle: true)
    {
        public override bool IsInvalid => handle == 0;

        protected override bool ReleaseHandle()
        {
            fe_session_free(handle);
            return true;
        }
    }

    [LibraryImport(Library, EntryPoint = "fe_session_new")]
    public static partial FeResult fe_session_new(byte* projectJson, nuint len, byte* seed, nuint seedLen, [MarshalAs(UnmanagedType.U1)] bool autoStartQuests, out SessionHandle output, FeBytes* error);

    [LibraryImport(Library, EntryPoint = "fe_session_free")]
    private static partial void fe_session_free(nint session);

    [LibraryImport(Library, EntryPoint = "fe_session_apply")]
    public static partial FeResult fe_session_apply(SessionHandle session, byte* commandsJson, nuint len, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_session_tick")]
    public static partial FeResult fe_session_tick(SessionHandle session, uint ticks, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_session_state_json")]
    public static partial FeResult fe_session_state_json(SessionHandle session, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_session_hash")]
    public static partial FeResult fe_session_hash(SessionHandle session, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_session_project_json")]
    public static partial FeResult fe_session_project_json(SessionHandle session, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_session_skip_day")]
    public static partial FeResult fe_session_skip_day(SessionHandle session, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_session_set_scripted")]
    public static partial FeResult fe_session_set_scripted(SessionHandle session, [MarshalAs(UnmanagedType.U1)] bool scripted, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_session_hook_events")]
    public static partial FeResult fe_session_hook_events(SessionHandle session, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_session_set_state")]
    public static partial FeResult fe_session_set_state(SessionHandle session, byte* stateJson, nuint len, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_session_save")]
    public static partial FeResult fe_session_save(SessionHandle session, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_session_load_save")]
    public static partial FeResult fe_session_load_save(SessionHandle session, byte* save, nuint len, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_session_last_error")]
    public static partial FeResult fe_session_last_error(SessionHandle session, FeBytes* output);

    // ---- crates/farm-ffi/src/render.rs ------------------------------------------------------

    /// <summary>An Edit Mode preview (<c>FePreview*</c>), freed with <c>fe_preview_free</c>.</summary>
    public sealed class PreviewHandle() : SafeHandle(0, ownsHandle: true)
    {
        public override bool IsInvalid => handle == 0;

        protected override bool ReleaseHandle()
        {
            fe_preview_free(handle);
            return true;
        }
    }

    [LibraryImport(Library, EntryPoint = "fe_render_json")]
    public static partial FeResult fe_render_json(byte* request, nuint len, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_preview_new")]
    public static partial FeResult fe_preview_new(byte* projectJson, nuint len, out PreviewHandle output, FeBytes* error);

    [LibraryImport(Library, EntryPoint = "fe_preview_set_project")]
    public static partial FeResult fe_preview_set_project(PreviewHandle preview, byte* projectJson, nuint len, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_preview_set_scenes")]
    public static partial FeResult fe_preview_set_scenes(PreviewHandle preview, byte* scenesJson, nuint len, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_preview_render")]
    public static partial FeResult fe_preview_render(PreviewHandle preview, byte* request, nuint len, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_preview_render_visual")]
    public static partial FeResult fe_preview_render_visual(PreviewHandle preview, byte* request, nuint len, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_preview_last_error")]
    public static partial FeResult fe_preview_last_error(PreviewHandle preview, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_preview_free")]
    private static partial void fe_preview_free(nint preview);

    // ---- crates/farm-ffi/src/player.rs ------------------------------------------------------

    /// <summary>An embedded player (<c>FePlayer*</c>), freed with <c>fe_player_free</c>.</summary>
    public sealed class PlayerHandle() : SafeHandle(0, ownsHandle: true)
    {
        public override bool IsInvalid => handle == 0;

        protected override bool ReleaseHandle()
        {
            fe_player_free(handle);
            return true;
        }
    }

    [LibraryImport(Library, EntryPoint = "fe_player_new")]
    public static partial FeResult fe_player_new(byte* game, nuint len, byte* options, nuint optionsLen, out PlayerHandle output, FeBytes* error);

    [LibraryImport(Library, EntryPoint = "fe_player_frame")]
    public static partial FeResult fe_player_frame(PlayerHandle player, byte* request, nuint len, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_player_frame_info")]
    public static partial FeResult fe_player_frame_info(PlayerHandle player, byte* request, nuint len, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_player_copy_pixels")]
    public static partial FeResult fe_player_copy_pixels(PlayerHandle player, byte* destination, nuint length, nuint stride, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_player_debug")]
    public static partial FeResult fe_player_debug(PlayerHandle player, byte* action, nuint len, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_player_commands")]
    public static partial FeResult fe_player_commands(PlayerHandle player, byte* commands, nuint len, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_player_state_json")]
    public static partial FeResult fe_player_state_json(PlayerHandle player, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_player_hash")]
    public static partial FeResult fe_player_hash(PlayerHandle player, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_player_synced_project")]
    public static partial FeResult fe_player_synced_project(PlayerHandle player, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_player_query_json")]
    public static partial FeResult fe_player_query_json(PlayerHandle player, byte* query, nuint len, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_player_last_error")]
    public static partial FeResult fe_player_last_error(PlayerHandle player, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_player_free")]
    private static partial void fe_player_free(nint player);
}
