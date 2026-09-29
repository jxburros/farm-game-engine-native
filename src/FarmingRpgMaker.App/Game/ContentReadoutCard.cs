using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using FarmEngine.Authoring;

namespace FarmingRpgMaker.App.Game;

/// <summary>
/// Read-only readouts beside a content form (web CropEditor's summary card and profit line). F#
/// <see cref="ContentReadouts"/> computes and formats the values; these only lay them out.
/// </summary>
internal static class ContentReadoutCard
{
    /// <summary>A card of label/value tiles, two per row: the label muted, the value bold.</summary>
    public static Border Card(string name, IEnumerable<ReadoutLine> lines)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var index = 0;
        foreach (var line in lines)
        {
            if (index % 2 == 0) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var value = Ui.Wrapped(line.Value);
            value.FontWeight = FontWeight.Bold;
            var tile = Ui.VStack(1, Ui.Text(line.Label, "muted", "small"), value);
            tile.Margin = new Thickness(0, 2, 12, 6);
            AutomationProperties.SetName(tile, $"{line.Label}: {line.Value}");
            Grid.SetRow(tile, index / 2);
            Grid.SetColumn(tile, index % 2);
            grid.Children.Add(tile);
            index++;
        }

        return new Border { Name = name, Child = grid }.WithClasses("row");
    }

    /// <summary>A one-line readout under a form ("Profit per harvest: $15"); set its text later.</summary>
    public static (Border Box, TextBlock Text) Line(string name)
    {
        var text = Ui.Wrapped("");
        text.Name = name;
        text.FontWeight = FontWeight.SemiBold;
        return (new Border { Child = text }.WithClasses("row"), text);
    }
}
