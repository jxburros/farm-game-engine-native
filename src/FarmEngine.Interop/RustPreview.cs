using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using FarmEngine.Authoring.Net;
using FarmEngine.Schemas;

namespace FarmEngine.Interop;

/// <summary>A viewport of the map in world pixels (<c>SnapshotCamera</c>).</summary>
public sealed record PreviewCamera(double X, double Y, double Width, double Height);

/// <summary>A rendered preview: <paramref name="Pixels"/> holds Width × Height premultiplied RGBA8 pixels, row by row.</summary>
public sealed record PreviewFrame(int Width, int Height, byte[] Pixels);

/// <summary>
/// Edit Mode's map preview drawn by the Rust renderer (<c>fe_preview_*</c>): it keeps the
/// project, its compiled content and the decoded images between frames and rasterizes one scene
/// viewport per call. One thread at a time. Dispose it to free the Rust side.
/// </summary>
public sealed class RustPreview : IDisposable
{
    private unsafe NativeMethods.FePreview* _handle;
    private bool _poisoned;

    private unsafe RustPreview(NativeMethods.FePreview* handle)
    {
        _handle = handle;
    }

    /// <summary>True after a Rust panic: every later call throws.</summary>
    public bool IsPoisoned => _poisoned;

    /// <summary>Creates a preview of a (migrated) project.</summary>
    public static RustPreview Create(GameProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!FarmFfi.IsAvailable)
        {
            throw new FarmFfiException("The Rust engine library (farm_ffi) is not available in this build.");
        }

        var json = RecordJson.ToUtf8(project);
        unsafe
        {
            fixed (byte* ptr = json)
            {
                NativeMethods.FePreview* handle;
                NativeMethods.FeBytes error;
                var result = NativeMethods.fe_preview_new(ptr, (nuint)json.Length, &handle, &error);
                var message = Encoding.UTF8.GetString(RustRender.TakeBytes(error));
                if (result != NativeMethods.FeResult.Ok)
                {
                    throw new FarmFfiException($"fe_preview_new failed: {result}. {message}");
                }

                return new RustPreview(handle);
            }
        }
    }

    /// <summary>Replaces the previewed project (after an edit); decoded images stay cached.</summary>
    public void SetProject(GameProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var json = RecordJson.ToUtf8(project);
        unsafe
        {
            ObjectDisposedException.ThrowIf(_handle == null, this);
            fixed (byte* ptr = json)
            {
                NativeMethods.FeBytes output;
                var result = NativeMethods.fe_preview_set_project(_handle, ptr, (nuint)json.Length, &output);
                Check(result, RustRender.TakeBytes(output), nameof(SetProject));
            }
        }
    }

    /// <summary>
    /// Renders scene <paramref name="sceneId"/> as Edit Mode shows it (grid seams, grid overlay,
    /// the project's art) at <paramref name="tileSize"/>. With a <paramref name="camera"/> only
    /// that viewport is drawn; the frame is the viewport size × <paramref name="scale"/>, rounded up.
    /// </summary>
    public PreviewFrame Render(string sceneId, double tileSize = 28, double padding = 12, PreviewCamera? camera = null, double scale = 1)
    {
        ArgumentNullException.ThrowIfNull(sceneId);
        var request = JsonSerializer.SerializeToUtf8Bytes(new { sceneId, tileSize, padding, camera, scale }, InteropJson.Options);
        unsafe
        {
            ObjectDisposedException.ThrowIf(_handle == null, this);
            fixed (byte* ptr = request)
            {
                NativeMethods.FeBytes output;
                var result = NativeMethods.fe_preview_render(_handle, ptr, (nuint)request.Length, &output);
                var bytes = Check(result, RustRender.TakeBytes(output), nameof(Render));
                var width = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
                var height = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
                return new PreviewFrame(width, height, bytes[8..]);
            }
        }
    }

    /// <summary>
    /// One frame of a visual binding of the project, resolved as the game resolves it (clip and
    /// frame at <paramref name="tick"/>, facing <paramref name="direction"/>), fitted into a
    /// <paramref name="size"/>-pixel square. A 0×0 frame when the binding resolves to nothing.
    /// </summary>
    public PreviewFrame RenderVisual(VisualRef? visual, double tick, double size, double scale = 1, string direction = "down", bool moving = true)
    {
        var request = JsonSerializer.SerializeToUtf8Bytes(new { visual, tick, size, scale, direction, moving }, InteropJson.Options);
        unsafe
        {
            ObjectDisposedException.ThrowIf(_handle == null, this);
            fixed (byte* ptr = request)
            {
                NativeMethods.FeBytes output;
                var result = NativeMethods.fe_preview_render_visual(_handle, ptr, (nuint)request.Length, &output);
                var bytes = Check(result, RustRender.TakeBytes(output), nameof(RenderVisual));
                var width = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
                var height = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
                return new PreviewFrame(width, height, bytes[8..]);
            }
        }
    }

    public void Dispose()
    {
        unsafe
        {
            if (_handle != null)
            {
                NativeMethods.fe_preview_free(_handle);
                _handle = null;
            }
        }
    }

    private byte[] Check(NativeMethods.FeResult result, byte[] output, string call)
    {
        if (result == NativeMethods.FeResult.Ok)
        {
            return output;
        }

        if (result is NativeMethods.FeResult.Panic or NativeMethods.FeResult.Poisoned)
        {
            _poisoned = true;
        }

        throw new FarmFfiException($"{call} failed: {result}. {Encoding.UTF8.GetString(output)}");
    }
}
