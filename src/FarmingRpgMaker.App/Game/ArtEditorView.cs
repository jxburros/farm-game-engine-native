using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FarmEngine.Authoring;
using FarmEngine.Authoring.Net;
using FarmEngine.Schemas;
using FarmingRpgMaker.App.Projects;

namespace FarmingRpgMaker.App.Game;

/// <summary>Import, animate and bind creator art through F# project edits.</summary>
public sealed class ArtEditorView : UserControl, IRetirable
{
    private static readonly FilePickerFileType ArtFiles = new("Artwork") { Patterns = ArtImport.FilePatterns };
    private readonly ProjectWorkspace _workspace;
    private readonly Action<string, VisualRef> _useMapBrush;
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly ListBox _assets = new() { Name = "ArtAssets", MinHeight = 130 };
    private readonly Border _preview = new() { Name = "ArtPreview", Width = 140, Height = 140 };
    private readonly VisualPreview _art = new();
    private readonly TextBlock _message = Ui.Wrapped("Import an image to start.", "muted", "small");
    private readonly TextBox _assetName = new() { Name = "ArtAssetName" };
    private readonly ComboBox _clips = new() { Name = "ArtClips" };
    private readonly TextBox _clipName = new() { Name = "ArtClipName", Text = "idle" };
    private readonly TextBox _frameWidth = new() { Name = "ArtFrameWidth", Text = "16", Width = 58 };
    private readonly TextBox _frameHeight = new() { Name = "ArtFrameHeight", Text = "16", Width = 58 };
    private readonly TextBox _frameX = new() { Name = "ArtFrameX", Text = "0", Width = 58 };
    private readonly TextBox _frameY = new() { Name = "ArtFrameY", Text = "0", Width = 58 };
    private readonly TextBox _frameTicks = new() { Name = "ArtFrameTicks", Text = "6", Width = 58 };
    private readonly CheckBox _loop = new() { Name = "ArtLoop", Content = "Loop", IsChecked = true };
    private readonly StackPanel _frames = new() { Name = "ArtFrames", Spacing = 4 };
    private readonly SpriteSheetView _sheet = new();
    private readonly ComboBox _frameSource = new() { Name = "ArtFrameSource", MinWidth = 160, PlaceholderText = "Choose image" };
    private readonly Button _addImageFrame;
    private readonly Button _play;
    private readonly ComboBox _target = new() { Name = "ArtTarget" };
    private readonly ComboBox _bindingAsset = new() { Name = "ArtBindingAsset" };
    private readonly ComboBox _bindingClip = new() { Name = "ArtBindingClip" };
    private readonly ComboBox _tileType = new() { Name = "ArtTileType" };
    private readonly CheckBox _singleCell = new() { Name = "ArtSingleCell", Content = "Use one cell" };
    private readonly TextBox _cellSize = new() { Name = "ArtCellSize", Text = "16", Width = 55 };
    private readonly TextBox _cellColumn = new() { Name = "ArtCellColumn", Text = "0", Width = 55 };
    private readonly TextBox _cellRow = new() { Name = "ArtCellRow", Text = "0", Width = 55 };
    private readonly CheckBox _pixelArt = new() { Name = "ArtPixelArt", Content = "Crisp pixel art" };
    private readonly TextBox _svgSize = new() { Name = "ArtSvgSize", Width = 70, Watermark = "own" };
    private readonly Border _unusedConfirm = new() { Name = "UnusedArtConfirm", IsVisible = false };
    /// <summary>Asset list thumbnails by asset id, with the data URL they were decoded from.</summary>
    private readonly Dictionary<string, (string DataUrl, Bitmap? Bitmap)> _thumbnails = [];
    /// <summary>The asset (and data URL) the sprite sheet shows.</summary>
    private (string AssetId, string DataUrl)? _sheetKey;
    private string? _selectedAssetId;
    private bool _refreshing;
    private bool _playing = true;
    private double _tick;

    public ArtEditorView(ProjectWorkspace workspace, Action<string, VisualRef> useMapBrush)
    {
        _workspace = workspace;
        _useMapBrush = useMapBrush;
        Ui.Label((_assets, "Artwork"), (_svgSize, "SVG size (longest side, px)"), (_assetName, "Asset name"), (_clips, "Animation clip"),
            (_clipName, "Clip name"), (_frameWidth, "Frame width"), (_frameHeight, "Frame height"), (_frameX, "Frame X"), (_frameY, "Frame Y"),
            (_frameTicks, "Ticks per frame"), (_target, "Assign to"), (_bindingAsset, "Artwork to assign"), (_bindingClip, "Clip to assign"),
            (_cellSize, "Cell size"), (_cellColumn, "Cell column"), (_cellRow, "Cell row"), (_tileType, "Map tile behavior"));
        Name = "ArtEditorView";
        _message.Name = "ArtMessage";
        _previewTimer.Tick += (_, _) => AdvancePreview();
        _sheet.CellClicked += AppendFrameAt;
        _frameWidth.TextChanged += (_, _) => RefreshSheetGrid();
        _frameHeight.TextChanged += (_, _) => RefreshSheetGrid();
        _assets.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            _selectedAssetId = (_assets.SelectedItem as ListBoxItem)?.Tag as string;
            RefreshSelected();
        };
        _clips.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            var clip = SelectedClip();
            if (clip is null) return;
            _clipName.Text = clip.Name;
            _loop.IsChecked = clip.Loop;
            if (clip.Frames.FirstOrDefault() is { } frame)
            {
                _frameWidth.Text = Number(frame.Width);
                _frameHeight.Text = Number(frame.Height);
                _frameTicks.Text = Number(frame.Ticks);
            }
            RefreshFrames();
            DrawPreview();
            UpdatePreviewTimer();
        };
        _bindingAsset.SelectionChanged += (_, _) => RefreshBindingClips();

        var left = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 20, 0) };
        left.Children.Add(Ui.Text("ARTWORK", "section"));
        left.Children.Add(Ui.Wrapped("Import PNG, JPEG, WebP, GIF, BMP or SVG. Animated images and SVGs become a still PNG; add clips below.", "muted", "small"));
        var import = Ui.AsyncButton("Import images", PickImagesAsync, error => _message.Text = $"Could not import: {error.Message}", "accent");
        import.Name = "ImportArtButton";
        ToolTip.SetTip(import, "Choose one or more images; they are added as one undo step.");
        left.Children.Add(import);
        var svgSize = Ui.HStack(6, Ui.Text("SVG size (longest side, px)", "muted", "small"), _svgSize);
        ToolTip.SetTip(svgSize, "Leave empty to use the SVG's own size.");
        left.Children.Add(svgSize);
        left.Children.Add(_assets);
        var removeUnused = Ui.Button("Remove unused art…", ConfirmRemoveUnused, "tool");
        removeUnused.Name = "RemoveUnusedArtButton";
        ToolTip.SetTip(removeUnused, "Delete every asset nothing in the game uses");
        left.Children.Add(removeUnused);
        left.Children.Add(_unusedConfirm);
        left.Children.Add(_pixelArt);
        _pixelArt.Click += (_, _) => _workspace.Apply(Edits.SetGraphics(GraphicsSettings.Default.WithPixelArt(_pixelArt.IsChecked == true)));
        left.Children.Add(_message);

        var right = new StackPanel { Spacing = 10 };
        right.Children.Add(_preview);
        _play = Ui.Button("Pause preview", TogglePreview, "tool", "small");
        _play.Name = "ArtPreviewPlayButton";
        _play.HorizontalAlignment = HorizontalAlignment.Center;
        right.Children.Add(_play);
        right.Children.Add(Ui.Text("Asset name", "muted", "small"));
        right.Children.Add(_assetName);
        var rename = Ui.Button("Rename", RenameAsset, "tool");
        rename.Name = "RenameArtButton";
        var remove = Ui.Button("Remove asset", RemoveAsset, "tool");
        remove.Name = "RemoveArtButton";
        right.Children.Add(Ui.HStack(8, rename, remove));
        right.Children.Add(Ui.Text("ANIMATION CLIPS", "section"));
        right.Children.Add(_clips);
        right.Children.Add(Ui.HStack(8, Ui.Text("Name", "muted", "small"), _clipName, _loop));
        right.Children.Add(Ui.HStack(8, Ui.Text("Frame", "muted", "small"), _frameWidth, Ui.Text("×"), _frameHeight,
            Ui.Text("at", "muted", "small"), _frameX, Ui.Text(","), _frameY, Ui.Text("ticks", "muted", "small"), _frameTicks));
        right.Children.Add(Ui.Wrapped("Click a cell of the sheet to add it to the clip, or type its position and use Append frame.", "muted", "small"));
        right.Children.Add(new ScrollViewer
        {
            Name = "ArtSheetScroll",
            Content = _sheet,
            MaxHeight = 300,
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        var slice = Ui.Button("Slice entire sheet", SliceSheet, "tool");
        slice.Name = "SliceArtButton";
        var append = Ui.Button("Append frame", AppendFrame, "tool");
        append.Name = "AppendArtFrameButton";
        var removeClip = Ui.Button("Remove clip", RemoveClip, "tool");
        right.Children.Add(Ui.HStack(8, slice, append, removeClip));
        _addImageFrame = Ui.Button("Add image frame", AddImageFrame, "tool");
        _addImageFrame.Name = "ArtAddImageFrameButton";
        _addImageFrame.IsEnabled = false;
        _frameSource.SelectionChanged += (_, _) => _addImageFrame.IsEnabled = _frameSource.SelectedItem is not null;
        ToolTip.SetTip(_addImageFrame, "Append the whole chosen image as the next frame: one animation can use frames of several images.");
        AutomationProperties.SetName(_frameSource, "Separate image to add as a frame");
        right.Children.Add(Ui.HStack(8, Ui.Text("Separate image", "muted", "small"), _frameSource, _addImageFrame));
        var allTicks = Ui.Button("Set every frame to these ticks", SetAllFrameTicks, "tool");
        allTicks.Name = "ArtAllFrameTicksButton";
        right.Children.Add(Ui.HStack(8, allTicks, Ui.Text("20 ticks = 1 second", "muted", "small")));
        right.Children.Add(_frames);
        right.Children.Add(Ui.Text("ASSIGN ARTWORK", "section"));
        right.Children.Add(Ui.HStack(8, Ui.Text("Target", "muted", "small"), _target));
        right.Children.Add(Ui.HStack(8, Ui.Text("Asset", "muted", "small"), _bindingAsset, Ui.Text("Clip", "muted", "small"), _bindingClip));
        _singleCell.Click += (_, _) => RefreshCellControls();
        right.Children.Add(Ui.HStack(8, _singleCell, Ui.Text("size", "muted", "small"), _cellSize,
            Ui.Text("column", "muted", "small"), _cellColumn, Ui.Text("row", "muted", "small"), _cellRow));
        foreach (var type in TileTypes.All) _tileType.Items.Add(new ComboBoxItem { Content = type, Tag = type });
        _tileType.SelectedIndex = 0;
        right.Children.Add(Ui.HStack(8, Ui.Text("Map tile behavior", "muted", "small"), _tileType));
        var bind = Ui.Button("Assign artwork", Bind, "accent");
        bind.Name = "BindArtButton";
        right.Children.Add(bind);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("250,*"), Margin = new Thickness(20) };
        grid.Children.Add(left);
        var scroll = new ScrollViewer { Content = right };
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(scroll);
        Content = grid;
        _workspace.ProjectChanged += OnProjectChanged;
        RefreshCellControls();
        Refresh();
    }

    /// <summary>Stops following the project (the editor that built this view was replaced).</summary>
    public void Retire() => _workspace.ProjectChanged -= OnProjectChanged;

    private void OnProjectChanged(object? sender, ProjectChangedEventArgs e)
    {
        if (IsEffectivelyVisible) Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdatePreviewTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _previewTimer.Stop();
        _art.Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    private static string Number(double value) => value.ToString("G", CultureInfo.InvariantCulture);
    /// <summary>"32×16", or "?×?" for legacy art imported without its size.</summary>
    private static string SizeText(CustomAsset asset) =>
        asset.Width.OrNullable() is { } width && asset.Height.OrNullable() is { } height ? $"{Number(width)}×{Number(height)}" : "?×?";
    private static int Positive(TextBox box) => int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : throw new FormatException($"{box.Name} must be a positive whole number.");
    private static int Nonnegative(TextBox box) => int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : throw new FormatException($"{box.Name} must be zero or more.");
    private CustomAsset? SelectedAsset() => _workspace.Current?.CustomAssets.FirstOrDefault(asset => asset.Id == _selectedAssetId);
    private AnimationClip? SelectedClip() => SelectedAsset()?.Animations.OrEmpty().FirstOrDefault(clip => clip.Name == (_clips.SelectedItem as ComboBoxItem)?.Tag as string);

    /// <summary>The asset being edited, or null.</summary>
    public string? SelectedAssetId => _selectedAssetId;

    /// <summary>Whether the animation preview is playing ("Pause preview" holds its current frame).</summary>
    public bool PreviewPlaying => _playing;

    /// <summary>The animation tick the preview shows.</summary>
    public double PreviewTick => _tick;

    /// <summary>Whether the preview timer runs (only while an animation with several frames plays on screen).</summary>
    internal bool PreviewTimerRunning => _previewTimer.IsEnabled;

    /// <summary>One step of the preview timer: the next tick is drawn unless the preview is paused or hidden.</summary>
    internal void AdvancePreview()
    {
        if (!_playing || !IsEffectivelyVisible) return;
        _tick++;
        DrawPreview();
    }

    /// <summary>
    /// Runs the preview timer only while it changes something: playing, on screen, and a clip
    /// with more than one frame selected. A still image is drawn once, not 20 times a second.
    /// </summary>
    private void UpdatePreviewTimer()
    {
        var animated = SelectedClip() is { } clip && clip.Frames.Length > 1;
        if (_playing && animated && TopLevel.GetTopLevel(this) is not null)
        {
            if (!_previewTimer.IsEnabled) _previewTimer.Start();
        }
        else
        {
            _previewTimer.Stop();
        }
    }

    private void TogglePreview()
    {
        _playing = !_playing;
        _play.Content = _playing ? "Pause preview" : "Play preview";
        UpdatePreviewTimer();
    }

    /// <summary>Opens an asset by id (Problems "Go to").</summary>
    public void SelectAsset(string assetId)
    {
        _selectedAssetId = assetId;
        Refresh();
    }

    public void ImportBytes(string fileName, byte[] bytes) => ImportFiles([(fileName, bytes)]);

    /// <summary>
    /// Imports several images as ONE undo step (web AssetManager multi-file upload). Files that
    /// fail are reported by name; the rest are still imported.
    /// </summary>
    public void ImportFiles(IReadOnlyList<(string FileName, byte[] Bytes)> files) => ImportFiles(files, []);

    private void ImportFiles(IReadOnlyList<(string FileName, byte[] Bytes)> files, IReadOnlyList<string> readErrors)
    {
        if (_workspace.Current is not { } project) return;
        var errors = new List<string>(readErrors);
        int? svgSide = null;
        if (!string.IsNullOrWhiteSpace(_svgSize.Text))
        {
            if (!int.TryParse(_svgSize.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var side) || side <= 0)
            {
                _message.Text = "The SVG size must be a positive whole number of pixels.";
                return;
            }

            svgSide = side;
        }

        var assets = new List<CustomAsset>();
        foreach (var (fileName, bytes) in files)
        {
            try
            {
                assets.Add(ArtImport.FromBytes(project, fileName, bytes, svgSide));
            }
            catch (ArgumentException error)
            {
                errors.Add($"{fileName}: {error.Message}");
            }
        }

        var imported = new List<CustomAsset>();
        if (assets.Count > 0)
        {
            var edit = ArtLibrary.Import(project, assets);
            _workspace.Apply(edit);
            var before = project.CustomAssets.Select(asset => asset.Id).ToHashSet();
            imported = _workspace.Current!.CustomAssets.Where(asset => !before.Contains(asset.Id)).ToList();
            if (imported.Count > 0)
            {
                _selectedAssetId = imported[^1].Id;
                Refresh();
            }
        }

        var summary = imported.Count switch
        {
            0 => "",
            1 => $"Imported {imported[0].Name} ({SizeText(imported[0])}).",
            _ => $"Imported {imported.Count} images.",
        };
        _message.Text = errors.Count == 0 ? summary : string.Join(" ", new[] { summary, $"Could not import {string.Join("; ", errors)}" }.Where(part => part.Length > 0));
    }

    private async Task PickImagesAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage) return;
        var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import artwork", AllowMultiple = true, FileTypeFilter = [ArtFiles, FilePickerFileTypes.All] });
        if (picked.Count == 0) return;
        var files = new List<(string, byte[])>();
        var errors = new List<string>();
        foreach (var file in picked)
        {
            try
            {
                await using var stream = await file.OpenReadAsync();
                using var memory = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(buffer)) > 0)
                {
                    if (memory.Length + read > ArtImport.MaxBytes) throw new ArgumentException("Each image must be under 16 MB.");
                    await memory.WriteAsync(buffer.AsMemory(0, read));
                }

                files.Add((file.Name, memory.ToArray()));
            }
            catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
            {
                errors.Add($"{file.Name}: {error.Message}");
            }
        }

        ImportFiles(files, errors);
    }

    /// <summary>Shows what "Remove unused art" would delete, with Remove and Cancel.</summary>
    public void ConfirmRemoveUnused()
    {
        if (_workspace.Current is not { } project) return;
        var unused = ArtLibrary.Unused(project);
        if (unused.Count == 0)
        {
            _unusedConfirm.IsVisible = false;
            _message.Text = "All art is in use; nothing to remove.";
            return;
        }

        var list = new StackPanel { Spacing = 2 };
        list.Children.Add(Ui.Wrapped($"Remove {unused.Count} unused asset{(unused.Count == 1 ? "" : "s")}? Nothing in the game uses {(unused.Count == 1 ? "it" : "them")}. Undo brings {(unused.Count == 1 ? "it" : "them")} back.", "small"));
        var names = new StackPanel { Name = "UnusedArtList", Spacing = 1 };
        foreach (var asset in unused) names.Children.Add(Ui.Text($"• {asset.Name} · {asset.Id}", "muted", "small"));
        list.Children.Add(new ScrollViewer { Content = names, MaxHeight = 160 });
        var confirm = Ui.Button($"Remove {unused.Count}", RemoveUnused, "accent");
        confirm.Name = "ConfirmRemoveUnusedArtButton";
        var cancel = Ui.Button("Cancel", () => _unusedConfirm.IsVisible = false, "tool");
        cancel.Name = "CancelRemoveUnusedArtButton";
        list.Children.Add(Ui.HStack(8, confirm, cancel));
        _unusedConfirm.Child = list;
        _unusedConfirm.IsVisible = true;
    }

    private void RemoveUnused()
    {
        _unusedConfirm.IsVisible = false;
        if (_workspace.Current is not { } project) return;
        var count = ArtLibrary.Unused(project).Count;
        if (_workspace.Apply(ArtLibrary.RemoveUnused(project)))
        {
            if (_workspace.Current?.CustomAssets.Any(asset => asset.Id == _selectedAssetId) != true) _selectedAssetId = null;
            Refresh();
            _message.Text = $"Removed {count} unused asset{(count == 1 ? "" : "s")}.";
        }
    }

    public void Refresh()
    {
        if (_workspace.Current is not { } project) return;
        _unusedConfirm.IsVisible = false;
        _refreshing = true;
        try
        {
            _assets.SelectedItem = null;
            _assets.Items.Clear();
            var pixelArt = project.Graphics.OrNull()?.PixelArt != false;
            foreach (var asset in project.CustomAssets)
            {
                var text = $"{asset.Name} · {SizeText(asset)}";
                var thumbnail = new Image { Width = 32, Height = 32, Stretch = Stretch.Uniform, Source = Thumbnail(asset) };
                RenderOptions.SetBitmapInterpolationMode(thumbnail, pixelArt ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
                var item = new ListBoxItem { Tag = asset.Id, Content = Ui.HStack(8, thumbnail, Ui.Text(text)) };
                AutomationProperties.SetName(item, text);
                _assets.Items.Add(item);
            }
            foreach (var stale in _thumbnails.Keys.Where(id => !project.CustomAssets.Any(asset => asset.Id == id)).ToList())
            {
                _thumbnails[stale].Bitmap?.Dispose();
                _thumbnails.Remove(stale);
            }
            _assets.SelectedItem = _assets.Items.OfType<ListBoxItem>().FirstOrDefault(item => Equals(item.Tag, _selectedAssetId));
            _pixelArt.IsChecked = pixelArt;
            var previousTarget = (_target.SelectedItem as ComboBoxItem)?.Tag;
            _target.Items.Clear();
            void Target(string label, string kind, string id) => _target.Items.Add(new ComboBoxItem { Content = label, Tag = (kind, id) });
            Target("Player", "player", "");
            Target("Map brush", "tiles", "");
            foreach (var npc in project.Npcs) Target($"NPC · {npc.Name}", "npc", npc.Id);
            foreach (var item in project.Items) Target($"Item · {item.Name}", "item", item.Id);
            foreach (var crop in project.CustomCrops.OrEmpty()) Target($"Crop · {crop.Name}", "crop", crop.Id);
            foreach (var node in project.NodeTypes) Target($"Node · {node.Name}", "node", node.Id);
            foreach (var animal in project.AnimalSpecies) Target($"Animal · {animal.Name}", "animal", animal.Id);
            foreach (var machine in project.MachineTypes) Target($"Machine · {machine.Name}", "machine", machine.Id);
            _target.SelectedItem = _target.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, previousTarget)) ?? _target.Items[0];
            var previousBinding = (_bindingAsset.SelectedItem as ComboBoxItem)?.Tag as string;
            _bindingAsset.Items.Clear();
            foreach (var asset in project.CustomAssets) _bindingAsset.Items.Add(new ComboBoxItem { Tag = asset.Id, Content = asset.Name });
            _bindingAsset.SelectedItem = _bindingAsset.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, previousBinding))
                ?? _bindingAsset.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, _selectedAssetId));
            var previousSource = (_frameSource.SelectedItem as ComboBoxItem)?.Tag as string;
            _frameSource.SelectedItem = null;
            _frameSource.Items.Clear();
            foreach (var asset in project.CustomAssets) _frameSource.Items.Add(new ComboBoxItem { Tag = asset.Id, Content = asset.Name });
            _frameSource.SelectedItem = _frameSource.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, previousSource));
        }
        finally
        {
            _refreshing = false;
        }
        RefreshSelected();
        RefreshBindingClips();
    }

    private void RefreshSelected()
    {
        var asset = SelectedAsset();
        _assetName.Text = asset?.Name ?? "";
        var previousClip = (_clips.SelectedItem as ComboBoxItem)?.Tag as string;
        _refreshing = true;
        try
        {
            _clips.SelectedItem = null;
            _clips.Items.Clear();
            foreach (var clip in asset?.Animations.OrEmpty() ?? []) _clips.Items.Add(new ComboBoxItem { Tag = clip.Name, Content = clip.Name });
            _clips.SelectedItem = _clips.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, previousClip)) ?? _clips.Items.OfType<ComboBoxItem>().FirstOrDefault();
        }
        finally
        {
            _refreshing = false;
        }
        RefreshSheet(asset);
        RefreshFrames();
        DrawPreview();
        UpdatePreviewTimer();
    }

    /// <summary>The asset's list thumbnail (at most 64px, for crisp 32px display), decoded once per image.</summary>
    private Bitmap? Thumbnail(CustomAsset asset)
    {
        if (_thumbnails.TryGetValue(asset.Id, out var cached))
        {
            if (cached.DataUrl == asset.DataUrl) return cached.Bitmap;
            cached.Bitmap?.Dispose();
        }

        var bitmap = ArtBitmaps.Thumbnail(asset, 64);
        _thumbnails[asset.Id] = (asset.DataUrl, bitmap);
        return bitmap;
    }

    /// <summary>Shows the selected asset's whole image on the sprite sheet (decoded again only when it changes).</summary>
    private void RefreshSheet(CustomAsset? asset)
    {
        if (asset is not null && _sheetKey is { } key && key.AssetId == asset.Id && key.DataUrl == asset.DataUrl) return;
        var previous = _sheet.Image;
        _sheet.SetImage(asset is null ? null : ArtBitmaps.Decode(asset.DataUrl));
        previous?.Dispose();
        _sheetKey = asset is null ? null : (asset.Id, asset.DataUrl);
        RefreshSheetGrid();
    }

    private void RefreshSheetGrid()
    {
        static int Cell(TextBox box) => int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : 0;
        _sheet.SetCell(Cell(_frameWidth), Cell(_frameHeight));
    }

    private void DrawPreview()
    {
        var asset = SelectedAsset();
        var project = _workspace.Current;
        if (asset is null || project is null) { VisualPreview.Release(_preview.Child as Image); _preview.Child = null; return; }
        var visual = VisualRef.Default.WithAssetId(asset.Id).WithAnimation((_clips.SelectedItem as ComboBoxItem)?.Tag as string);
        // The Rust renderer resolves and draws the frame, exactly as the game will show it, into
        // the bitmap already on show when the size is the same.
        _preview.Child = _art.Draw(_preview.Child as Image, project, visual, _tick, _preview.Width);
    }

    /// <summary>
    /// "1. 16,0 · 16×16" for a cell of the asset's own image, "2. from Carrot.png · 16×16" for a
    /// frame drawn from another image.
    /// </summary>
    private string FrameLabel(CustomAsset asset, int index, ArtFrame frame)
    {
        var size = $"{Number(frame.Width)}×{Number(frame.Height)}";
        var position = $"{Number(frame.X)},{Number(frame.Y)}";
        if (frame.AssetId.OrNull() is not { } sourceId || sourceId == asset.Id) return $"{index + 1}. {position} · {size}";
        var source = _workspace.Current?.CustomAssets.FirstOrDefault(other => other.Id == sourceId);
        var at = frame.X == 0 && frame.Y == 0 ? "" : $" at {position}";
        return $"{index + 1}. from {source?.Name ?? $"(missing: {sourceId})"}{at} · {size}";
    }

    private void RefreshFrames()
    {
        _frames.Children.Clear();
        var clip = SelectedClip();
        if (clip is null || SelectedAsset() is not { } owner) return;
        for (var index = 0; index < clip.Frames.Length; index++)
        {
            var i = index;
            var frame = clip.Frames[i];
            var label = Ui.Text(FrameLabel(owner, i, frame), "muted", "small");
            label.Name = $"ArtFrameLabel_{i}";
            var ticks = new TextBox { Name = $"ArtFrameTicks_{i}", Text = Number(frame.Ticks), Width = 52 };
            ToolTip.SetTip(ticks, $"Frame {i + 1} duration in ticks");
            Ui.Label((ticks, $"Frame {i + 1} duration in ticks"));
            var setTicks = Ui.Button("Set", () =>
            {
                if (SelectedAsset() is not { } asset) return;
                try
                {
                    _workspace.Apply(Edits.SetFrameTicks(asset.Id, clip.Name, i, Positive(ticks)));
                }
                catch (FormatException error) { _message.Text = error.Message; }
            }, "tool", "small");
            setTicks.Name = $"ArtFrameTicksSet_{i}";
            Ui.Label((setTicks, $"Set frame {i + 1} duration"));
            var duplicate = Ui.Button("Copy", () =>
            {
                if (SelectedAsset() is { } asset) _workspace.Apply(Edits.DuplicateFrame(asset.Id, clip.Name, i));
            }, "tool", "small");
            duplicate.Name = $"ArtFrameDuplicate_{i}";
            duplicate.IsEnabled = clip.Frames.Length < 1024;
            ToolTip.SetTip(duplicate, "Duplicate this frame");
            Ui.Label((duplicate, $"Duplicate frame {i + 1}"));
            var up = Ui.Button("↑", () =>
            {
                if (i == 0) return;
                var frames = clip.Frames.ToList();
                (frames[i - 1], frames[i]) = (frames[i], frames[i - 1]);
                SaveClip(clip.WithFrames(frames));
            }, "tool", "small");
            up.IsEnabled = i > 0;
            AutomationProperties.SetName(up, $"Move frame {i + 1} earlier");
            var remove = Ui.Button("×", () =>
            {
                var frames = clip.Frames.Where((_, n) => n != i).ToList();
                if (frames.Count == 0) RemoveClip();
                else SaveClip(clip.WithFrames(frames));
            }, "tool", "small");
            AutomationProperties.SetName(remove, $"Remove frame {i + 1}");
            _frames.Children.Add(Ui.Row(label, ticks, setTicks, duplicate, up, remove));
        }
    }

    private void SaveClip(AnimationClip clip)
    {
        if (SelectedAsset() is not { } asset) return;
        var clips = asset.Animations.OrEmpty().Where(existing => existing.Name != clip.Name).Append(clip).ToList();
        _workspace.Apply(Edits.UpsertAsset(asset.WithAnimations(clips)));
        _clips.SelectedItem = _clips.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, clip.Name));
    }

    private void SliceSheet()
    {
        if (SelectedAsset() is not { } asset || asset.Width is null || asset.Height is null) return;
        try
        {
            var width = Positive(_frameWidth);
            var height = Positive(_frameHeight);
            var ticks = Positive(_frameTicks);
            var name = _clipName.Text?.Trim() ?? "";
            if (name.Length == 0) throw new FormatException("Give the clip a name.");
            var frames = new List<ArtFrame>();
            for (var y = 0; y + height <= asset.Height.Or(0); y += height)
                for (var x = 0; x + width <= asset.Width.Or(0) && frames.Count < 1024; x += width)
                    frames.Add(ArtFrame.Default.WithX(x).WithY(y).WithWidth(width).WithHeight(height).WithTicks(ticks));
            if (frames.Count == 0) throw new FormatException("No complete frames fit the image.");
            SaveClip(AnimationClip.Default.WithName(name).WithLoop(_loop.IsChecked == true).WithFrames(frames));
            _message.Text = $"Sliced {frames.Count} frames into {name}.";
        }
        catch (FormatException error) { _message.Text = error.Message; }
    }

    private void AppendFrame()
    {
        if (SelectedAsset() is not { } asset) return;
        try
        {
            var width = Positive(_frameWidth);
            var height = Positive(_frameHeight);
            var x = Nonnegative(_frameX);
            var y = Nonnegative(_frameY);
            var ticks = Positive(_frameTicks);
            var (imageWidth, imageHeight) = ImageSize(asset);
            if (x + width > imageWidth || y + height > imageHeight) throw new FormatException("That frame extends beyond the image.");
            AddFrame(asset, ArtFrame.Default.WithX(x).WithY(y).WithWidth(width).WithHeight(height).WithTicks(ticks));
        }
        catch (FormatException error) { _message.Text = error.Message; }
    }

    /// <summary>
    /// Adds the sprite sheet cell under image pixel (<paramref name="x"/>, <paramref name="y"/>)
    /// of the selected asset to the clip named in the Clip name box, creating the clip when needed
    /// (web AssetManager sheet click): a frame width × height cell with the current ticks, as one
    /// undo step.
    /// </summary>
    public void AppendFrameAt(double x, double y)
    {
        if (SelectedAsset() is not { } asset) return;
        try
        {
            var width = Positive(_frameWidth);
            var height = Positive(_frameHeight);
            var ticks = Positive(_frameTicks);
            var (imageWidth, imageHeight) = ImageSize(asset);
            var cellX = Math.Floor(x / width) * width;
            var cellY = Math.Floor(y / height) * height;
            if (!double.IsFinite(cellX) || !double.IsFinite(cellY) || cellX < 0 || cellY < 0 || cellX + width > imageWidth || cellY + height > imageHeight)
                throw new FormatException("That frame extends beyond the image.");
            AddFrame(asset, ArtFrame.Default.WithX(cellX).WithY(cellY).WithWidth(width).WithHeight(height).WithTicks(ticks));
        }
        catch (FormatException error) { _message.Text = error.Message; }
    }

    /// <summary>
    /// Adds the whole image picked under "Separate image" as the next frame of the clip (web
    /// AssetManager "Add image frame"), so one animation can use frames of several images. The
    /// renderer draws a frame with an <c>AssetId</c> from that asset.
    /// </summary>
    private void AddImageFrame()
    {
        if (SelectedAsset() is not { } asset) return;
        var sourceId = (_frameSource.SelectedItem as ComboBoxItem)?.Tag as string;
        if (_workspace.Current?.CustomAssets.FirstOrDefault(other => other.Id == sourceId) is not { } source)
        {
            _message.Text = "Choose the image to add.";
            return;
        }

        try
        {
            if (source.Width.OrNullable() is not { } width || source.Height.OrNullable() is not { } height) throw new FormatException("Reimport this art to read its size.");
            var ticks = Positive(_frameTicks);
            // A frame of the clip's own asset needs no AssetId.
            AddFrame(asset, ArtFrame.Default.WithAssetId(source.Id == asset.Id ? null : source.Id).WithWidth(width).WithHeight(height).WithTicks(ticks));
        }
        catch (FormatException error) { _message.Text = error.Message; }
    }

    /// <summary>The asset's size in pixels: the size recorded at import, else the sheet's decoded image (legacy art).</summary>
    private (double Width, double Height) ImageSize(CustomAsset asset)
    {
        if (asset.Width.OrNullable() is { } width && asset.Height.OrNullable() is { } height) return (width, height);
        if (_sheetKey?.AssetId == asset.Id && _sheet.Image is { } image) return (image.PixelSize.Width, image.PixelSize.Height);
        throw new FormatException("Reimport this art to read its size.");
    }

    /// <summary>
    /// Appends <paramref name="frame"/> to the clip named in the Clip name box, creating the clip
    /// when it does not exist yet: one F# edit (one undo step).
    /// </summary>
    private void AddFrame(CustomAsset asset, ArtFrame frame)
    {
        var name = _clipName.Text?.Trim() ?? "";
        if (name.Length == 0) throw new FormatException("Give the clip a name.");
        var existing = asset.Animations.OrEmpty().FirstOrDefault(clip => clip.Name == name);
        var frames = existing?.Frames.ToList() ?? [];
        if (frames.Count >= 1024) throw new FormatException("A clip may contain at most 1024 frames.");
        frames.Add(frame);
        SaveClip(AnimationClip.Default.WithName(name).WithLoop(_loop.IsChecked == true).WithFrames(frames));
        _message.Text = $"Added frame {frames.Count} to {name}.";
    }

    private void SetAllFrameTicks()
    {
        if (SelectedAsset() is not { } asset || SelectedClip() is not { } clip) return;
        try
        {
            _workspace.Apply(Edits.SetAllFrameTicks(asset.Id, clip.Name, Positive(_frameTicks)));
            _message.Text = $"Every frame of {clip.Name} now lasts {Positive(_frameTicks)} ticks.";
        }
        catch (FormatException error) { _message.Text = error.Message; }
    }

    private void RemoveClip()
    {
        if (SelectedAsset() is not { } asset || SelectedClip() is not { } clip) return;
        _workspace.Apply(Edits.UpsertAsset(asset.WithAnimations(asset.Animations.OrEmpty().Where(other => other.Name != clip.Name).ToList())));
    }

    private void RenameAsset()
    {
        if (SelectedAsset() is not { } asset || string.IsNullOrWhiteSpace(_assetName.Text)) return;
        _workspace.Apply(Edits.UpsertAsset(asset.WithName(_assetName.Text.Trim())));
    }

    private void RemoveAsset()
    {
        if (_selectedAssetId is not { } id) return;
        _selectedAssetId = null;
        _workspace.Apply(Edits.RemoveAsset(id));
        _message.Text = "Asset removed; bindings now use their default look.";
    }

    private void RefreshBindingClips()
    {
        var id = (_bindingAsset.SelectedItem as ComboBoxItem)?.Tag as string;
        var asset = _workspace.Current?.CustomAssets.FirstOrDefault(item => item.Id == id);
        var previous = (_bindingClip.SelectedItem as ComboBoxItem)?.Tag as string;
        _bindingClip.SelectedItem = null;
        _bindingClip.Items.Clear();
        _bindingClip.Items.Add(new ComboBoxItem { Content = "Automatic", Tag = "" });
        foreach (var clip in asset?.Animations.OrEmpty() ?? []) _bindingClip.Items.Add(new ComboBoxItem { Content = clip.Name, Tag = clip.Name });
        _bindingClip.SelectedItem = _bindingClip.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, previous)) ?? _bindingClip.Items[0];
    }

    private void RefreshCellControls()
    {
        _cellSize.IsEnabled = _singleCell.IsChecked == true;
        _cellColumn.IsEnabled = _singleCell.IsChecked == true;
        _cellRow.IsEnabled = _singleCell.IsChecked == true;
    }

    private void Bind()
    {
        if (_workspace.Current is not { } project || _target.SelectedItem is not ComboBoxItem { Tag: ValueTuple<string, string> target }
            || _bindingAsset.SelectedItem is not ComboBoxItem { Tag: string assetId }) return;
        var asset = project.CustomAssets.FirstOrDefault(item => item.Id == assetId);
        if (asset is null) return;
        try
        {
            ArtFrame? frame = null;
            if (_singleCell.IsChecked == true)
            {
                var size = Positive(_cellSize);
                var x = Nonnegative(_cellColumn) * size;
                var y = Nonnegative(_cellRow) * size;
                if (x + size > asset.Width.Or(0) || y + size > asset.Height.Or(0)) throw new FormatException("That cell extends beyond the image.");
                frame = ArtFrame.Default.WithX(x).WithY(y).WithWidth(size).WithHeight(size).WithTicks(6);
            }
            var clip = (_bindingClip.SelectedItem as ComboBoxItem)?.Tag as string;
            var visual = VisualRef.Default.WithAssetId(assetId).WithAnimation(string.IsNullOrEmpty(clip) ? null : clip).WithFrame(frame);
            var edit = target.Item1 switch
            {
                "player" => Edits.BindPlayerVisual(visual),
                "npc" => Edits.BindNpcVisual(target.Item2, visual),
                "item" => Edits.BindItemVisual(target.Item2, visual),
                "crop" => Edits.BindCropVisual(target.Item2, visual),
                "node" => Edits.BindNodeTypeVisual(target.Item2, visual),
                "animal" => Edits.BindAnimalSpeciesVisual(target.Item2, visual),
                "machine" => Edits.BindMachineTypeVisual(target.Item2, visual),
                _ => null,
            };
            if (target.Item1 == "tiles")
            {
                if (_tileType.SelectedItem is ComboBoxItem { Tag: string type }) _useMapBrush(type, visual);
            }
            else if (edit is not null) _workspace.Apply(edit);
            _message.Text = "Artwork assigned.";
        }
        catch (FormatException error) { _message.Text = error.Message; }
    }
}
