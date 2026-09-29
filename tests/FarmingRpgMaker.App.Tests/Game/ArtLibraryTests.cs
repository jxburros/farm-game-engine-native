using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using FarmEngine.Authoring;
using FarmEngine.Authoring.Net;
using FarmingRpgMaker.App.Game;
using SkiaSharp;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

/// <summary>The art studio's batch import and "Remove unused art".</summary>
public sealed class ArtLibraryTests
{
    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.SeaGreen);
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

    [AvaloniaFact]
    public void SeveralImagesImportAsOneUndoStepAndBadFilesAreReported()
    {
        using var host = new GameTestHost();
        var art = OpenArt(host);
        art.ImportFiles([("a.png", Png(16, 16)), ("broken.png", [1, 2, 3]), ("b.png", Png(32, 16))]);
        var assets = host.Workspace.Current!.CustomAssets;
        Assert.Equal(["a.png", "b.png"], assets.Select(asset => asset.Name));
        Assert.Equal(2, assets.Select(asset => asset.Id).Distinct().Count());
        var message = FindByName<TextBlock>(host.Window, "ArtMessage").Text;
        Assert.Contains("Imported 2 images.", message);
        Assert.Contains("broken.png", message);
        Assert.Equal(assets[^1].Id, art.SelectedAssetId);

        host.Workspace.Undo();
        Assert.Empty(host.Workspace.Current!.CustomAssets);
    }

    [AvaloniaFact]
    public void RemoveUnusedArtConfirmsTheListThenRemovesItAsOneStep()
    {
        using var host = new GameTestHost();
        var art = OpenArt(host);
        Press(host.Window, "RemoveUnusedArtButton");
        Assert.Contains("nothing to remove", FindByName<TextBlock>(host.Window, "ArtMessage").Text);

        art.ImportFiles([("used.png", Png(16, 16)), ("spare.png", Png(16, 16)), ("extra.png", Png(8, 8))]);
        var used = host.Workspace.Current!.CustomAssets.First(asset => asset.Name == "used.png");
        host.Workspace.Apply(Edits.BindPlayerVisual(FarmEngine.Schemas.VisualRef.Default.WithAssetId(used.Id)));
        var before = host.Workspace.Current!;

        Press(host.Window, "RemoveUnusedArtButton");
        Pump();
        var confirm = FindByName<Border>(host.Window, "UnusedArtConfirm");
        Assert.True(confirm.IsVisible);
        var listed = AllVisibleText(confirm);
        Assert.Contains("spare.png", listed);
        Assert.Contains("extra.png", listed);
        Assert.DoesNotContain("used.png ·", listed);
        Press(confirm, "CancelRemoveUnusedArtButton");
        Assert.False(confirm.IsVisible);
        Assert.Same(before, host.Workspace.Current);

        Press(host.Window, "RemoveUnusedArtButton");
        Press(confirm, "ConfirmRemoveUnusedArtButton");
        Assert.Equal([used.Id], host.Workspace.Current!.CustomAssets.Select(asset => asset.Id));
        Assert.Equal("Removed 2 unused assets.", FindByName<TextBlock>(host.Window, "ArtMessage").Text);
        host.Workspace.Undo();
        Assert.Same(before, host.Workspace.Current);
    }
}
