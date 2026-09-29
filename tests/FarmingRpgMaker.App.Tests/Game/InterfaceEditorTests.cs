using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using FarmEngine.Authoring;
using FarmEngine.Authoring.Net;
using FarmEngine.Schemas;
using FarmingRpgMaker.App.Game;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

/// <summary>Game panel entries (web InterfaceEditor.tsx): each kind edits its value with its own control.</summary>
public sealed class InterfaceEditorTests
{
    private static void OpenInterface(GameTestHost host)
    {
        FindByName<TabControl>(host.Window, "EditorTabs").SelectedIndex = 7;
        Pump();
    }

    private static void Press(Control root, string name) =>
        FindByName<Button>(root, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static Control Row(GameTestHost host, int index) => (Control)FindByName<StackPanel>(host.Window, "InterfaceEntries").Children[index];

    private static Control? ValueControl(Control row) => FindByName<ContentControl>(row, "InterfaceEntryValueHost").Content as Control;

    private static void Choose(ComboBox box, string tag)
    {
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, tag));
        Pump();
    }

    private static GamePanel AddPanel(GameTestHost host)
    {
        OpenInterface(host);
        Press(host.Window, "AddInterfacePanelButton");
        return host.Workspace.Current!.GamePanels.OrEmpty().Last();
    }

    [AvaloniaFact]
    public void ChangingTheKindSwapsTheValueControlAndClearsTheValue()
    {
        using var host = new GameTestHost();
        AddPanel(host);
        Press(host.Window, "AddInterfaceEntryButton");
        var row = Row(host, 0);
        Assert.Equal("Entry 1 label", AutomationProperties.GetName(FindByName<TextBox>(row, "InterfaceEntryLabel")));
        var kind = FindByName<ComboBox>(row, "InterfaceEntryKind");
        Assert.Equal("Entry 1 kind", AutomationProperties.GetName(kind));
        Assert.Equal("Remove entry 1", AutomationProperties.GetName(FindByName<Button>(row, "InterfaceEntryRemove")));
        var text = Assert.IsType<TextBox>(ValueControl(row));
        Assert.Equal("Text", text.Watermark);
        Assert.Equal("Entry 1 text", AutomationProperties.GetName(text));
        text.Text = "Hello";

        foreach (var counter in new[] { "money", "energy", "day" })
        {
            Choose(kind, counter);
            Assert.Null(ValueControl(row));
        }

        Choose(kind, "flag");
        var flag = Assert.IsType<TextBox>(ValueControl(row));
        Assert.Equal("Flag name", flag.Watermark);
        Assert.Equal("", flag.Text ?? "");
        Assert.Equal("Entry 1 flag name", AutomationProperties.GetName(flag));

        var project = host.Workspace.Current!;
        Choose(kind, "item");
        var items = Assert.IsType<ComboBox>(ValueControl(row));
        Assert.Equal("Entry 1 item", AutomationProperties.GetName(items));
        Assert.Null(items.SelectedItem);
        Assert.Equal(ContentForms.Options("item", project).Select(option => option.Id), items.Items.OfType<ComboBoxItem>().Select(item => item.Tag));
        Assert.Equal(ContentForms.Options("item", project).Select(option => option.Label), items.Items.OfType<ComboBoxItem>().Select(item => item.Content));

        Choose(kind, "action");
        var actions = Assert.IsType<ComboBox>(ValueControl(row));
        Assert.Equal("Entry 1 action", AutomationProperties.GetName(actions));
        var available = ContentForms.Options("action", project);
        Assert.NotEmpty(available);
        Assert.Equal(available.Select(option => option.Id), actions.Items.OfType<ComboBoxItem>().Select(item => item.Tag));
        Assert.Equal(available.Select(option => option.Label), actions.Items.OfType<ComboBoxItem>().Select(item => item.Content));
        Assert.Same(project, host.Workspace.Current);
    }

    [AvaloniaFact]
    public void ItemAndActionChoicesSaveAsOneUndoStep()
    {
        using var host = new GameTestHost();
        var panel = AddPanel(host);
        Press(host.Window, "AddInterfaceEntryButton");
        Press(host.Window, "AddInterfaceEntryButton");
        Press(host.Window, "AddInterfaceEntryButton");
        var itemId = ContentForms.Options("item", host.Workspace.Current!)[0].Id;
        var actionId = ContentForms.Options("action", host.Workspace.Current!)[^1].Id;
        FindByName<TextBox>(Row(host, 0), "InterfaceEntryLabel").Text = "Seeds";
        Choose(FindByName<ComboBox>(Row(host, 0), "InterfaceEntryKind"), "item");
        Choose(Assert.IsType<ComboBox>(ValueControl(Row(host, 0))), itemId);
        FindByName<TextBox>(Row(host, 1), "InterfaceEntryLabel").Text = "Bless";
        Choose(FindByName<ComboBox>(Row(host, 1), "InterfaceEntryKind"), "action");
        Choose(Assert.IsType<ComboBox>(ValueControl(Row(host, 1))), actionId);
        Choose(FindByName<ComboBox>(Row(host, 2), "InterfaceEntryKind"), "money");
        var before = host.Workspace.Current;

        Press(host.Window, "SaveInterfacePanelButton");
        var saved = host.Workspace.Current!.GamePanels.OrEmpty().Single(p => p.Id == panel.Id);
        Assert.Equal(
            [("Seeds", "item", itemId), ("Bless", "action", actionId), ("", "money", "")],
            saved.Entries.Select(entry => (entry.Label, entry.Kind, entry.Value)));

        // Reopening shows the saved choices.
        FindByName<InterfaceEditorView>(host.Window, "InterfaceEditorView").Refresh();
        Assert.Equal(itemId, (Assert.IsType<ComboBox>(ValueControl(Row(host, 0))).SelectedItem as ComboBoxItem)?.Tag);
        Assert.Equal(actionId, (Assert.IsType<ComboBox>(ValueControl(Row(host, 1))).SelectedItem as ComboBoxItem)?.Tag);
        Assert.Null(ValueControl(Row(host, 2)));

        host.Workspace.Undo();
        Assert.Same(before, host.Workspace.Current);
    }

    [AvaloniaFact]
    public void AnUnknownIdStaysVisibleAndSaved()
    {
        using var host = new GameTestHost();
        var panel = AddPanel(host);
        var ghost = panel.WithEntries([GamePanelEntry.Default.WithLabel("Ghost").WithKind("item").WithValue("ghost-item")]);
        host.Workspace.Apply(Edits.SetGamePanels(host.Workspace.Current!.GamePanels.OrEmpty().Select(p => p.Id == panel.Id ? ghost : p)));
        var picker = Assert.IsType<ComboBox>(ValueControl(Row(host, 0)));
        var selected = Assert.IsType<ComboBoxItem>(picker.SelectedItem);
        Assert.Equal("(missing: ghost-item)", selected.Content);
        Assert.Contains("missing", selected.Classes);

        FindByName<TextBox>(Row(host, 0), "InterfaceEntryLabel").Text = "Still here";
        Press(host.Window, "SaveInterfacePanelButton");
        var entry = Assert.Single(host.Workspace.Current!.GamePanels.OrEmpty().Single(p => p.Id == panel.Id).Entries);
        Assert.Equal(("Still here", "item", "ghost-item"), (entry.Label, entry.Kind, entry.Value));
    }

    [AvaloniaFact]
    public void RemovingAnEntryRenumbersTheRest()
    {
        using var host = new GameTestHost();
        var panel = AddPanel(host);
        Press(host.Window, "AddInterfaceEntryButton");
        Press(host.Window, "AddInterfaceEntryButton");
        FindByName<TextBox>(Row(host, 0), "InterfaceEntryLabel").Text = "First";
        FindByName<TextBox>(Row(host, 1), "InterfaceEntryLabel").Text = "Second";
        Press(Row(host, 0), "InterfaceEntryRemove");
        var row = Row(host, 0);
        Assert.Single(FindByName<StackPanel>(host.Window, "InterfaceEntries").Children);
        Assert.Equal("Second", FindByName<TextBox>(row, "InterfaceEntryLabel").Text);
        Assert.Equal("Entry 1 label", AutomationProperties.GetName(FindByName<TextBox>(row, "InterfaceEntryLabel")));
        Assert.Equal("Remove entry 1", AutomationProperties.GetName(FindByName<Button>(row, "InterfaceEntryRemove")));
        Assert.Equal("Entry 1 text", AutomationProperties.GetName(ValueControl(row)!));

        Press(host.Window, "SaveInterfacePanelButton");
        Assert.Equal(["Second"], host.Workspace.Current!.GamePanels.OrEmpty().Single(p => p.Id == panel.Id).Entries.Select(entry => entry.Label));
    }
}
