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
    }

    [LibraryImport(Library, EntryPoint = "fe_version")]
    public static partial byte* fe_version();

    [LibraryImport(Library, EntryPoint = "fe_bytes_free")]
    public static partial void fe_bytes_free(FeBytes bytes);

    [LibraryImport(Library, EntryPoint = "fe_hash_text")]
    public static partial FeResult fe_hash_text(byte* text, nuint len, FeBytes* output);
}
