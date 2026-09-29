using Avalonia;
using Avalonia.Controls;
using FarmEngine.Authoring;
using FarmEngine.Authoring.Net;
using FarmEngine.Schemas;
using FarmingRpgMaker.App.Projects;

namespace FarmingRpgMaker.App.Game;

/// <summary>Creator-defined in-game panels and their live entries.</summary>
public sealed class InterfaceEditorView : UserControl
{
    private sealed record EntryRow(TextBox Label, ComboBox Kind, TextBox Value, Control Control);
    private readonly ProjectWorkspace _workspace;
    private readonly ListBox _panels = new() { Name = "InterfacePanels", MinHeight = 130 };
    private readonly TextBox _title = new() { Name = "InterfaceTitle" };
    private readonly TextBox _flag = new() { Name = "InterfaceVisibleFlag", Watermark = "Optional story flag" };
    private readonly StackPanel _entries = new() { Name = "InterfaceEntries", Spacing = 8 };
    private readonly List<EntryRow> _rows = [];
    private readonly TextBlock _message = Ui.Wrapped("Select or add a panel.", "muted", "small");
    private readonly Button _save;
    private readonly Button _delete;
    private string? _selectedId;
    private bool _refreshing;

    public InterfaceEditorView(ProjectWorkspace workspace)
    {
        _workspace = workspace;
        Name = "InterfaceEditorView";
        _message.Name = "InterfaceMessage";
        _panels.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            _selectedId = (_panels.SelectedItem as ListBoxItem)?.Tag as string;
            RefreshSelected();
        };
        var left = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 20, 0) };
        left.Children.Add(Ui.Text("GAME PANELS", "section"));
        left.Children.Add(Ui.Wrapped("Panels appear below the game and can show live counters, story status or action buttons.", "muted", "small"));
        left.Children.Add(_panels);
        var add = Ui.Button("Add panel", Add, "accent");
        add.Name = "AddInterfacePanelButton";
        _delete = Ui.Button("Remove panel", Delete, "tool");
        _delete.Name = "RemoveInterfacePanelButton";
        left.Children.Add(Ui.HStack(8, add, _delete));

        var right = new StackPanel { Spacing = 10 };
        right.Children.Add(Ui.Text("PANEL DETAILS", "section"));
        right.Children.Add(Ui.Text("Title", "muted", "small"));
        right.Children.Add(_title);
        right.Children.Add(Ui.Text("Show after story flag (optional)", "muted", "small"));
        right.Children.Add(_flag);
        right.Children.Add(Ui.Text("ENTRIES", "section"));
        right.Children.Add(_entries);
        var addEntry = Ui.Button("Add entry", AddEntry, "tool");
        addEntry.Name = "AddInterfaceEntryButton";
        right.Children.Add(addEntry);
        right.Children.Add(Ui.Wrapped("Kinds: text, money, energy, day, item, flag, action. Use an item or action id as the value for those kinds.", "muted", "small"));
        _save = Ui.Button("Save panel", Save, "accent");
        _save.Name = "SaveInterfacePanelButton";
        right.Children.Add(_save);
        right.Children.Add(_message);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("245,*"), Margin = new Thickness(20) };
        grid.Children.Add(left);
        var scroll = new ScrollViewer { Content = right };
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(scroll);
        Content = grid;
        _workspace.ProjectChanged += (_, _) =>
        {
            if (IsEffectivelyVisible) Refresh();
        };
        Refresh();
    }

    public void Refresh()
    {
        _refreshing = true;
        try
        {
            _panels.SelectedItem = null;
            _panels.Items.Clear();
            foreach (var panel in _workspace.Current?.GamePanels.OrEmpty() ?? [])
                _panels.Items.Add(new ListBoxItem { Content = panel.Title, Tag = panel.Id });
            _panels.SelectedItem = _panels.Items.OfType<ListBoxItem>().FirstOrDefault(item => Equals(item.Tag, _selectedId));
            if (_panels.SelectedItem is null) _selectedId = null;
        }
        finally { _refreshing = false; }
        RefreshSelected();
    }

    private GamePanel? Selected() => _workspace.Current?.GamePanels.OrEmpty().FirstOrDefault(panel => panel.Id == _selectedId);

    private void RefreshSelected()
    {
        var panel = Selected();
        _title.Text = panel?.Title ?? "";
        _flag.Text = panel?.VisibleFlag.OrNull() ?? "";
        _rows.Clear();
        _entries.Children.Clear();
        foreach (var entry in panel?.Entries ?? []) AddEntryRow(entry);
        _save.IsEnabled = panel is not null;
        _delete.IsEnabled = panel is not null;
        _message.Text = panel is null ? "Select or add a panel." : $"Editing {panel.Id}";
    }

    private void AddEntryRow(GamePanelEntry entry)
    {
        var label = new TextBox { Name = "InterfaceEntryLabel", Text = entry.Label, Watermark = "Label", MinWidth = 115 };
        var kind = new ComboBox { Name = "InterfaceEntryKind", MinWidth = 85 };
        foreach (var value in GamePanelEntryKinds.All) kind.Items.Add(new ComboBoxItem { Content = value, Tag = value });
        kind.SelectedItem = kind.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, entry.Kind)) ?? kind.Items[0];
        var content = new TextBox { Name = "InterfaceEntryValue", Text = entry.Value, Watermark = "Value / item id / action id", MinWidth = 150 };
        var remove = Ui.Button("×", () =>
        {
            var row = _rows.First(row => ReferenceEquals(row.Label, label));
            _rows.Remove(row);
            _entries.Children.Remove(row.Control);
        }, "tool", "small");
        var control = Ui.HStack(8, label, kind, content, remove);
        _rows.Add(new EntryRow(label, kind, content, control));
        _entries.Children.Add(control);
    }

    private void AddEntry()
    {
        if (Selected() is null) return;
        if (_rows.Count >= 40) { _message.Text = "A panel may have at most 40 entries."; return; }
        AddEntryRow(GamePanelEntry.Default.WithKind(GamePanelEntryKinds.Text));
    }

    private void Add()
    {
        if (_workspace.Current is not { } project) return;
        var panel = Defaults.NewGamePanel(project);
        _selectedId = panel.Id;
        _workspace.Apply(Edits.SetGamePanels([.. project.GamePanels.OrEmpty(), panel]));
    }

    private void Delete()
    {
        if (_workspace.Current is not { } project || _selectedId is not { } id) return;
        _selectedId = null;
        _workspace.Apply(Edits.SetGamePanels(project.GamePanels.OrEmpty().Where(panel => panel.Id != id)));
    }

    private void Save()
    {
        if (_workspace.Current is not { } project || Selected() is not { } panel) return;
        var title = _title.Text?.Trim() ?? "";
        if (title.Length == 0) { _message.Text = "Give the panel a title."; return; }
        var entries = _rows.Select(row => GamePanelEntry.Default.WithLabel(row.Label.Text ?? "").WithKind((row.Kind.SelectedItem as ComboBoxItem)?.Tag as string ?? GamePanelEntryKinds.Text).WithValue(row.Value.Text ?? "")).ToList();
        var updated = panel.WithTitle(title).WithVisibleFlag(string.IsNullOrWhiteSpace(_flag.Text) ? null : _flag.Text.Trim()).WithEntries(entries);
        _workspace.Apply(Edits.SetGamePanels(project.GamePanels.OrEmpty().Select(existing => existing.Id == panel.Id ? updated : existing)));
        _message.Text = "Panel saved.";
    }
}
