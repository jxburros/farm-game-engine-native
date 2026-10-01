using System.Buffers.Binary;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FarmEngine.Authoring;
using FarmEngine.Authoring.Net;
using FarmEngine.Export;
using FarmEngine.Schemas;
using FarmingRpgMaker.App.Game;
using FarmingRpgMaker.App.Projects;
using FarmingRpgMaker.App.ViewModels;
using SkiaSharp;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

/// <summary>
/// Data the editor did not import itself: images inside projects and packs (size limits before
/// decoding), picked files (size caps), pack art, and an Export Game dialog closed mid-export.
/// </summary>
public sealed class ImportSafetyTests
{
    /// <summary>A PNG whose header claims <paramref name="width"/>×<paramref name="height"/> over a one-byte body.</summary>
    internal static byte[] BombPng(int width, int height)
    {
        static byte[] Chunk(string kind, byte[] data)
        {
            var body = Encoding.ASCII.GetBytes(kind).Concat(data).ToArray();
            var chunk = new byte[12 + data.Length];
            BinaryPrimitives.WriteInt32BigEndian(chunk, data.Length);
            body.CopyTo(chunk, 4);
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc32(body));
            return chunk;
        }

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 6;
        return [.. new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 },
            .. Chunk("IHDR", header), .. Chunk("IDAT", [0x78, 0x9C, 3, 0, 0, 0, 0, 1]), .. Chunk("IEND", [])];
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.OrangeRed);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static string DataUrl(byte[] png) => "data:image/png;base64," + Convert.ToBase64String(png);

    [AvaloniaFact]
    public void ImagesOverTheImportLimitsAreRefusedFromTheirHeader()
    {
        var bomb = BombPng(60000, 60000);
        Assert.True(bomb.Length < 100);
        Assert.Equal((60000, 60000), ArtBitmaps.HeaderSize(bomb));
        Assert.Null(ArtBitmaps.Decode(DataUrl(bomb)));
        Assert.Null(ArtBitmaps.Thumbnail(DataUrl(bomb), 64));
        // An asset whose stored size understates the image: the header decides.
        var lying = CustomAsset.Default.WithId("art-lie").WithDataUrl(DataUrl(bomb)).WithWidth(16).WithHeight(16);
        Assert.Null(ArtBitmaps.Thumbnail(lying, 64));
        Assert.False(ArtBitmaps.WithinLimits(8193, 1));
        Assert.False(ArtBitmaps.WithinLimits(5000, 5000));
        Assert.True(ArtBitmaps.WithinLimits(4096, 4096));

        // A real image whose stored size is wrong still gets a thumbnail of the right size.
        var real = CustomAsset.Default.WithId("art-real").WithDataUrl(DataUrl(Png(200, 100))).WithWidth(1).WithHeight(1);
        using var thumbnail = ArtBitmaps.Thumbnail(real, 64);
        Assert.NotNull(thumbnail);
        Assert.Equal(64, thumbnail!.PixelSize.Width);
    }

    [AvaloniaFact]
    public async Task PickedFilesAreReadUpToACap()
    {
        using var small = new MemoryStream("﻿{ \"ok\": true }"u8.ToArray());
        Assert.Equal("{ \"ok\": true }", (await PickedFiles.ReadTextAsync(small, 1024, "a.json", "project files")).TrimStart('﻿'));
        using var large = new MemoryStream(new byte[4096]);
        var error = await Assert.ThrowsAsync<FileTooLargeException>(() => PickedFiles.ReadTextAsync(large, 1024, "big.json", "project files"));
        Assert.Contains("big.json", error.Message, StringComparison.Ordinal);
        // A stream that can't tell its length is cut off while reading.
        using var unseekable = new UnseekableStream(new byte[4096]);
        await Assert.ThrowsAsync<FileTooLargeException>(() => PickedFiles.ReadTextAsync(unseekable, 1024, "big.json", "content packs"));
        Assert.Equal(256L * 1024 * 1024, PickedFiles.MaxProjectBytes);
        Assert.Equal(64L * 1024 * 1024, PickedFiles.MaxPackBytes);
    }

    private sealed class UnseekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }

    [AvaloniaFact]
    public void ExportedPacksCarryTheirArtAndInstallingAddsIt()
    {
        using var host = new GameTestHost();
        var project = host.Workspace.Current!;
        var asset = CustomAsset.Default.WithId("art-hoe").WithName("hoe.png").WithType(CustomAssetTypes.Art).WithDataUrl(DataUrl(Png(16, 16))).WithWidth(16).WithHeight(16);
        var item = project.Items[0];
        host.Workspace.Apply(Edits.UpsertAsset(asset));
        host.Workspace.Apply(Edits.UpsertItem(item.WithVisual(VisualRef.Default.WithAssetId("art-hoe"))));
        FindByName<TabControl>(host.Window, "EditorTabs").SelectedIndex = 4;
        Pump();
        var mods = FindByName<ModsEditorView>(host.Window, "ModsEditorView");
        var result = mods.BuildSelectedPack();
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        Assert.Equal(["art-hoe"], result.Assets.Select(a => a.Id));
        Assert.Equal("Saved p.json with 1 image (the art its entries use).", ModsEditorView.ExportedText("p.json", result));

        // Another project: the review lists the art, and installing adds it.
        using var other = new GameTestHost();
        FindByName<TabControl>(other.Window, "EditorTabs").SelectedIndex = 4;
        Pump();
        var otherMods = FindByName<ModsEditorView>(other.Window, "ModsEditorView");
        otherMods.ReviewPackJson(result.Text);
        Assert.Contains("Art: 1 image", FindByName<TextBlock>(other.Window, "PackReviewArt").Text, StringComparison.Ordinal);
        FindByName<Button>(other.Window, "InstallPackButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Contains(other.Workspace.Current!.CustomAssets, a => a.Id == "art-hoe");
    }

    [AvaloniaFact]
    public void ClosingTheExportDialogMidExportWaitsForTheResult()
    {
        using var host = new GameTestHost();
        using var dir = new TempDir();
        var templates = Path.Combine(dir.Path, "templates");
        var folder = Directory.CreateDirectory(Path.Combine(templates, "linux-x64")).FullName;
        File.WriteAllText(Path.Combine(folder, "template.json"), "{ \"target\": \"linux-x64\" }");
        var viewModel = new ExportGameViewModel(host.Workspace, _ => Task.FromResult<string?>(null), host.Launcher, templates)
        {
            OutputFolder = Path.Combine(dir.Path, "out"),
            ExportWindows = false,
            ExportWeb = false,
            ExportLinux = true,
        };
        var dialog = new ExportGameWindow(viewModel);
        dialog.Show(host.Window);
        Pump();
        viewModel.ExportCommand.Execute(null);
        Assert.True(viewModel.IsExporting);
        Assert.Equal("Cancel", FindByName<Button>(dialog, "ExportCloseButton").Content);
        var closed = false;
        dialog.Closed += (_, _) => closed = true;
        dialog.Close();
        // The window stays until the export has stopped, then closes with its result kept.
        Assert.False(closed);
        Assert.True(viewModel.IsCancelling || !viewModel.IsExporting);
        PumpUntil(() => closed, "dialog closed after the export stopped");
        Assert.False(viewModel.IsExporting);
        Assert.NotNull(viewModel.Report);
        Assert.False(viewModel.ExportCommand.IsRunning);
    }
}
