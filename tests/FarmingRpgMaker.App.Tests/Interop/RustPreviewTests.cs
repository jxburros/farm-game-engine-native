using FarmEngine.Authoring;
using FarmEngine.Authoring.Net;
using FarmEngine.Interop;
using FarmingRpgMaker.App;

namespace FarmingRpgMaker.App.Tests.Interop;

/// <summary>
/// Edit Mode's map preview and the stateless render requests (<c>fe_preview_*</c>,
/// <c>fe_render_json</c>). The pixels themselves are tested in <c>crates/farm-render</c>.
/// </summary>
public sealed class RustPreviewTests
{
    [Fact]
    public void PreviewRendersTheRequestedViewport()
    {
        if (!FarmFfi.IsAvailable)
        {
            return;
        }

        var project = ProjectCatalog.CreateInitialProject(0);
        var scene = project.Scenes.First(s => s.Id == project.Player.SceneId);
        using var preview = RustPreview.Create(project);
        var camera = new PreviewCamera(40, 30, 200.5, 120);
        var frame = preview.Render(scene.Id, 28, 12, camera, 2);
        Assert.Equal((401, 240), (frame.Width, frame.Height));
        Assert.Equal(401 * 240 * 4, frame.Pixels.Length);
        Assert.True(frame.Pixels.Distinct().Count() > 16, "the viewport shows the map");

        // Without a camera the whole scene is drawn: tiles with one-pixel seams, plus padding.
        var whole = preview.Render(scene.Id, 28, 12);
        Assert.Equal(((int)Math.Ceiling(24 + (scene.Width * 29) - 1), (int)Math.Ceiling(24 + (scene.Height * 29) - 1)), (whole.Width, whole.Height));

        // A new project replaces the old one.
        var flooded = scene.WithTiles(scene.Tiles.Select(row => row.Select(t => t.WithType("water").WithBackground("water").WithOverlay(null).WithObject(null).WithVisuals(null)).ToFSharpList()).ToList());
        preview.SetProject(project.WithScenes([.. project.Scenes.Select(s => s.Id == scene.Id ? flooded : s)]));
        var water = preview.Render(scene.Id, 28, 12, camera, 2);
        Assert.Equal((frame.Width, frame.Height), (water.Width, water.Height));
        Assert.False(frame.Pixels.AsSpan().SequenceEqual(water.Pixels));
        Assert.Throws<FarmFfiException>(() => preview.Render("missing-scene"));
        Assert.False(preview.IsPoisoned);

        // A visual binding that resolves to no art draws nothing (the art studio shows a placeholder).
        var missing = preview.RenderVisual(FarmEngine.Schemas.VisualRef.Default.WithAssetId("no-such-asset"), tick: 0, size: 32);
        Assert.Equal((0, 0), (missing.Width, missing.Height));
        var unbound = preview.RenderVisual(null, 0, 32);
        Assert.Equal((0, 0), (unbound.Width, unbound.Height));
    }

    [Fact]
    public void BadRequestsThrowWithTheRustMessage()
    {
        if (!FarmFfi.IsAvailable)
        {
            return;
        }

        var error = Assert.Throws<FarmFfiException>(() => RustRender.EditorSnapshotJson(ProjectCatalog.CreateBlankProject(0), "missing-scene"));
        Assert.Contains("Scene missing-scene not found.", error.Message, StringComparison.Ordinal);
        Assert.Throws<FarmFfiException>(() => RustRender.RasterizePng("{}", 0));
    }
}
