using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FarmEngine.Authoring;
using FarmEngine.Authoring.Net;
using FarmEngine.Schemas;
using FarmingRpgMaker.App.Game;
using SkiaSharp;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

/// <summary>
/// The art studio's conveniences (web AssetManager.tsx): clicking sprite sheet cells into a clip,
/// list thumbnails, pausing the preview, and one animation from frames of several images.
/// </summary>
public sealed class ArtStudioTests
{
    /// <summary>A PNG whose left half is <paramref name="left"/> and right half <paramref name="right"/>.</summary>
    private static byte[] Png(int width, int height, SKColor left, SKColor? right = null)
    {
        using var bitmap = new SKBitmap(width, height);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                bitmap.SetPixel(x, y, x < width / 2 ? left : right ?? left);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static ArtEditorView OpenArt(GameTestHost host)
    {
        FindByName<TabControl>(host.Window, "EditorTabs").SelectedIndex = 5;
        Pump();
        return FindByName<ArtEditorView>(host.Window, "ArtEditorView");
    }

    private static void Press(Control root, string name) =>
        FindByName<Button>(root, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static string Message(GameTestHost host) => FindByName<TextBlock>(host.Window, "ArtMessage").Text ?? "";

    private static CustomAsset Asset(GameTestHost host, string name) => host.Workspace.Current!.CustomAssets.Single(asset => asset.Name == name);

    private static AnimationClip Clip(GameTestHost host, string asset, string clip) =>
        Asset(host, asset).Animations.OrEmpty().Single(existing => existing.Name == clip);

    /// <summary>The colour at the centre of the animation preview the Rust renderer drew.</summary>
    private static (byte R, byte G, byte B) PreviewCentre(GameTestHost host)
    {
        var image = Assert.IsType<Image>(FindByName<Border>(host.Window, "ArtPreview").Child);
        var bitmap = Assert.IsType<WriteableBitmap>(image.Source);
        using var buffer = bitmap.Lock();
        var offset = (bitmap.PixelSize.Height / 2 * buffer.RowBytes) + (bitmap.PixelSize.Width / 2 * 4);
        var pixel = new byte[4];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address + offset, pixel, 0, 4);
        return (pixel[0], pixel[1], pixel[2]);
    }

    [AvaloniaFact]
    public void ClickingASheetCellAppendsItAsAFrame()
    {
        using var host = new GameTestHost();
        var art = OpenArt(host);
        art.ImportBytes("sheet.png", Png(32, 16, SKColors.Red, SKColors.Blue));
        Assert.Equal("Imported sheet.png (32×16).", Message(host));
        var sheet = FindByName<SpriteSheetView>(host.Window, "ArtSheet");
        Assert.Equal("Sprite sheet: click a cell to add it as a frame", AutomationProperties.GetName(sheet));
        Assert.NotNull(sheet.Image);
        Assert.Equal(new Avalonia.PixelSize(32, 16), sheet.Image!.PixelSize);
        Assert.Equal(4, sheet.Zoom);

        FindByName<TextBox>(host.Window, "ArtClipName").Text = "walk";
        FindByName<TextBox>(host.Window, "ArtFrameTicks").Text = "4";
        var before = host.Workspace.Current!;
        art.AppendFrameAt(20.5, 3);
        var frame = Assert.Single(Clip(host, "sheet.png", "walk").Frames);
        Assert.Equal((16.0, 0.0, 16.0, 16.0, 4.0), (frame.X, frame.Y, frame.Width, frame.Height, frame.Ticks));
        Assert.Null(frame.AssetId);
        Assert.Equal("Added frame 1 to walk.", Message(host));

        // A real click on the sheet: its centre is image pixel (16, 8) at 4× zoom.
        sheet.BringIntoView();
        Pump();
        Click(host.Window, sheet);
        Assert.Equal([16.0, 16.0], Clip(host, "sheet.png", "walk").Frames.Select(f => f.X));

        // The same clip grows one frame per click; each click is one undo step.
        art.AppendFrameAt(0, 15);
        Assert.Equal([16.0, 16.0, 0.0], Clip(host, "sheet.png", "walk").Frames.Select(f => f.X));
        host.Workspace.Undo();
        Assert.Equal(2, Clip(host, "sheet.png", "walk").Frames.Length);

        var current = host.Workspace.Current;
        art.AppendFrameAt(20, 17);
        Assert.Equal("That frame extends beyond the image.", Message(host));
        FindByName<TextBox>(host.Window, "ArtFrameWidth").Text = "24";
        art.AppendFrameAt(30, 0);
        Assert.Equal("That frame extends beyond the image.", Message(host));
        art.AppendFrameAt(-1, 0);
        Assert.Equal("That frame extends beyond the image.", Message(host));
        Assert.Same(current, host.Workspace.Current);

        // The X/Y boxes stay for precise entry, with the same bounds check.
        FindByName<TextBox>(host.Window, "ArtFrameWidth").Text = "16";
        FindByName<TextBox>(host.Window, "ArtFrameX").Text = "24";
        Press(host.Window, "AppendArtFrameButton");
        Assert.Equal("That frame extends beyond the image.", Message(host));
        FindByName<TextBox>(host.Window, "ArtFrameX").Text = "8";
        Press(host.Window, "AppendArtFrameButton");
        Assert.Equal([16.0, 16.0, 8.0], Clip(host, "sheet.png", "walk").Frames.Select(f => f.X));

        host.Workspace.Undo();
        host.Workspace.Undo();
        host.Workspace.Undo();
        Assert.Empty(Asset(host, "sheet.png").Animations.OrEmpty());
        Assert.Same(before, host.Workspace.Current);
        host.Workspace.Undo();
        Assert.Empty(host.Workspace.Current!.CustomAssets);
    }

    [AvaloniaFact]
    public void TheAssetListShowsThumbnailsWithNameAndSize()
    {
        using var host = new GameTestHost();
        var art = OpenArt(host);
        art.ImportFiles([("a.png", Png(16, 16, SKColors.Red)), ("wide.png", Png(128, 32, SKColors.Blue))]);
        var items = FindByName<ListBox>(host.Window, "ArtAssets").Items.OfType<ListBoxItem>().ToList();
        Assert.Equal(host.Workspace.Current!.CustomAssets.Select(asset => asset.Id), items.Select(item => item.Tag));
        Assert.Equal(["a.png · 16×16", "wide.png · 128×32"], items.Select(item => AutomationProperties.GetName(item)));

        var row = Assert.IsType<StackPanel>(items[1].Content);
        var thumbnail = Assert.IsType<Image>(row.Children[0]);
        Assert.Equal(32, thumbnail.Width);
        // Large art is decoded small (64px on its longest side) for the list.
        Assert.Equal(64, Assert.IsAssignableFrom<Bitmap>(thumbnail.Source).PixelSize.Width);
        Assert.Equal(BitmapInterpolationMode.None, RenderOptions.GetBitmapInterpolationMode(thumbnail));
        Assert.Equal("wide.png · 128×32", Assert.IsType<TextBlock>(row.Children[1]).Text);

        // Smooth art (Crisp pixel art off) gets smooth thumbnails.
        var pixelArt = FindByName<CheckBox>(host.Window, "ArtPixelArt");
        pixelArt.IsChecked = false;
        pixelArt.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(host.Workspace.Current!.Graphics.OrNull()?.PixelArt);
        items = FindByName<ListBox>(host.Window, "ArtAssets").Items.OfType<ListBoxItem>().ToList();
        var smooth = Assert.IsType<Image>(Assert.IsType<StackPanel>(items[0].Content).Children[0]);
        Assert.Equal(BitmapInterpolationMode.HighQuality, RenderOptions.GetBitmapInterpolationMode(smooth));
        Assert.NotNull(smooth.Source);
    }

    [AvaloniaFact]
    public void PausingThePreviewHoldsItsFrameAndPlayResumes()
    {
        using var host = new GameTestHost();
        var art = OpenArt(host);
        art.ImportBytes("sheet.png", Png(32, 16, SKColors.Red, SKColors.Blue));
        Press(host.Window, "SliceArtButton");
        var play = FindByName<Button>(host.Window, "ArtPreviewPlayButton");
        Assert.Equal("Pause preview", play.Content);
        Assert.True(art.PreviewPlaying);
        var tick = art.PreviewTick;
        art.AdvancePreview();
        Assert.True(art.PreviewTick > tick);

        Press(host.Window, "ArtPreviewPlayButton");
        Assert.False(art.PreviewPlaying);
        Assert.Equal("Play preview", play.Content);
        tick = art.PreviewTick;
        var shown = FindByName<Border>(host.Window, "ArtPreview").Child;
        var colour = PreviewCentre(host);
        for (var i = 0; i < 20; i++) art.AdvancePreview();
        Pump();
        Assert.Equal(tick, art.PreviewTick);
        Assert.Same(shown, FindByName<Border>(host.Window, "ArtPreview").Child);

        // Redrawing (any project change) keeps the paused frame: frames 0–5 are red, 6–11 blue.
        art.Refresh();
        Assert.NotSame(shown, FindByName<Border>(host.Window, "ArtPreview").Child);
        Assert.Equal(colour, PreviewCentre(host));
        var red = tick % 12 < 6;
        Assert.Equal(red ? (byte)255 : (byte)0, colour.R);
        Assert.Equal(red ? (byte)0 : (byte)255, colour.B);

        Press(host.Window, "ArtPreviewPlayButton");
        Assert.True(art.PreviewPlaying);
        Assert.Equal("Pause preview", play.Content);
        art.AdvancePreview();
        Assert.True(art.PreviewTick > tick);
    }

    [AvaloniaFact]
    public void OneAnimationCanUseFramesOfSeveralImages()
    {
        using var host = new GameTestHost();
        var art = OpenArt(host);
        art.ImportFiles([("sheet.png", Png(32, 16, SKColors.Red)), ("Carrot.png", Png(16, 16, SKColors.Blue))]);
        var sheet = Asset(host, "sheet.png");
        var carrot = Asset(host, "Carrot.png");
        art.SelectAsset(sheet.Id);
        FindByName<TextBox>(host.Window, "ArtClipName").Text = "grow";
        art.AppendFrameAt(0, 0);

        var add = FindByName<Button>(host.Window, "ArtAddImageFrameButton");
        Assert.False(add.IsEnabled);
        var source = FindByName<ComboBox>(host.Window, "ArtFrameSource");
        Assert.Equal(["sheet.png", "Carrot.png"], source.Items.OfType<ComboBoxItem>().Select(item => item.Content));
        source.SelectedItem = source.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, carrot.Id));
        Assert.True(add.IsEnabled);
        Press(host.Window, "ArtAddImageFrameButton");
        var frames = Clip(host, "sheet.png", "grow").Frames;
        Assert.Equal(2, frames.Length);
        Assert.Null(frames[0].AssetId);
        Assert.Equal((carrot.Id, 0.0, 0.0, 16.0, 16.0, 6.0), (frames[1].AssetId.OrNull(), frames[1].X, frames[1].Y, frames[1].Width, frames[1].Height, frames[1].Ticks));
        Assert.Equal("1. 0,0 · 16×16", FindByName<TextBlock>(host.Window, "ArtFrameLabel_0").Text);
        Assert.Equal("2. from Carrot.png · 16×16", FindByName<TextBlock>(host.Window, "ArtFrameLabel_1").Text);
        Assert.Empty(ProblemsFor(host, "graphics."));

        // The preview draws the second frame from the carrot image (ticks 6–11 of each 12).
        for (var i = 0; i < 12 && art.PreviewTick % 12 < 6; i++) art.AdvancePreview();
        Press(host.Window, "ArtPreviewPlayButton");
        art.Refresh();
        var carrotShown = art.PreviewTick % 12 >= 6;
        Assert.Equal(carrotShown ? (byte)0 : (byte)255, PreviewCentre(host).R);
        Assert.Equal(carrotShown ? (byte)255 : (byte)0, PreviewCentre(host).B);

        host.Workspace.Undo();
        Assert.Single(Clip(host, "sheet.png", "grow").Frames);

        // Legacy art without a recorded size cannot become a whole-image frame.
        host.Workspace.Apply(Edits.UpsertAsset(carrot.WithId("legacy").WithName("old.png").WithWidth(null).WithHeight(null)));
        source.SelectedItem = source.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, "legacy"));
        var current = host.Workspace.Current;
        Press(host.Window, "ArtAddImageFrameButton");
        Assert.Equal("Reimport this art to read its size.", Message(host));
        Assert.Same(current, host.Workspace.Current);
    }

    private static List<string> ProblemsFor(GameTestHost host, string codePrefix) =>
        [.. Problems.Collect(host.Workspace.Current!).Where(problem => problem.Code.StartsWith(codePrefix, StringComparison.Ordinal)).Select(problem => problem.Message)];
}
