using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FarmingRpgMaker.App.Game;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

/// <summary>
/// Screen readers can name every control of the editor (web: labels and aria-labels on every
/// input): each button, box, picker, check box and list a screen reader reaches has an accessible
/// name with words in it, not just "×" or "↑".
/// </summary>
public sealed class AccessibilityTests
{
    private static readonly string[] ContentCategories =
    [
        "NPCs", "Dialogue", "Items", "Crops", "Quests", "Events", "Shops", "Recipes", "Node types",
        "Machine types", "Animal species", "Fish tables", "Actions", "Minigames",
    ];

    /// <summary>The unnamed controls under <paramref name="root"/>, described for the failure message.</summary>
    private static IEnumerable<string> Unnamed(Visual root, string where) =>
        root.GetVisualDescendants()
            .OfType<Control>()
            // Parts of another control's template (a ComboBox's toggle, a scroll bar's buttons)
            // are named by their owner.
            .Where(control => control.IsEffectivelyVisible && control.TemplatedParent is null)
            .Where(control => control is Button or ToggleButton or TextBox or ComboBox or ListBox or Slider)
            .Select(control => (control, name: ControlAutomationPeer.CreatePeerForElement(control).GetName()))
            .Where(pair => string.IsNullOrWhiteSpace(pair.name) || !pair.name.Any(char.IsLetterOrDigit))
            .Select(pair => $"{where}: {pair.control.GetType().Name} \"{pair.control.Name}\" named \"{pair.name}\"");

    [AvaloniaFact]
    public void EveryEditorControlHasAnAccessibleName()
    {
        using var host = new GameTestHost(1600, 1000);
        var tabs = FindByName<TabControl>(host.Window, "EditorTabs");
        var missing = new List<string>();
        for (var index = 0; index < tabs.ItemCount; index++)
        {
            tabs.SelectedIndex = index;
            Pump();
            var header = (tabs.Items[index] as TabItem)?.Header as string ?? index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            missing.AddRange(Unnamed(host.Window, header));
        }

        // Every content form, with its first entry open.
        tabs.SelectedIndex = 1;
        Pump();
        var content = FindByName<ContentEditorView>(host.Window, "ContentEditorView");
        foreach (var category in ContentCategories)
        {
            content.SelectCategory(category);
            Pump();
            var entries = FindByName<ListBox>(host.Window, "ContentEntities");
            if (entries.ItemCount > 0)
            {
                entries.SelectedIndex = 0;
                Pump();
            }

            missing.AddRange(Unnamed(content, $"Content · {category}"));
        }

        var distinct = missing.Distinct().ToList();
        Assert.True(distinct.Count == 0, $"{distinct.Count} controls have no accessible name:\n{string.Join("\n", distinct)}");
    }
}
