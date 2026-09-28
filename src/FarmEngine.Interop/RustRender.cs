using System.Text;
using System.Text.Json;
using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Interop;

/// <summary>
/// Stateless requests to the Rust renderer (<c>crates/farm-render</c> through
/// <c>fe_render_json</c>): the decorated Edit Mode snapshot, and a PNG of any world snapshot.
/// The snapshot JSON has the shape of <c>FarmEngine.Rendering.WorldSnapshot</c>.
/// </summary>
public static class RustRender
{
    /// <summary>
    /// The Edit Mode snapshot of <paramref name="sceneId"/> decorated with the project's art, as
    /// JSON: what <c>EditModeView</c> builds with <c>ShellSnapshot.BuildEditorSnapshot</c> and
    /// <c>Graphics.ApplyGraphics(snapshot, GraphicsSource.FromProject(project), scene, 0, false)</c>.
    /// </summary>
    public static string EditorSnapshotJson(GameProject project, string sceneId, double tileSize = 28, double padding = 12)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sceneId);
        var request = JsonSerializer.SerializeToUtf8Bytes(new { type = "editorSnapshot", project, sceneId, tileSize, padding }, JsonDefaults.Options);
        return Encoding.UTF8.GetString(Call(request, nameof(EditorSnapshotJson)));
    }

    /// <summary>
    /// Rasterizes a world snapshot (JSON) with the Rust CPU renderer and returns PNG bytes of the
    /// viewport at <paramref name="scale"/>, like <c>SkiaWorldRenderer.RenderToBitmap</c>.
    /// </summary>
    public static byte[] RasterizePng(string snapshotJson, double scale = 1)
    {
        ArgumentNullException.ThrowIfNull(snapshotJson);
        using var snapshot = JsonDocument.Parse(snapshotJson);
        var request = JsonSerializer.SerializeToUtf8Bytes(new { type = "rasterize", snapshot = snapshot.RootElement, scale }, JsonDefaults.Options);
        return Call(request, nameof(RasterizePng));
    }

    private static byte[] Call(byte[] request, string call)
    {
        if (!FarmFfi.IsAvailable)
        {
            throw new FarmFfiException("The Rust engine library (farm_ffi) is not available in this build.");
        }

        unsafe
        {
            fixed (byte* ptr = request)
            {
                NativeMethods.FeBytes output;
                var result = NativeMethods.fe_render_json(ptr, (nuint)request.Length, &output);
                var bytes = TakeBytes(output);
                if (result != NativeMethods.FeResult.Ok)
                {
                    throw new FarmFfiException($"{call} failed: {result}. {Encoding.UTF8.GetString(bytes)}");
                }

                return bytes;
            }
        }
    }

    /// <summary>Copies a Rust buffer into a managed array and frees it.</summary>
    internal static unsafe byte[] TakeBytes(NativeMethods.FeBytes bytes)
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
}
