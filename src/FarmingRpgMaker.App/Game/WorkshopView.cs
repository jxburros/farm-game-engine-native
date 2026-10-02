using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using FarmEngine.Authoring;
using FarmingRpgMaker.App.Projects;

namespace FarmingRpgMaker.App.Game;

/// <summary>Creates ordinary editable content from the F# creator patterns in one undo step.</summary>
public sealed class WorkshopView : UserControl, IRetirable
{
    /// <summary>"Build your game" shortcuts: web editor tab keys and their labels (web CreatorWorkshop).</summary>
    private static readonly (string Tab, string Label)[] QuickLinks =
    [
        ("assets", "Import art"), ("scenes", "Create scenes"), ("crops", "Crops & seeds"), ("wildlife", "Animals & fish"),
        ("items", "Inventory items"), ("craft", "Recipes & stations"), ("npcs", "Characters & dialogue"), ("quests", "Quests"),
        ("events", "Story events"), ("actions", "Magic & minigames"), ("problems", "Check project"),
    ];

    private readonly ProjectWorkspace _workspace;
    private readonly Action<string> _openEditor;
    private readonly ComboBox _patterns = new() { Name = "WorkshopPattern", MinWidth = 240 };
    private readonly ComboBox _npc = new() { Name = "WorkshopNpc", MinWidth = 180 };
    private readonly TextBox _name = new() { Name = "WorkshopName", Watermark = "Name" };
    private readonly TextBox _text = new() { Name = "WorkshopText", Watermark = "Story text or description", AcceptsReturn = true, MinHeight = 80, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBox _x = new() { Name = "WorkshopX", Text = "1", Width = 65 };
    private readonly TextBox _y = new() { Name = "WorkshopY", Text = "1", Width = 65 };
    private readonly TextBox _day = new() { Name = "WorkshopDay", Text = "2", Width = 65 };
    private readonly TextBox _friendship = new() { Name = "WorkshopFriendship", Text = "500", Width = 75 };
    private readonly CheckBox _consequences = new() { Name = "WorkshopConsequences", Content = "Remember the player's promise", IsChecked = true };
    private readonly TextBlock _help = Ui.Wrapped("", "muted", "small");
    private readonly TextBlock _message = Ui.Wrapped("", "muted", "small");

    public WorkshopView(ProjectWorkspace workspace, Action<string> openEditor)
    {
        _workspace = workspace;
        _openEditor = openEditor;
        Name = "WorkshopView";
        _message.Name = "WorkshopMessage";
        Ui.Label((_patterns, "Pattern"), (_name, "Name"), (_text, "Story text or description"), (_x, "Scene tile X"), (_y, "Scene tile Y"),
            (_day, "Delivery day"), (_friendship, "Friendship"), (_npc, "Character"));
        foreach (var pattern in Patterns.All) _patterns.Items.Add(new ComboBoxItem { Content = pattern.Name, Tag = pattern });
        _patterns.SelectionChanged += (_, _) =>
        {
            _help.Text = (_patterns.SelectedItem as ComboBoxItem)?.Tag is PatternInfo info ? info.Help : "";
            _message.Text = "";
        };
        _patterns.SelectedIndex = 0;

        var form = new StackPanel { Spacing = 12, Margin = new Thickness(20), MaxWidth = 760 };
        form.Children.Add(Ui.Text("CREATOR WORKSHOP", "section"));
        form.Children.Add(Ui.Wrapped("Start with a working interaction. Each pattern creates editable content and undoes as one step.", "muted", "small"));
        form.Children.Add(_patterns);
        form.Children.Add(_help);
        form.Children.Add(Ui.Text("Name", "muted", "small"));
        form.Children.Add(_name);
        form.Children.Add(Ui.Text("Story text or description", "muted", "small"));
        form.Children.Add(_text);
        form.Children.Add(Ui.HStack(8, Ui.Text("Scene tile", "muted", "small"), _x, Ui.Text(","), _y));
        form.Children.Add(Ui.HStack(8, Ui.Text("Delivery day", "muted", "small"), _day,
            Ui.Text("Friendship", "muted", "small"), _friendship));
        form.Children.Add(Ui.HStack(8, Ui.Text("Character", "muted", "small"), _npc));
        form.Children.Add(_consequences);
        var create = Ui.Button("Create pattern", Create, "accent");
        create.Name = "CreatePatternButton";
        var open = Ui.Button("Open its editor", OpenEditor, "tool");
        form.Children.Add(Ui.HStack(8, create, open));
        form.Children.Add(_message);

        form.Children.Add(Ui.Text("BUILD YOUR GAME", "section"));
        var links = new WrapPanel { Name = "WorkshopLinks" };
        foreach (var (tab, label) in QuickLinks)
        {
            var link = Ui.Button(label, () => _openEditor(tab), "tool");
            link.Name = $"WorkshopLink_{tab}";
            link.Margin = new Thickness(0, 0, 6, 6);
            links.Children.Add(link);
        }
        form.Children.Add(links);
        Content = new ScrollViewer { Content = form };
        _workspace.ProjectChanged += OnProjectChanged;
        Refresh();
    }

    /// <summary>Stops following the project (the editor that built this view was replaced).</summary>
    public void Retire() => _workspace.ProjectChanged -= OnProjectChanged;

    private void OnProjectChanged(object? sender, ProjectChangedEventArgs e)
    {
        if (IsEffectivelyVisible) Refresh();
    }

    public void Refresh()
    {
        var selected = (_npc.SelectedItem as ComboBoxItem)?.Tag as string;
        _npc.SelectedItem = null;
        _npc.Items.Clear();
        _npc.Items.Add(new ComboBoxItem { Content = "Choose a character", Tag = "" });
        foreach (var npc in _workspace.Current?.Npcs ?? []) _npc.Items.Add(new ComboBoxItem { Content = npc.Name, Tag = npc.Id });
        _npc.SelectedItem = _npc.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, selected)) ?? _npc.Items[0];
    }

    private static int Parse(TextBox box) => int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0
        ? value : throw new FormatException($"{box.Name} must be a nonnegative whole number.");

    private void Create()
    {
        if (_workspace.Current is not { } project || (_patterns.SelectedItem as ComboBoxItem)?.Tag is not PatternInfo pattern) return;
        try
        {
            var options = Patterns.Options(_name.Text ?? "", _text.Text ?? "", Parse(_x), Parse(_y), Parse(_day),
                (_npc.SelectedItem as ComboBoxItem)?.Tag as string ?? "", Parse(_friendship), _consequences.IsChecked == true);
            var result = Patterns.Build(pattern.Kind, options, project);
            if (!result.Ok || result.Edit is null) { _message.Text = result.Error ?? "This pattern could not be created."; return; }
            _workspace.Apply(result.Edit);
            _message.Text = Patterns.SuccessMessage(pattern.Kind, options.Name);
        }
        catch (FormatException error) { _message.Text = error.Message; }
    }

    private void OpenEditor()
    {
        if ((_patterns.SelectedItem as ComboBoxItem)?.Tag is PatternInfo pattern) _openEditor(pattern.Tab);
    }
}
