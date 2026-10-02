using Avalonia.Automation;
using Avalonia.Controls;

namespace FarmingRpgMaker.App.Game;

/// <summary>The layout pieces <see cref="ContentForm"/> builds its fields and rows from.</summary>
internal static class ContentFormLayout
{
    internal static TextBlock Label(string text) => Ui.Text(text, "muted", "small");

    /// <summary>Gives <paramref name="control"/> the name screen readers announce (its visible label).</summary>
    internal static T Accessible<T>(T control, string name) where T : Control
    {
        AutomationProperties.SetName(control, name);
        return control;
    }

    /// <summary>Controls side by side in <paramref name="columns"/> (Grid column definitions).</summary>
    internal static Grid Columns(string columns, params Control[] children)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), ColumnSpacing = 6 };
        for (var i = 0; i < children.Length; i++)
        {
            Grid.SetColumn(children[i], i);
            grid.Children.Add(children[i]);
        }

        return grid;
    }

    internal static StackPanel Labeled(string label, Control control, double width = 0)
    {
        var stack = Ui.VStack(3, Label(label), control);
        if (width > 0) stack.Width = width;
        return stack;
    }

    internal static Border ListRow(Control header, Control body)
    {
        var stack = Ui.VStack(6, header, body);
        return new Border { Child = stack }.WithClasses("row");
    }

    internal static Grid RowHeader(Control title, Control buttons)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(title);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);
        return grid;
    }

    internal static TextBlock EmptyNote(string path, string text)
    {
        var note = Ui.Wrapped(text, "muted", "small");
        note.Name = $"ContentEmpty_{path}";
        return note;
    }

    internal static Control NotAnObject(Control buttons) =>
        Columns("*,Auto", Ui.Wrapped("This entry is not an object; use Edit as JSON.", "muted", "small"), buttons);
}
