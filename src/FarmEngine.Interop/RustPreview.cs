using System.Buffers.Binary;
using System.Reflection;
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
/// project, its compiled content, the decoded images and the snapshot of the scene on screen
/// between frames and rasterizes one scene viewport per call. Calls are serialized; dispose it to
/// free the Rust side (its finalizer does when nobody did).
/// </summary>
public sealed unsafe class RustPreview : IDisposable
{
    private static readonly PropertyInfo[] ProjectFields = typeof(GameProject)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.GetIndexParameters().Length == 0 && property.Name != nameof(GameProject.Scenes))
        .ToArray();

    private readonly NativeHandle<NativeMethods.PreviewHandle> _native;

    /// <summary>The project Rust has, to send only what an edit changed.</summary>
    private GameProject _sent;

    private RustPreview(NativeMethods.PreviewHandle handle, GameProject project)
    {
        _native = new NativeHandle<NativeMethods.PreviewHandle>(handle, this, prefixMessages: true);
        _sent = project;
    }

    /// <summary>True after a Rust panic: every later call throws.</summary>
    public bool IsPoisoned => _native.IsPoisoned;

    /// <summary>How many <see cref="SetProject"/> calls sent only changed scenes (tests).</summary>
    public int SceneUpdates { get; private set; }

    /// <summary>How many <see cref="SetProject"/> calls sent the whole project (tests).</summary>
    public int ProjectUpdates { get; private set; }

    /// <summary>Creates a preview of a (migrated) project.</summary>
    public static RustPreview Create(GameProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        FarmFfi.EnsureAvailable();
        var json = RecordJson.ToUtf8(project);
        fixed (byte* ptr = json)
        {
            NativeMethods.FeBytes error = default;
            var result = NativeMethods.fe_preview_new(ptr, (nuint)json.Length, out var handle, &error);
            var message = Encoding.UTF8.GetString(RustRender.TakeBytes(error));
            if (result != NativeMethods.FeResult.Ok)
            {
                handle.Dispose();
                throw new FarmFfiException($"fe_preview_new failed: {result}. {message}");
            }

            return new RustPreview(handle, project);
        }
    }

    /// <summary>
    /// Replaces the previewed project (after an edit); decoded images stay cached. An edit that
    /// changed only scenes (a paint stroke) sends just those scenes, not the whole project and its
    /// art: the editor's records share every part an edit did not touch, so the parts are compared
    /// by reference.
    /// </summary>
    public void SetProject(GameProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (ReferenceEquals(project, _sent))
        {
            return;
        }

        if (ChangedScenes(_sent, project) is { } scenes)
        {
            try
            {
                if (scenes.Count > 0)
                {
                    _native.Call(nameof(SetProject), ScenesJson(scenes), NativeMethods.fe_preview_set_scenes);
                }

                _sent = project;
                SceneUpdates++;
                return;
            }
            catch (FarmFfiException) when (!IsPoisoned)
            {
                // Rust refused the scenes (it never saw one of them): send the project.
            }
        }

        _native.Call(nameof(SetProject), RecordJson.ToUtf8(project), NativeMethods.fe_preview_set_project);
        _sent = project;
        ProjectUpdates++;
    }

    /// <summary>
    /// The scenes of <paramref name="now"/> that are not the same objects as in
    /// <paramref name="before"/>, when nothing else changed (same fields, same scene ids in the
    /// same order); null when the whole project must be sent.
    /// </summary>
    internal static List<Scene>? ChangedScenes(GameProject before, GameProject now)
    {
        foreach (var field in ProjectFields)
        {
            var a = field.GetValue(before);
            var b = field.GetValue(now);
            if (!ReferenceEquals(a, b) && !(a is ValueType or string && Equals(a, b)))
            {
                return null;
            }
        }

        var old = before.Scenes.ToList();
        var current = now.Scenes.ToList();
        if (old.Count != current.Count)
        {
            return null;
        }

        var changed = new List<Scene>();
        for (var i = 0; i < current.Count; i++)
        {
            if (old[i].Id != current[i].Id)
            {
                return null;
            }

            if (!ReferenceEquals(old[i], current[i]))
            {
                changed.Add(current[i]);
            }
        }

        return changed;
    }

    private static byte[] ScenesJson(List<Scene> scenes)
    {
        using var buffer = new MemoryStream();
        buffer.WriteByte((byte)'[');
        for (var i = 0; i < scenes.Count; i++)
        {
            if (i > 0)
            {
                buffer.WriteByte((byte)',');
            }

            buffer.Write(RecordJson.ToUtf8(scenes[i]));
        }

        buffer.WriteByte((byte)']');
        return buffer.ToArray();
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
        return _native.Read(nameof(Render), request, NativeMethods.fe_preview_render, ReadFrame);
    }

    /// <summary>
    /// One frame of a visual binding of the project, resolved as the game resolves it (clip and
    /// frame at <paramref name="tick"/>, facing <paramref name="direction"/>), fitted into a
    /// <paramref name="size"/>-pixel square. A 0×0 frame when the binding resolves to nothing.
    /// </summary>
    public PreviewFrame RenderVisual(VisualRef? visual, double tick, double size, double scale = 1, string direction = "down", bool moving = true)
    {
        var request = JsonSerializer.SerializeToUtf8Bytes(new { visual, tick, size, scale, direction, moving }, InteropJson.Options);
        return _native.Read(nameof(RenderVisual), request, NativeMethods.fe_preview_render_visual, ReadFrame);
    }

    public void Dispose() => _native.Dispose();

    /// <summary>Width and height, then the pixels: copied once, straight out of the Rust buffer.</summary>
    private static PreviewFrame ReadFrame(ReadOnlySpan<byte> bytes)
    {
        var width = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[..4]);
        var height = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..8]);
        return new PreviewFrame(width, height, bytes[8..].ToArray());
    }
}
