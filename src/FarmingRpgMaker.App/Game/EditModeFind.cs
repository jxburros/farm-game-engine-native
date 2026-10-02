using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using FarmEngine.Authoring;
using FarmEngine.Schemas;
using FarmingRpgMaker.App.Projects;

namespace FarmingRpgMaker.App.Game;

/// <summary>
/// The map's markers (doors, arrivals, event tiles, the mine entrance, the player start),
/// "Pick on map" for the forms' tile coordinates (#47), the quick-open search (#56) and the
/// forms' unsaved fields when the project is left (#87).
/// </summary>
public sealed partial class EditModeView
{
    /// <summary>One quick-open result: what it is, its label, and how to open it.</summary>
    public sealed record FindResult(string Kind, string Label, Action Open);

    private readonly Canvas _markerLayer = new() { Name = "MapMarkers", IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly CheckBox _showMarkers = new() { Name = "ShowMarkers", Content = "Markers", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private IReadOnlyList<MapMarker> _markers = [];
    private (GameProject? Project, MapGeometry? Geometry, bool Shown) _markersFor;
    private readonly Border _quickOpen = new() { Name = "QuickOpen", IsVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Width = 520, Margin = new Thickness(0, 48, 0, 0) };
    private readonly TextBox _quickOpenBox = new() { Name = "QuickOpenBox", Watermark = "Find NPCs, items, quests, events, scenes, art…" };
    private readonly ListBox _quickOpenResults = new() { Name = "QuickOpenResults", MaxHeight = 360 };
    private List<FindResult> _found = [];
    /// <summary>A "Pick on map" waiting for a tile: what to do with it, and where to go back to.</summary>
    private (Action<int, int> Done, string? ReturnScene, EditorTab ReturnTab)? _pick;

    /// <summary>The markers drawn over the scene on show.</summary>
    public IReadOnlyList<MapMarker> Markers => _markers;

    /// <summary>True while a "Pick on map" waits for a tile.</summary>
    public bool IsPicking => _pick is not null;

    /// <summary>True while a form (content, interface, settings) holds fields that are not saved (#87).</summary>
    public bool HasUnsavedDrafts => _contentEditor.HasUnsavedChanges || _interfaceEditor.HasUnsavedChanges || _settingsEditor.HasUnsavedChanges;

    /// <summary>
    /// Applies the fields typed into the forms but not saved yet (the project is being left, the
    /// editor closes). False when some could not be applied; their forms say why.
    /// </summary>
    public bool SaveDrafts()
    {
        var content = _contentEditor.SaveDraft();
        var panel = _interfaceEditor.SaveDraft();
        var settings = _settingsEditor.SaveDrafts();
        return content && panel && settings;
    }

    private void OnLeaving(object? sender, EventArgs e) => SaveDrafts();

    // ---- Markers (#47) ----

    /// <summary>Draws the scene's markers over the map, again when the project, scene or zoom changed.</summary>
    private void RefreshMarkers(bool force = false)
    {
        var project = _workspace.Current;
        var geometry = _canvas.Geometry;
        var shown = _showMarkers.IsChecked == true;
        if (!force && ReferenceEquals(_markersFor.Project, project) && _markersFor.Geometry == geometry && _markersFor.Shown == shown) return;
        _markersFor = (project, geometry, shown);
        _markers = project is not null && geometry is not null ? MapMarkers.ForScene(project, geometry.SceneId) : [];
        _markerLayer.Children.Clear();
        _markerLayer.IsVisible = shown;
        if (geometry is null) return;
        _markerLayer.Width = geometry.WorldSize.Width;
        _markerLayer.Height = geometry.WorldSize.Height;
        foreach (var marker in _markers)
        {
            var first = _canvas.TileRect(marker.X, marker.Y);
            var last = _canvas.TileRect(marker.X2, marker.Y2);
            var (glyph, color) = marker.Kind switch
            {
                "door" => ("D", "#8A4B12"),
                "arrival" => ("A", "#3B5BA9"),
                "event" => ("!", "#9B2C6F"),
                "mine" => ("M", "#3F3F46"),
                _ => ("S", "#0B6B3A"),
            };
            if (marker.Kind == "event" && (marker.X2 != marker.X || marker.Y2 != marker.Y))
            {
                var region = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.Parse(color)),
                    BorderThickness = new Thickness(2),
                    Background = new SolidColorBrush(Color.Parse(color), 0.12),
                    Width = last.Right - first.Left,
                    Height = last.Bottom - first.Top,
                };
                Avalonia.Controls.Canvas.SetLeft(region, first.Left);
                Avalonia.Controls.Canvas.SetTop(region, first.Top);
                _markerLayer.Children.Add(region);
            }

            var size = Math.Max(10, Math.Round(first.Width * 0.5));
            var badge = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 2),
                Background = new SolidColorBrush(Color.Parse(color)),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = glyph,
                    Foreground = Brushes.White,
                    FontWeight = FontWeight.Bold,
                    FontSize = Math.Max(8, size * 0.62),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            badge.Name = $"Marker_{marker.Kind}_{marker.X}_{marker.Y}";
            AutomationProperties.SetName(badge, marker.Label);
            ToolTip.SetTip(badge, marker.Label);
            Avalonia.Controls.Canvas.SetLeft(badge, first.Left + 1);
            Avalonia.Controls.Canvas.SetTop(badge, first.Top + 1);
            _markerLayer.Children.Add(badge);
        }
    }

    /// <summary>The markers covering a tile.</summary>
    private IEnumerable<MapMarker> MarkersAt(int x, int y) =>
        _markers.Where(m => x >= m.X && x <= m.X2 && y >= m.Y && y <= m.Y2);

    /// <summary>
    /// Inspect on a marked tile opens what marks it: a door in the door form, an event in
    /// Content → Events, an arrival at the door it comes from, the mine entrance in Settings.
    /// </summary>
    private bool OpenMarkerAt((int X, int Y) tile)
    {
        if (MarkersAt(tile.X, tile.Y).FirstOrDefault() is not { } marker) return false;
        switch (marker.Kind)
        {
            case "door":
                Tool = MapTool.Door;
                ChooseDoor(tile.X, tile.Y);
                _editorMessage.Text = $"{marker.Label}: edit it below.";
                return true;
            case "arrival":
                SelectScene(marker.TargetId);
                Tool = MapTool.Door;
                ChooseDoor(marker.FromX, marker.FromY);
                SetCursor((marker.FromX, marker.FromY));
                _editorMessage.Text = $"The door that lands there: edit it below.";
                return true;
            case "event":
                SelectedTab = EditorTab.Content;
                _contentEditor.SelectEntry("Events", marker.TargetId);
                return true;
            case "mine":
                SelectedTab = EditorTab.Settings;
                return true;
            default:
                _editorMessage.Text = marker.Label;
                return true;
        }
    }

    // ---- Pick on map (#47) ----

    /// <summary>
    /// Shows <paramref name="sceneId"/> on the Map tab and waits for a tile: the next click (or
    /// Enter at the editing cursor) hands it to <paramref name="done"/> and goes back to the tab
    /// and scene shown before. Escape cancels.
    /// </summary>
    public void PickOnMap(string sceneId, string prompt, Action<int, int> done)
    {
        ArgumentNullException.ThrowIfNull(done);
        var returnTab = SelectedTab;
        var returnScene = _sceneId;
        SelectedTab = EditorTab.Map;
        if (_workspace.Current?.Scenes.Any(s => s.Id == sceneId) == true && sceneId != _sceneId) SelectScene(sceneId);
        _pick = (done, returnScene, returnTab);
        _editorMessage.Text = $"{prompt} Escape cancels.";
        _canvas.Focus();
    }

    /// <summary>A pending pick takes the tile; true when one did.</summary>
    private bool CompletePick((int X, int Y) tile)
    {
        if (_pick is not { } pick) return false;
        _pick = null;
        if (pick.ReturnScene is { } scene && scene != _sceneId) SelectScene(scene);
        // The form gets the tile before its tab is shown again, so it keeps it as unsaved fields.
        pick.Done(tile.X, tile.Y);
        SelectedTab = pick.ReturnTab;
        return true;
    }

    private void CancelPick()
    {
        if (_pick is not { } pick) return;
        _pick = null;
        if (pick.ReturnScene is { } scene && scene != _sceneId) SelectScene(scene);
        SelectedTab = pick.ReturnTab;
        _editorMessage.Text = "Pick cancelled.";
    }

    /// <summary>The door form's arrival tile, clicked in the destination scene.</summary>
    private void PickDoorArrival()
    {
        if (_doorDestination.SelectedItem is not ComboBoxItem { Tag: string destination })
        {
            _editorMessage.Text = "Choose the scene the door leads to first.";
            return;
        }

        var door = _doorAt;
        var returning = _doorReturn.IsChecked;
        PickOnMap(destination, "Click the tile the player arrives on.", (x, y) =>
        {
            // Coming back to the door's scene reset the form: put the door back, with the arrival.
            Tool = MapTool.Door;
            if (door is { } from)
            {
                ChooseDoor(from.X, from.Y);
            }

            _doorDestination.SelectedItem = _doorDestination.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, destination));
            _doorX.Text = x.ToString(CultureInfo.InvariantCulture);
            _doorY.Text = y.ToString(CultureInfo.InvariantCulture);
            _doorReturn.IsChecked = returning;
            _editorMessage.Text = $"Arrival set to ({x}, {y}). Choose Save door to keep it.";
        });
    }

    // ---- Quick open (#56) ----

    private Border BuildQuickOpen()
    {
        AutomationProperties.SetName(_quickOpenBox, "Find in project");
        AutomationProperties.SetName(_quickOpenResults, "Results");
        _quickOpenBox.TextChanged += (_, _) => RunQuickOpen();
        _quickOpenBox.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Down when _quickOpenResults.ItemCount > 0:
                    _quickOpenResults.SelectedIndex = Math.Min(_quickOpenResults.SelectedIndex + 1, _quickOpenResults.ItemCount - 1);
                    e.Handled = true;
                    break;
                case Key.Up when _quickOpenResults.ItemCount > 0:
                    _quickOpenResults.SelectedIndex = Math.Max(_quickOpenResults.SelectedIndex - 1, 0);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    OpenFound(Math.Max(0, _quickOpenResults.SelectedIndex));
                    e.Handled = true;
                    break;
                case Key.Escape:
                    CloseQuickOpen();
                    e.Handled = true;
                    break;
            }
        };
        _quickOpenResults.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                OpenFound(_quickOpenResults.SelectedIndex);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CloseQuickOpen();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        _quickOpenResults.Tapped += (_, _) => OpenFound(_quickOpenResults.SelectedIndex);
        var close = Ui.Button("Close", CloseQuickOpen, "tool", "small");
        close.Name = "QuickOpenClose";
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(Ui.Text("FIND IN PROJECT (CTRL+K)", "section"));
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        _quickOpen.Child = Ui.VStack(8, header, _quickOpenBox, _quickOpenResults);
        _quickOpen.Classes.Add("modal");
        _quickOpen.Padding = new Thickness(14);
        return _quickOpen;
    }

    /// <summary>Opens the quick-open search (Ctrl+K or Ctrl+P) with the search box focused.</summary>
    public void OpenQuickOpen()
    {
        _quickOpen.IsVisible = true;
        _quickOpenBox.Text = "";
        RunQuickOpen();
        _quickOpenBox.Focus();
    }

    private void CloseQuickOpen() => _quickOpen.IsVisible = false;

    /// <summary>
    /// Every content entry, scene and piece of art whose name or id contains
    /// <paramref name="query"/> (ignoring case), names starting with it first.
    /// </summary>
    public IReadOnlyList<FindResult> Find(string query)
    {
        var text = (query ?? "").Trim();
        if (text.Length == 0 || _workspace.Current is not { } project) return [];
        var results = new List<(int Rank, FindResult Result)>();
        foreach (var scene in project.Scenes.Where(s => !s.Extra.ContainsKey("generated")))
        {
            if ((ContentEditorView.Rank(scene.Name, text) ?? ContentEditorView.Rank(scene.Id, text) + 2) is { } rank)
            {
                var id = scene.Id;
                results.Add((rank, new FindResult("Scene", $"{scene.Name}  ·  {id}", () =>
                {
                    SelectedTab = EditorTab.Map;
                    SelectScene(id);
                })));
            }
        }

        foreach (var (category, id, label) in ContentEditorView.Search(project, text))
        {
            var name = label.Split("  ·  ")[0];
            results.Add((ContentEditorView.Rank(name, text) ?? 2, new FindResult(category, label, () =>
            {
                SelectedTab = EditorTab.Content;
                _contentEditor.SelectEntry(category, id);
            })));
        }

        foreach (var asset in project.CustomAssets)
        {
            if ((ContentEditorView.Rank(asset.Name, text) ?? ContentEditorView.Rank(asset.Id, text) + 2) is { } rank)
            {
                var id = asset.Id;
                results.Add((rank, new FindResult("Art", $"{asset.Name}  ·  {id}", () =>
                {
                    SelectedTab = EditorTab.Art;
                    _artEditor.SelectAsset(id);
                })));
            }
        }

        return results.OrderBy(r => r.Rank).ThenBy(r => r.Result.Label, StringComparer.OrdinalIgnoreCase).Take(60).Select(r => r.Result).ToList();
    }

    private void RunQuickOpen()
    {
        _found = Find(_quickOpenBox.Text ?? "").ToList();
        _quickOpenResults.Items.Clear();
        foreach (var result in _found)
        {
            var item = new ListBoxItem { Content = Ui.HStack(8, Ui.Text(result.Kind, "muted", "small"), Ui.Text(result.Label)) };
            AutomationProperties.SetName(item, $"{result.Kind}: {result.Label}");
            _quickOpenResults.Items.Add(item);
        }

        if (_found.Count > 0) _quickOpenResults.SelectedIndex = 0;
    }

    private void OpenFound(int index)
    {
        if (index < 0 || index >= _found.Count) return;
        var result = _found[index];
        CloseQuickOpen();
        result.Open();
    }
}
