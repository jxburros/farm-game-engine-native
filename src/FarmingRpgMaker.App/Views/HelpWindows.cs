using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FarmingRpgMaker.App.Controls;
using FarmingRpgMaker.App.Game;
using FarmingRpgMaker.App.Localization;
using FarmingRpgMaker.App.Services;

namespace FarmingRpgMaker.App.Views;

/// <summary>The in-app help: the creator guide (docs/CREATOR-GUIDE.md, embedded) and links.</summary>
public static class HelpContent
{
    /// <summary>Manifest name of the embedded creator guide (see the app's .csproj).</summary>
    public const string CreatorGuideResource = "FarmingRpgMaker.App.CreatorGuide.md";

    /// <summary>Where relative links of the guide point (the repository's docs folder).</summary>
    public const string DocsBaseUrl = "https://github.com/jxburros/farm-game-engine-native/blob/main/docs/";

    /// <summary>The creator guide's Markdown.</summary>
    public static string CreatorGuideMarkdown()
    {
        using var stream = typeof(HelpContent).Assembly.GetManifestResourceStream(CreatorGuideResource)
            ?? throw new InvalidOperationException($"The creator guide resource {CreatorGuideResource} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>A link of the guide as a URL to open: web links as they are, docs relative to the repository.</summary>
    public static string ResolveLink(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var relative = url.StartsWith("docs/", StringComparison.Ordinal) ? url["docs/".Length..] : url.TrimStart('.', '/');
        return DocsBaseUrl + relative;
    }
}

/// <summary>Shared look of the help windows: a sized, closable tool window with a footer.</summary>
public abstract class HelpWindowBase : Window
{
    protected HelpWindowBase(string name, string title, double width, double height)
    {
        Name = name;
        Title = title;
        Width = width;
        Height = height;
        MinWidth = 420;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Icon = MainWindowIcon();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    /// <summary>A footer with <paramref name="buttons"/> on the right.</summary>
    protected static Border Footer(params Control[] buttons)
    {
        var stack = Ui.HStack(8, buttons);
        stack.HorizontalAlignment = HorizontalAlignment.Right;
        return new Border { Child = stack, Padding = new Thickness(16, 10) }.WithClasses("statusbar");
    }

    protected Button CloseButton(string name)
    {
        var close = Ui.Button(EditorStrings.Get("common.close"), Close, "accent");
        close.Name = name;
        close.MinWidth = 88;
        close.HorizontalContentAlignment = HorizontalAlignment.Center;
        close.IsCancel = true;
        return close;
    }

    private static WindowIcon? MainWindowIcon()
    {
        try
        {
            return new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://FarmingRpgMaker/Assets/app.ico")));
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// The first-run welcome (web <c>WelcomeDialog</c>): four steps from playtesting to exporting,
/// adapted to the desktop editor, and a link to the full creator guide.
/// </summary>
public sealed class WelcomeWindow : HelpWindowBase
{
    public static readonly IReadOnlyList<(string Icon, string Key)> Steps =
    [
        ("IconPlay", "welcome.play"),
        ("IconMap", "welcome.edit"),
        ("IconStar", "welcome.content"),
        ("IconPackage", "welcome.share"),
    ];

    public WelcomeWindow()
        : base("WelcomeWindow", EditorStrings.Get("welcome.title"), 560, 600)
    {
        SizeToContent = SizeToContent.Height;
        CanResize = false;

        var header = Ui.VStack(
            4,
            new Image
            {
                Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://FarmingRpgMaker/Assets/app-icon-256.png"))),
                Width = 64,
                Height = 64,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 6),
            },
            Ui.Wrapped(EditorStrings.Get("welcome.title"), "h1"),
            Ui.Wrapped(EditorStrings.Get("welcome.subtitle"), "muted"));

        var steps = new StackPanel { Name = "WelcomeSteps", Spacing = 14, Margin = new Thickness(0, 18, 0, 0) };
        foreach (var (icon, key) in Steps)
        {
            var badge = new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(18),
                Background = new SolidColorBrush(Color.Parse("#2e2a25")),
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Top,
                Child = Ui.Icon(icon, 18),
            };
            badge.Child.HorizontalAlignment = HorizontalAlignment.Center;
            badge.Child.VerticalAlignment = VerticalAlignment.Center;
            var text = Ui.VStack(2, Ui.Wrapped(EditorStrings.Get(key + ".title"), "h3"), Ui.Wrapped(EditorStrings.Get(key + ".body"), "muted", "small"));
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
            row.Children.Add(badge);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            steps.Children.Add(row);
        }

        var guide = new HyperlinkButton { Name = "WelcomeGuideLink", Content = EditorStrings.Get("welcome.guide"), VerticalAlignment = VerticalAlignment.Center };
        guide.Click += (_, _) => GuideRequested?.Invoke(this, EventArgs.Empty);
        var start = Ui.Button(EditorStrings.Get("welcome.start"), Close, "accent");
        start.Name = "WelcomeStartButton";
        start.IsDefault = true;
        start.IsCancel = true;
        start.MinWidth = 110;
        start.HorizontalContentAlignment = HorizontalAlignment.Center;
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        footer.Children.Add(guide);
        Grid.SetColumn(start, 1);
        footer.Children.Add(start);

        Content = new StackPanel
        {
            Children =
            {
                new Border { Padding = new Thickness(28, 24, 28, 20), Child = Ui.VStack(0, header, steps) },
                new Border { Child = footer, Padding = new Thickness(20, 10) }.WithClasses("statusbar"),
            },
        };
    }

    /// <summary>"Full creator guide" was clicked; the owner opens the guide.</summary>
    public event EventHandler? GuideRequested;
}

/// <summary>Help → Creator Guide: docs/CREATOR-GUIDE.md rendered in the app.</summary>
public sealed class CreatorGuideWindow : HelpWindowBase
{
    public CreatorGuideWindow(IUrlLauncher launcher)
        : base("CreatorGuideWindow", EditorStrings.Get("guide.title"), 760, 720)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        Guide = new MarkdownView { Name = "CreatorGuideView", Markdown = HelpContent.CreatorGuideMarkdown() };
        Guide.LinkClicked += (_, url) => launcher.Open(HelpContent.ResolveLink(url));
        var footer = Footer(CloseButton("CreatorGuideCloseButton"));
        DockPanel.SetDock(footer, Dock.Bottom);
        Content = new DockPanel
        {
            Children =
            {
                footer,
                new ScrollViewer { Name = "CreatorGuideScroller", Content = new Border { Padding = new Thickness(28, 20), Child = Guide } },
            },
        };
    }

    public MarkdownView Guide { get; }
}

/// <summary>Help → Keyboard Shortcuts: the editor's shortcuts and the game's default keys.</summary>
public sealed class ShortcutsWindow : HelpWindowBase
{
    /// <summary>Editor shortcuts (keys, description key).</summary>
    public static readonly IReadOnlyList<(string Keys, string Key)> EditorShortcuts =
    [
        ("Ctrl+N", "shortcuts.newProject"),
        ("Ctrl+O", "shortcuts.openProject"),
        ("Ctrl+Shift+E", "shortcuts.exportGame"),
        ("F5", "shortcuts.playMode"),
        ("F6", "shortcuts.editMode"),
        ("Ctrl+Z", "shortcuts.undo"),
        ("Ctrl+Y / Ctrl+Shift+Z", "shortcuts.redo"),
        ("←↑→↓", "shortcuts.mapCursor"),
        ("Enter / Space", "shortcuts.mapApply"),
        ("Esc", "shortcuts.mapCancel"),
        ("V B R G I M E X U D P", "shortcuts.mapTools"),
        ("Ctrl+K", "shortcuts.quickOpen"),
        ("Ctrl+R · Ctrl+Shift+K · Ctrl+D", "shortcuts.playToolbar"),
        ("F1", "shortcuts.guide"),
        ("Alt+F4", "shortcuts.exit"),
    ];

    /// <summary>The game's default keys (farm-ui's bindings; docs/PLAYER.md "Controls").</summary>
    public static readonly IReadOnlyList<(string Keys, string Key)> GameKeys =
    [
        ("WASD / ←↑→↓", "shortcuts.move"),
        ("E / Space / Enter", "shortcuts.interact"),
        ("Q · T · R · F · C", "shortcuts.tools"),
        ("X", "shortcuts.craft"),
        ("Z", "shortcuts.sleep"),
        ("I", "shortcuts.inventory"),
        ("J", "shortcuts.quests"),
        ("Esc", "shortcuts.menu"),
        ("1–9", "shortcuts.options"),
    ];

    public ShortcutsWindow()
        : base("ShortcutsWindow", EditorStrings.Get("shortcuts.title"), 560, 640)
    {
        var list = new StackPanel { Name = "ShortcutList", Spacing = 6 };
        Section(list, "shortcuts.editor", EditorShortcuts);
        Section(list, "shortcuts.game", GameKeys);
        list.Children.Add(Ui.Wrapped(EditorStrings.Get("shortcuts.rebind"), "muted", "small"));
        var footer = Footer(CloseButton("ShortcutsCloseButton"));
        DockPanel.SetDock(footer, Dock.Bottom);
        Content = new DockPanel
        {
            Children =
            {
                footer,
                new ScrollViewer { Content = new Border { Padding = new Thickness(24, 18), Child = list } },
            },
        };
    }

    private static void Section(StackPanel list, string titleKey, IReadOnlyList<(string Keys, string Key)> rows)
    {
        var title = Ui.Text(EditorStrings.Get(titleKey), "section");
        title.Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 12, 0, 2);
        list.Children.Add(title);
        foreach (var (keys, key) in rows)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("190,*"), ColumnSpacing = 12 };
            var cap = new Border
            {
                Child = Ui.Text(keys),
                Padding = new Thickness(8, 3),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.Parse("#55504a")),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            var what = Ui.Wrapped(EditorStrings.Get(key));
            Grid.SetColumn(what, 1);
            row.Children.Add(cap);
            row.Children.Add(what);
            list.Children.Add(row);
        }
    }
}
