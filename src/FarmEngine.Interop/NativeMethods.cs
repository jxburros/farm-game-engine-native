using System.Runtime.InteropServices;

namespace FarmEngine.Interop;

/// <summary>
/// Raw P/Invoke surface of <c>crates/farm-ffi</c>. Every entry point mirrors a
/// <c>#[no_mangle] extern "C"</c> function in <c>crates/farm-ffi/src/lib.rs</c>; keep the two in
/// step. Callers go through <see cref="FarmFfi"/>, which frees Rust buffers and turns result
/// codes into exceptions.
/// </summary>
internal static unsafe partial class NativeMethods
{
    /// <summary>Library name without prefix/extension; the runtime probes <c>farm_ffi.dll</c>, <c>libfarm_ffi.so</c> and <c>libfarm_ffi.dylib</c>.</summary>
    public const string Library = "farm_ffi";

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
        /// <summary>A previous call on this session panicked; its state is not trustworthy.</summary>
        Poisoned = 3,
    }

    /// <summary>Opaque session handle (<c>FeSession*</c>).</summary>
    public struct FeSession
    {
    }

    [LibraryImport(Library, EntryPoint = "fe_version")]
    public static partial byte* fe_version();

    [LibraryImport(Library, EntryPoint = "fe_bytes_free")]
    public static partial void fe_bytes_free(FeBytes bytes);

    [LibraryImport(Library, EntryPoint = "fe_hash_text")]
    public static partial FeResult fe_hash_text(byte* text, nuint len, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_session_new")]
    public static partial FeResult fe_session_new(byte* projectJson, nuint len, byte* seed, nuint seedLen, [MarshalAs(UnmanagedType.U1)] bool autoStartQuests, FeSession** output, FeBytes* error);

    [LibraryImport(Library, EntryPoint = "fe_session_free")]
    public static partial void fe_session_free(FeSession* session);

    [LibraryImport(Library, EntryPoint = "fe_session_apply")]
    public static partial FeResult fe_session_apply(FeSession* session, byte* commandsJson, nuint len, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_session_tick")]
    public static partial FeResult fe_session_tick(FeSession* session, uint ticks, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_session_state_json")]
    public static partial FeResult fe_session_state_json(FeSession* session, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_session_hash")]
    public static partial FeResult fe_session_hash(FeSession* session, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_session_project_json")]
    public static partial FeResult fe_session_project_json(FeSession* session, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_session_overlay_json")]
    public static partial FeResult fe_session_overlay_json(FeSession* session, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_session_runtime_json")]
    public static partial FeResult fe_session_runtime_json(FeSession* session, byte* request, nuint len, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_session_skip_day")]
    public static partial FeResult fe_session_skip_day(FeSession* session, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_session_hook_events")]
    public static partial FeResult fe_session_hook_events(FeSession* session, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_session_set_state")]
    public static partial FeResult fe_session_set_state(FeSession* session, byte* stateJson, nuint len, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_session_state_changes")]
    public static partial FeResult fe_session_state_changes(FeSession* session, [MarshalAs(UnmanagedType.U1)] bool full, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_session_save")]
    public static partial FeResult fe_session_save(FeSession* session, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_session_load_save")]
    public static partial FeResult fe_session_load_save(FeSession* session, byte* save, nuint len, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_session_last_error")]
    public static partial FeResult fe_session_last_error(FeSession* session, FeBytes* output);

    // ---- crates/farm-ffi/src/render.rs ------------------------------------------------------

    /// <summary>Opaque Edit Mode preview handle (<c>FePreview*</c>).</summary>
    public struct FePreview
    {
    }

    [LibraryImport(Library, EntryPoint = "fe_render_json")]
    public static partial FeResult fe_render_json(byte* request, nuint len, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_preview_new")]
    public static partial FeResult fe_preview_new(byte* projectJson, nuint len, FePreview** output, FeBytes* error);

    [LibraryImport(Library, EntryPoint = "fe_preview_set_project")]
    public static partial FeResult fe_preview_set_project(FePreview* preview, byte* projectJson, nuint len, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_preview_render")]
    public static partial FeResult fe_preview_render(FePreview* preview, byte* request, nuint len, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_preview_render_visual")]
    public static partial FeResult fe_preview_render_visual(FePreview* preview, byte* request, nuint len, FeBytes* output);

    [LibraryImport(Library, EntryPoint = "fe_preview_free")]
    public static partial void fe_preview_free(FePreview* preview);
}
