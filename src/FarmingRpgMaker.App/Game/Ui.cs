using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace FarmingRpgMaker.App.Game;

/// <summary>Small factory helpers for the code-built game UI (styles live in GameStyles.axaml).</summary>
internal static class Ui
{
    public static TextBlock Text(string text, params string[] classes)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in classes)
        {
            block.Classes.Add(c);
        }

        return block;
    }

    public static TextBlock Wrapped(string text, params string[] classes)
    {
        var block = Text(text, classes);
        block.TextWrapping = TextWrapping.Wrap;
        return block;
    }

    public static Button Button(object content, Action onClick, params string[] classes)
    {
        var button = new Button { Content = content, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in classes)
        {
            button.Classes.Add(c);
        }

        button.Click += (_, _) => onClick();
        return button;
    }

    public static PathIcon Icon(string resourceKey, double size = 16)
    {
        var icon = new PathIcon { Width = size, Height = size };
        if (Application.Current?.TryFindResource(resourceKey, out var geometry) == true && geometry is Geometry g)
        {
            icon.Data = g;
        }

        return icon;
    }

    /// <summary>Icon + label content for buttons.</summary>
    public static StackPanel IconLabel(string iconKey, string text, double iconSize = 14)
    {
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { Icon(iconKey, iconSize), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
        };
    }

    public static StackPanel HStack(double spacing, params Control[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        panel.Children.AddRange(children);
        return panel;
    }

    public static StackPanel VStack(double spacing, params Control[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Spacing = spacing };
        panel.Children.AddRange(children);
        return panel;
    }

    public static Border WithClasses(this Border border, params string[] classes)
    {
        foreach (var c in classes)
        {
            border.Classes.Add(c);
        }

        return border;
    }

    public static Border Classed(Control child, params string[] classes) => new Border { Child = child }.WithClasses(classes);

    /// <summary>
    /// Modal card scaffold (web Card with header / scroll body / optional footer): icon tile,
    /// title + subtitle, close button.
    /// </summary>
    public static Border ModalCard(string name, string iconKey, string title, string? subtitle, Control body, Action onClose, double width = 560, Control? footer = null, string closeLabel = "Close")
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var iconTile = new Border { Child = Icon(iconKey, 22) }.WithClasses("icon-tile");
        iconTile.Margin = new Thickness(0, 0, 12, 0);
        header.Children.Add(iconTile);
        var titles = VStack(1, Text(title, "h2"));
        if (subtitle is not null)
        {
            var sub = Text(subtitle, "muted", "small");
            sub.Name = name + "Subtitle";
            titles.Children.Add(sub);
        }

        titles.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        var close = new Button { Content = Icon("IconClose", 14), Classes = { "subtle" }, Padding = new Thickness(8), Name = name + "Close" };
        ToolTip.SetTip(close, closeLabel);
        close.Click += (_, _) => onClose();
        Grid.SetColumn(close, 2);
        header.Children.Add(close);

        var layout = new DockPanel();
        var headerBorder = new Border { Child = header }.WithClasses("modal-header");
        DockPanel.SetDock(headerBorder, Dock.Top);
        layout.Children.Add(headerBorder);
        if (footer is not null)
        {
            var footerBorder = new Border { Child = footer }.WithClasses("modal-footer");
            DockPanel.SetDock(footerBorder, Dock.Bottom);
            layout.Children.Add(footerBorder);
        }

        layout.Children.Add(new ScrollViewer
        {
            Content = new Border { Padding = new Thickness(16, 12), Child = body },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        return new Border { Name = name, Child = layout, Width = width, MaxHeight = 620 }.WithClasses("modal");
    }

    /// <summary>A list row: content on the left, actions on the right.</summary>
    public static Border Row(Control main, params Control[] actions)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(main);
        var right = HStack(6, actions);
        right.VerticalAlignment = VerticalAlignment.Center;
        right.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return new Border { Child = grid }.WithClasses("row");
    }

    public static Border Empty(string iconKey, string title, string detail)
    {
        var icon = Icon(iconKey, 48);
        icon.Opacity = 0.3;
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        var stack = VStack(4, icon, Centered(Text(title, "h3")), Centered(Text(detail, "muted", "small")));
        stack.Margin = new Thickness(0, 28);
        return new Border { Child = stack };
    }

    public static TextBlock Centered(TextBlock block)
    {
        block.HorizontalAlignment = HorizontalAlignment.Center;
        block.TextAlignment = TextAlignment.Center;
        return block;
    }

    /// <summary>Money like the web UI (<c>$123</c>).</summary>
    public static string Money(double amount) => "$" + FarmEngine.Json.Js.Num(amount);

    public static string Num(double value) => FarmEngine.Json.Js.Num(value);

    /// <summary>"spring" → "Spring" (CSS <c>capitalize</c>).</summary>
    public static string Capitalize(string? text) =>
        string.IsNullOrEmpty(text) ? "" : char.ToUpperInvariant(text[0]) + text[1..];
}
