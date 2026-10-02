using System.Text;
using System.Text.Json;
using FarmEngine.Authoring.Net;
using FarmEngine.Schemas;

namespace FarmEngine.Interop;

/// <summary>
/// Stateless requests to the Rust renderer (<c>crates/farm-render</c> through
/// <c>fe_render_json</c>): the decorated Edit Mode snapshot, and a PNG of any world snapshot.
/// The snapshot JSON has the shape of <c>farm_render::WorldSnapshot</c>.
/// </summary>
public static class RustRender
{
    /// <summary>
    /// The Edit Mode snapshot of <paramref name="sceneId"/> decorated with the project's art, as
    /// JSON (grid seams, the project's art at tick 0, nothing moving).
    /// </summary>
    public static string EditorSnapshotJson(GameProject project, string sceneId, double tileSize = 28, double padding = 12)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sceneId);
        var request = JsonSerializer.SerializeToUtf8Bytes(new { type = "editorSnapshot", project = RecordJson.ToNode(project), sceneId, tileSize, padding }, InteropJson.Options);
        return Encoding.UTF8.GetString(Call(request, nameof(EditorSnapshotJson)));
    }

    /// <summary>
    /// Rasterizes a world snapshot (JSON) with the Rust CPU renderer and returns PNG bytes of the
    /// viewport at <paramref name="scale"/>.
    /// </summary>
    public static byte[] RasterizePng(string snapshotJson, double scale = 1)
    {
        ArgumentNullException.ThrowIfNull(snapshotJson);
        using var snapshot = JsonDocument.Parse(snapshotJson);
        var request = JsonSerializer.SerializeToUtf8Bytes(new { type = "rasterize", snapshot = snapshot.RootElement, scale }, InteropJson.Options);
        return Call(request, nameof(RasterizePng));
    }

    private static byte[] Call(byte[] request, string call)
    {
        FarmFfi.EnsureAvailable();
        unsafe
        {
            fixed (byte* ptr = request)
            {
                NativeMethods.FeBytes output = default;
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
