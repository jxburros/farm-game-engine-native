using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FarmEngine.Authoring;
using FarmEngine.Schemas;
using FarmingRpgMaker.App.Projects;

namespace FarmingRpgMaker.App.Game;

/// <summary>Import, animate and bind creator art through F# project edits.</summary>
public sealed class ArtEditorView : UserControl
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
    private string? _selectedAssetId;
    private bool _refreshing;
    private double _tick;

    public ArtEditorView(ProjectWorkspace workspace, Action<string, VisualRef> useMapBrush)
    {
        _workspace = workspace;
        _useMapBrush = useMapBrush;
        Name = "ArtEditorView";
        _message.Name = "ArtMessage";
        _previewTimer.Tick += (_, _) =>
        {
            if (!IsEffectivelyVisible) return;
            _tick++;
            DrawPreview();
        };
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
        };
        _bindingAsset.SelectionChanged += (_, _) => RefreshBindingClips();

        var left = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 20, 0) };
        left.Children.Add(Ui.Text("ARTWORK", "section"));
        left.Children.Add(Ui.Wrapped("Import PNG, JPEG, WebP, GIF, BMP or SVG. Animated images and SVGs become a still PNG; add clips below.", "muted", "small"));
        var import = Ui.Button("Import image", async () => await PickImageAsync(), "accent");
        import.Name = "ImportArtButton";
        left.Children.Add(import);
        var svgSize = Ui.HStack(6, Ui.Text("SVG size (longest side, px)", "muted", "small"), _svgSize);
        ToolTip.SetTip(svgSize, "Leave empty to use the SVG's own size.");
        left.Children.Add(svgSize);
        left.Children.Add(_assets);
        left.Children.Add(_pixelArt);
        _pixelArt.Click += (_, _) => _workspace.Apply(Edits.SetGraphics(new GraphicsSettings { PixelArt = _pixelArt.IsChecked == true }));
        left.Children.Add(_message);

        var right = new StackPanel { Spacing = 10 };
        right.Children.Add(_preview);
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
        var slice = Ui.Button("Slice entire sheet", SliceSheet, "tool");
        slice.Name = "SliceArtButton";
        var append = Ui.Button("Append frame", AppendFrame, "tool");
        append.Name = "AppendArtFrameButton";
        var removeClip = Ui.Button("Remove clip", RemoveClip, "tool");
        right.Children.Add(Ui.HStack(8, slice, append, removeClip));
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
        _workspace.ProjectChanged += (_, _) =>
        {
            if (IsEffectivelyVisible) Refresh();
        };
        RefreshCellControls();
        Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _previewTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _previewTimer.Stop();
        _art.Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    private static string Number(double value) => value.ToString("G", CultureInfo.InvariantCulture);
    private static int Positive(TextBox box) => int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : throw new FormatException($"{box.Name} must be a positive whole number.");
    private static int Nonnegative(TextBox box) => int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : throw new FormatException($"{box.Name} must be zero or more.");
    private CustomAsset? SelectedAsset() => _workspace.Current?.CustomAssets.FirstOrDefault(asset => asset.Id == _selectedAssetId);
    private AnimationClip? SelectedClip() => SelectedAsset()?.Animations?.FirstOrDefault(clip => clip.Name == (_clips.SelectedItem as ComboBoxItem)?.Tag as string);

    /// <summary>The asset being edited, or null.</summary>
    public string? SelectedAssetId => _selectedAssetId;

    /// <summary>Opens an asset by id (Problems "Go to").</summary>
    public void SelectAsset(string assetId)
    {
        _selectedAssetId = assetId;
        Refresh();
    }

    public void ImportBytes(string fileName, byte[] bytes)
    {
        if (_workspace.Current is not { } project) return;
        try
        {
            int? svgSide = null;
            if (!string.IsNullOrWhiteSpace(_svgSize.Text))
            {
                if (!int.TryParse(_svgSize.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var side) || side <= 0)
                    throw new ArgumentException("The SVG size must be a positive whole number of pixels.");
                svgSide = side;
            }

            var asset = ArtImport.FromBytes(project, fileName, bytes, svgSide);
            _selectedAssetId = asset.Id;
            _workspace.Apply(Edits.UpsertAsset(asset));
            _message.Text = $"Imported {asset.Name} ({asset.Width}×{asset.Height}).";
        }
        catch (ArgumentException error)
        {
            _message.Text = error.Message;
        }
    }

    private async Task PickImageAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import artwork", AllowMultiple = false, FileTypeFilter = [ArtFiles, FilePickerFileTypes.All] });
        if (files.Count == 0) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                if (memory.Length + read > ArtImport.MaxBytes) throw new ArgumentException("Each image must be under 16 MB.");
                await memory.WriteAsync(buffer.AsMemory(0, read));
            }
            ImportBytes(files[0].Name, memory.ToArray());
        }
        catch (Exception error) when (error is IOException or ArgumentException)
        {
            _message.Text = $"Could not import art: {error.Message}";
        }
    }

    public void Refresh()
    {
        if (_workspace.Current is not { } project) return;
        _refreshing = true;
        try
        {
            _assets.SelectedItem = null;
            _assets.Items.Clear();
            foreach (var asset in project.CustomAssets)
                _assets.Items.Add(new ListBoxItem { Tag = asset.Id, Content = $"{asset.Name} · {asset.Width ?? 0}×{asset.Height ?? 0}" });
            _assets.SelectedItem = _assets.Items.OfType<ListBoxItem>().FirstOrDefault(item => Equals(item.Tag, _selectedAssetId));
            _pixelArt.IsChecked = project.Graphics?.PixelArt != false;
            var previousTarget = (_target.SelectedItem as ComboBoxItem)?.Tag;
            _target.Items.Clear();
            void Target(string label, string kind, string id) => _target.Items.Add(new ComboBoxItem { Content = label, Tag = (kind, id) });
            Target("Player", "player", "");
            Target("Map brush", "tiles", "");
            foreach (var npc in project.Npcs) Target($"NPC · {npc.Name}", "npc", npc.Id);
            foreach (var item in project.Items) Target($"Item · {item.Name}", "item", item.Id);
            foreach (var crop in project.CustomCrops ?? []) Target($"Crop · {crop.Name}", "crop", crop.Id);
            foreach (var node in project.NodeTypes) Target($"Node · {node.Name}", "node", node.Id);
            foreach (var animal in project.AnimalSpecies) Target($"Animal · {animal.Name}", "animal", animal.Id);
            foreach (var machine in project.MachineTypes) Target($"Machine · {machine.Name}", "machine", machine.Id);
            _target.SelectedItem = _target.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, previousTarget)) ?? _target.Items[0];
            var previousBinding = (_bindingAsset.SelectedItem as ComboBoxItem)?.Tag as string;
            _bindingAsset.Items.Clear();
            foreach (var asset in project.CustomAssets) _bindingAsset.Items.Add(new ComboBoxItem { Tag = asset.Id, Content = asset.Name });
            _bindingAsset.SelectedItem = _bindingAsset.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, previousBinding))
                ?? _bindingAsset.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, _selectedAssetId));
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
            foreach (var clip in asset?.Animations ?? []) _clips.Items.Add(new ComboBoxItem { Tag = clip.Name, Content = clip.Name });
            _clips.SelectedItem = _clips.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, previousClip)) ?? _clips.Items.OfType<ComboBoxItem>().FirstOrDefault();
        }
        finally
        {
            _refreshing = false;
        }
        RefreshFrames();
        DrawPreview();
    }

    private void DrawPreview()
    {
        var asset = SelectedAsset();
        var project = _workspace.Current;
        if (asset is null || project is null) { _preview.Child = null; return; }
        var visual = new VisualRef { AssetId = asset.Id, Animation = (_clips.SelectedItem as ComboBoxItem)?.Tag as string };
        // The Rust renderer resolves and draws the frame, exactly as the game will show it.
        _preview.Child = _art.Render(project, visual, _tick, _preview.Width);
    }

    private void RefreshFrames()
    {
        _frames.Children.Clear();
        var clip = SelectedClip();
        if (clip is null) return;
        for (var index = 0; index < clip.Frames.Count; index++)
        {
            var i = index;
            var frame = clip.Frames[i];
            var label = Ui.Text($"{i + 1}. {frame.X},{frame.Y} · {frame.Width}×{frame.Height}", "muted", "small");
            var ticks = new TextBox { Name = $"ArtFrameTicks_{i}", Text = Number(frame.Ticks), Width = 52 };
            ToolTip.SetTip(ticks, $"Frame {i + 1} duration in ticks");
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
            var duplicate = Ui.Button("Copy", () =>
            {
                if (SelectedAsset() is { } asset) _workspace.Apply(Edits.DuplicateFrame(asset.Id, clip.Name, i));
            }, "tool", "small");
            duplicate.Name = $"ArtFrameDuplicate_{i}";
            duplicate.IsEnabled = clip.Frames.Count < 1024;
            ToolTip.SetTip(duplicate, "Duplicate this frame");
            var up = Ui.Button("↑", () =>
            {
                if (i == 0) return;
                var frames = clip.Frames.ToList();
                (frames[i - 1], frames[i]) = (frames[i], frames[i - 1]);
                SaveClip(clip with { Frames = frames });
            }, "tool", "small");
            up.IsEnabled = i > 0;
            var remove = Ui.Button("×", () =>
            {
                var frames = clip.Frames.Where((_, n) => n != i).ToList();
                if (frames.Count == 0) RemoveClip();
                else SaveClip(clip with { Frames = frames });
            }, "tool", "small");
            _frames.Children.Add(Ui.Row(label, ticks, setTicks, duplicate, up, remove));
        }
    }

    private void SaveClip(AnimationClip clip)
    {
        if (SelectedAsset() is not { } asset) return;
        var clips = (asset.Animations ?? []).Where(existing => existing.Name != clip.Name).Append(clip).ToList();
        _workspace.Apply(Edits.UpsertAsset(asset with { Animations = clips }));
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
            for (var y = 0; y + height <= asset.Height; y += height)
                for (var x = 0; x + width <= asset.Width && frames.Count < 1024; x += width)
                    frames.Add(new ArtFrame { X = x, Y = y, Width = width, Height = height, Ticks = ticks });
            if (frames.Count == 0) throw new FormatException("No complete frames fit the image.");
            SaveClip(new AnimationClip { Name = name, Loop = _loop.IsChecked == true, Frames = frames });
            _message.Text = $"Sliced {frames.Count} frames into {name}.";
        }
        catch (FormatException error) { _message.Text = error.Message; }
    }

    private void AppendFrame()
    {
        if (SelectedAsset() is not { } asset || asset.Width is null || asset.Height is null) return;
        try
        {
            var width = Positive(_frameWidth);
            var height = Positive(_frameHeight);
            var x = Nonnegative(_frameX);
            var y = Nonnegative(_frameY);
            var ticks = Positive(_frameTicks);
            var name = _clipName.Text?.Trim() ?? "";
            if (name.Length == 0 || x + width > asset.Width || y + height > asset.Height) throw new FormatException("Frame needs a name and must fit within the image.");
            var existing = asset.Animations?.FirstOrDefault(clip => clip.Name == name);
            var frames = existing?.Frames.ToList() ?? [];
            if (frames.Count >= 1024) throw new FormatException("A clip may contain at most 1024 frames.");
            frames.Add(new ArtFrame { X = x, Y = y, Width = width, Height = height, Ticks = ticks });
            SaveClip(new AnimationClip { Name = name, Loop = _loop.IsChecked == true, Frames = frames });
            _message.Text = $"Added frame to {name}.";
        }
        catch (FormatException error) { _message.Text = error.Message; }
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
        _workspace.Apply(Edits.UpsertAsset(asset with { Animations = asset.Animations?.Where(other => other.Name != clip.Name).ToList() }));
    }

    private void RenameAsset()
    {
        if (SelectedAsset() is not { } asset || string.IsNullOrWhiteSpace(_assetName.Text)) return;
        _workspace.Apply(Edits.UpsertAsset(asset with { Name = _assetName.Text.Trim() }));
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
        foreach (var clip in asset?.Animations ?? []) _bindingClip.Items.Add(new ComboBoxItem { Content = clip.Name, Tag = clip.Name });
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
                if (x + size > asset.Width || y + size > asset.Height) throw new FormatException("That cell extends beyond the image.");
                frame = new ArtFrame { X = x, Y = y, Width = size, Height = size, Ticks = 6 };
            }
            var clip = (_bindingClip.SelectedItem as ComboBoxItem)?.Tag as string;
            var visual = new VisualRef { AssetId = assetId, Animation = string.IsNullOrEmpty(clip) ? null : clip, Frame = frame };
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
