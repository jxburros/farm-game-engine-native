using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Interop;

/// <summary>Raised when the Rust side returns an error code.</summary>
public sealed class FarmFfiException(string message) : Exception(message);

/// <summary>
/// Managed entry point to the Rust engine (<c>farm-ffi</c>). All calls are coarse and batched;
/// Rust allocates result buffers, this class copies them into managed memory and frees them
/// before returning, so no pointer into Rust memory ever escapes.
/// </summary>
public static class FarmFfi
{
    private static readonly Lazy<bool> Available = new(Probe);

    /// <summary>True when the native library loaded (a Rust toolchain built it into the output folder).</summary>
    public static bool IsAvailable => Available.Value;

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

    /// <summary>FNV-1a state hash of <paramref name="text"/> computed by Rust (parity check with <c>Hash.HashText</c>).</summary>
    public static string HashText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        EnsureAvailable();
        var bytes = Encoding.UTF8.GetBytes(text);
        unsafe
        {
            fixed (byte* input = bytes)
            {
                NativeMethods.FeBytes output;
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

    /// <summary>Build an editor preview in Rust using content compiled by F#.</summary>
    public static string EditorSnapshotJson(GameProject project, GameContent content, string sceneId, double tileSize, double padding)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(content);
        EnsureAvailable();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { project, content, sceneId, tileSize, padding }, JsonDefaults.Options);
        unsafe
        {
            fixed (byte* input = bytes)
            {
                NativeMethods.FeBytes output;
                var result = NativeMethods.fe_preview_snapshot_json(input, (nuint)bytes.Length, &output);
                try
                {
                    var text = output.Ptr == null ? "" : Encoding.UTF8.GetString(output.Ptr, (int)output.Len);
                    if (result != NativeMethods.FeResult.Ok)
                        throw new FarmFfiException($"Editor snapshot failed: {result}. {text}");
                    return text;
                }
                finally
                {
                    NativeMethods.fe_bytes_free(output);
                }
            }
        }
    }

    private static void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            throw new FarmFfiException("The Rust engine library (farm_ffi) is not available in this build.");
        }
    }

    private static void Check(NativeMethods.FeResult result, string call)
    {
        if (result != NativeMethods.FeResult.Ok)
        {
            throw new FarmFfiException($"{call} failed: {result}.");
        }
    }

    private static bool Probe()
    {
        try
        {
            unsafe
            {
                return NativeMethods.fe_version() != null;
            }
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }
}
