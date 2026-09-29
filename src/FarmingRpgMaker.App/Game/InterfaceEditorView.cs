using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using FarmEngine.Authoring;
using FarmEngine.Authoring.Net;
using FarmEngine.Schemas;
using FarmingRpgMaker.App.Projects;

namespace FarmingRpgMaker.App.Game;

/// <summary>Creator-defined in-game panels and their live entries.</summary>
public sealed class InterfaceEditorView : UserControl
{
    /// <summary>
    /// One entry: its label, its kind, and the value control that kind needs in
    /// <paramref name="Value"/> (empty for live counters).
    /// </summary>
    private sealed record EntryRow(TextBox Label, ComboBox Kind, ContentControl Value, Button Remove, Control Control);
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
        right.Children.Add(Ui.Wrapped("Kinds: text shows what you type; money, energy and day show live values; item counts that item in the bag; flag shows Yes or No for a story flag; action adds a button that runs the action.", "muted", "small"));
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
        var valueHost = new ContentControl { Name = "InterfaceEntryValueHost" };
        ShowValue(valueHost, ValueControl(KindOf(kind), entry.Value));
        EntryRow? row = null;
        var remove = Ui.Button("×", () =>
        {
            if (row is null) return;
            _rows.Remove(row);
            _entries.Children.Remove(row.Control);
            Renumber();
        }, "tool", "small");
        remove.Name = "InterfaceEntryRemove";
        ToolTip.SetTip(remove, "Remove this entry");
        // A new kind needs a different value: the web clears it too.
        kind.SelectionChanged += (_, _) =>
        {
            ShowValue(valueHost, ValueControl(KindOf(kind), ""));
            Renumber();
        };
        row = new EntryRow(label, kind, valueHost, remove, Ui.HStack(8, label, kind, valueHost, remove));
        _rows.Add(row);
        _entries.Children.Add(row.Control);
        Renumber();
    }

    /// <summary>Puts <paramref name="control"/> in the row; a kind without a value takes no room.</summary>
    private static void ShowValue(ContentControl host, Control? control)
    {
        host.Content = control;
        host.IsVisible = control is not null;
    }

    private static string KindOf(ComboBox kind) => (kind.SelectedItem as ComboBoxItem)?.Tag as string ?? GamePanelEntryKinds.Text;

    /// <summary>
    /// The control an entry of <paramref name="kind"/> edits its value with: a picker of the
    /// project's items or actions (an unknown id shows as "(missing: id)"), a text box for text
    /// and flag names, or nothing for the live counters (money, energy, day).
    /// </summary>
    private Control? ValueControl(string kind, string value) => kind switch
    {
        GamePanelEntryKinds.Item => Picker("item", "Choose item", value),
        GamePanelEntryKinds.Action => Picker("action", "Choose action", value),
        GamePanelEntryKinds.Text => new TextBox { Name = "InterfaceEntryValue", Text = value, Watermark = "Text", MinWidth = 150 },
        GamePanelEntryKinds.Flag => new TextBox { Name = "InterfaceEntryValue", Text = value, Watermark = "Flag name", MinWidth = 150 },
        _ => null,
    };

    private ComboBox Picker(string reference, string placeholder, string value)
    {
        var picker = new ComboBox { Name = "InterfaceEntryValue", MinWidth = 150, PlaceholderText = placeholder, MaxDropDownHeight = 320 };
        if (_workspace.Current is { } project)
        {
            var field = ContentForms.Field("GamePanelEntry", "Value", "Value") ?? throw new InvalidOperationException("GamePanelEntry.Value is not declared.");
            foreach (var option in ContentForms.Entries(field, ContentForms.Options(reference, project), value))
            {
                var item = new ComboBoxItem { Content = option.Label, Tag = option.Id };
                if (option.Missing) item.Classes.Add("missing");
                picker.Items.Add(item);
            }
        }

        picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, value));
        return picker;
    }

    private static string ValueOf(EntryRow row) => row.Value.Content switch
    {
        TextBox box => box.Text ?? "",
        ComboBox picker => (picker.SelectedItem as ComboBoxItem)?.Tag as string ?? "",
        _ => "",
    };

    /// <summary>Screen reader names that follow the rows' order ("Entry 2 item", "Remove entry 2").</summary>
    private void Renumber()
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var number = i + 1;
            AutomationProperties.SetName(row.Label, $"Entry {number} label");
            AutomationProperties.SetName(row.Kind, $"Entry {number} kind");
            AutomationProperties.SetName(row.Remove, $"Remove entry {number}");
            if (row.Value.Content is Control value)
            {
                var what = KindOf(row.Kind) == GamePanelEntryKinds.Flag ? "flag name" : KindOf(row.Kind);
                AutomationProperties.SetName(value, $"Entry {number} {what}");
            }
        }
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
        var entries = _rows.Select(row => GamePanelEntry.Default.WithLabel(row.Label.Text ?? "").WithKind(KindOf(row.Kind)).WithValue(ValueOf(row))).ToList();
        var updated = panel.WithTitle(title).WithVisibleFlag(string.IsNullOrWhiteSpace(_flag.Text) ? null : _flag.Text.Trim()).WithEntries(entries);
        _workspace.Apply(Edits.SetGamePanels(project.GamePanels.OrEmpty().Select(existing => existing.Id == panel.Id ? updated : existing)));
        _message.Text = "Panel saved.";
    }
}
