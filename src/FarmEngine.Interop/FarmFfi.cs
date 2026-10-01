using System.Runtime.InteropServices;
using System.Text;

namespace FarmEngine.Interop;

/// <summary>Raised when the Rust side returns an error code.</summary>
public sealed class FarmFfiException(string message) : Exception(message);

/// <summary>
/// Managed entry point to the Rust engine (<c>farm-ffi</c>). All calls are coarse and batched;
/// Rust allocates result buffers, this class copies them into managed memory and frees them
/// before returning, so no pointer into Rust memory ever escapes.
/// </summary>
/// <remarks>
/// The library is loaded from the application's folder only (never the current directory or
/// PATH), and only when it speaks the ABI version these bindings were written for
/// (<c>fe_abi_version</c>): a stale library with other signatures is refused instead of called.
/// Why it did not load is kept in <see cref="LoadError"/>.
/// </remarks>
public static class FarmFfi
{
    private static readonly Lazy<string?> Loaded = new(Probe);

    /// <summary>True when the native library loaded (a Rust toolchain built it into the output folder).</summary>
    public static bool IsAvailable => Loaded.Value is null;

    /// <summary>
    /// Why the native library is not available (missing from the build, a missing system
    /// dependency the loader named, another ABI version), or null when it loaded.
    /// </summary>
    public static string? LoadError => Loaded.Value;

    /// <summary>The C ABI version these bindings expect (<c>FE_ABI_VERSION</c>).</summary>
    public static uint AbiVersion => NativeMethods.AbiVersion;

    /// <summary>The Rust crate version, or null when the library is not available.</summary>
    public static string? Version
    {
        get
        {
            if (!IsAvailable)
            {
                return null;
            }

            unsafe
            {
                return Marshal.PtrToStringUTF8((nint)NativeMethods.fe_version());
            }
        }
    }

    /// <summary>The v8 text hash (FNV-1a, <c>hashText</c>) of <paramref name="text"/>, computed by Rust. Not the
    /// state hash since v9: hashing a state's JSON with it never matches <c>StateHash()</c>.</summary>
    public static string HashText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        EnsureAvailable();
        var bytes = Encoding.UTF8.GetBytes(text);
        unsafe
        {
            // An empty array pins to null; Rust accepts null with length 0.
            fixed (byte* input = bytes)
            {
                NativeMethods.FeBytes output = default;
                var result = NativeMethods.fe_hash_text(input, (nuint)bytes.Length, &output);
                try
                {
                    Check(result, nameof(HashText));
                    return Encoding.ASCII.GetString(output.Ptr, (int)output.Len);
                }
                finally
                {
                    NativeMethods.fe_bytes_free(output);
                }
            }
        }
    }

    /// <summary>Throws <see cref="FarmFfiException"/> with <see cref="LoadError"/> when the library is not available.</summary>
    internal static void EnsureAvailable()
    {
        if (LoadError is { } error)
        {
            throw new FarmFfiException($"The Rust engine library (farm_ffi) is not available: {error}");
        }
    }

    private static void Check(NativeMethods.FeResult result, string call)
    {
        if (result != NativeMethods.FeResult.Ok)
        {
            throw new FarmFfiException($"{call} failed: {result}.");
        }
    }

    private static string? Probe()
    {
        var assembly = typeof(NativeMethods).Assembly;
        try
        {
            NativeLibrary.SetDllImportResolver(assembly, NativeMethods.Resolve);
        }
        catch (InvalidOperationException)
        {
            // A host installed its own resolver for this assembly first; it decides.
        }

        var path = NativeMethods.LibraryPath(assembly);
        if (!File.Exists(path))
        {
            return $"{Path.GetFileName(path)} is not in {Path.GetDirectoryName(path)} (this build was made without the Rust toolchain).";
        }

        try
        {
            unsafe
            {
                var abi = NativeMethods.fe_abi_version();
                if (abi != NativeMethods.AbiVersion)
                {
                    return $"{path} speaks C ABI version {abi}, but this program needs version {NativeMethods.AbiVersion}. Rebuild farm-ffi from the same sources.";
                }

                return NativeMethods.fe_version() != null ? null : $"{path} reported no version.";
            }
        }
        catch (DllNotFoundException ex)
        {
            return $"{path} could not be loaded: {ex.Message}";
        }
        catch (EntryPointNotFoundException)
        {
            return $"{path} is older than this program (it has no fe_abi_version). Rebuild farm-ffi from the same sources.";
        }
        catch (BadImageFormatException ex)
        {
            return $"{path} is not a library for this machine: {ex.Message}";
        }
    }
}
