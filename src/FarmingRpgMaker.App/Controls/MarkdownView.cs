using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using FarmingRpgMaker.App.Markdown;

namespace FarmingRpgMaker.App.Controls;

/// <summary>
/// Renders Markdown (see <see cref="MarkdownParser"/>) as selectable, wrapped text blocks.
/// Text blocks get the style classes <c>md-h1</c>…<c>md-h3</c>, <c>md-p</c>, <c>md-li</c>,
/// <c>md-quote</c> and <c>md-code</c> so the look is controlled from <c>App.axaml</c>.
/// </summary>
public sealed class MarkdownView : Border
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace");

    static MarkdownView()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Rebuild());
    }

    public MarkdownView()
    {
        Rebuild();
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    /// <summary>The rendered blocks (for tests).</summary>
    public StackPanel Document { get; private set; } = new();

    private void Rebuild()
    {
        var panel = new StackPanel { Spacing = 6 };
        var blocks = MarkdownParser.Parse(Markdown);
        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            var control = block switch
            {
                MarkdownHeading h => Text(h.Inlines, $"md-h{Math.Min(3, h.Level)}", topMargin: index == 0 ? 0 : 8),
                MarkdownParagraph p => Text(p.Inlines, "md-p"),
                MarkdownListItem li => ListItem(li),
                MarkdownQuote q => Quote(q),
                MarkdownCodeBlock code => CodeBlock(code),
                MarkdownRule => new Separator { Margin = new Thickness(0, 6) },
                _ => null,
            };
            if (control is not null)
            {
                panel.Children.Add(control);
            }
        }

        Document = panel;
        Child = panel;
    }

    private static SelectableTextBlock Text(IReadOnlyList<MarkdownInline> inlines, string styleClass, double topMargin = 0)
    {
        var block = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Classes = { styleClass },
            Margin = new Thickness(0, topMargin, 0, 0),
            Inlines = [],
        };
        foreach (var inline in inlines)
        {
            block.Inlines!.Add(ToRun(inline));
        }

        return block;
    }

    private static Run ToRun(MarkdownInline inline)
    {
        var run = new Run(inline.Text);
        if (inline.Bold)
        {
            run.FontWeight = FontWeight.SemiBold;
        }

        if (inline.Italic)
        {
            run.FontStyle = FontStyle.Italic;
        }

        if (inline.Code)
        {
            run.FontFamily = MonoFont;
            run.Background = Brush("FarmCodeBackgroundBrush", Color.FromRgb(0xEE, 0xE8, 0xDA));
        }

        if (inline.LinkUrl is not null)
        {
            run.Foreground = Brush("FarmPrimaryBrush", Color.FromRgb(0x09, 0x5C, 0x34));
            run.TextDecorations = TextDecorations.Underline;
        }

        return run;
    }

    private static Control ListItem(MarkdownListItem item)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(8 + (item.Depth * 18), 0, 0, 0),
        };
        var marker = new TextBlock
        {
            Text = item.Marker,
            Classes = { "md-marker" },
            MinWidth = item.Marker == "•" ? 14 : 22,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var text = Text(item.Inlines, "md-li");
        Grid.SetColumn(text, 1);
        grid.Children.Add(marker);
        grid.Children.Add(text);
        return grid;
    }

    private static Control Quote(MarkdownQuote quote) => new Border
    {
        Classes = { "md-quote" },
        Child = Text(quote.Inlines, "md-quote-text"),
    };

    private static Control CodeBlock(MarkdownCodeBlock code) => new Border
    {
        Classes = { "md-code" },
        Child = new SelectableTextBlock
        {
            Text = code.Code,
            FontFamily = MonoFont,
            TextWrapping = TextWrapping.Wrap,
            Classes = { "md-code-text" },
        },
    };

    private static IBrush Brush(string key, Color fallback) =>
        Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) == true && value is IBrush brush
            ? brush
            : new SolidColorBrush(fallback);
}
