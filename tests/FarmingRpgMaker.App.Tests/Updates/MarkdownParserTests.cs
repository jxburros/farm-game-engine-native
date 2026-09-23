using FarmingRpgMaker.App.Markdown;

namespace FarmingRpgMaker.App.Tests.Updates;

public sealed class MarkdownParserTests
{
    [Fact]
    public void ParsesReleaseNotesStructure()
    {
        var blocks = MarkdownParser.Parse("""
            ## What's new

            - **Fishing** mini-game
              continued line
            - Crops show a *growth* tooltip
              - nested item

            1. First fix
            2) Second fix

            Plain paragraph
            spanning lines.

            ```
            code line
            ```

            > quoted
            ---
            """);

        Assert.Collection(
            blocks,
            b =>
            {
                var heading = Assert.IsType<MarkdownHeading>(b);
                Assert.Equal(2, heading.Level);
                Assert.Equal([new MarkdownInline("What's new")], heading.Inlines);
            },
            b =>
            {
                var item = Assert.IsType<MarkdownListItem>(b);
                Assert.Equal("•", item.Marker);
                Assert.Equal(0, item.Depth);
                Assert.Equal(new MarkdownInline("Fishing", Bold: true), item.Inlines[0]);
                Assert.Equal("Fishing mini-game continued line", string.Concat(item.Inlines.Select(i => i.Text)));
            },
            b =>
            {
                var item = Assert.IsType<MarkdownListItem>(b);
                Assert.Contains(new MarkdownInline("growth", Italic: true), item.Inlines);
            },
            b => Assert.Equal(1, Assert.IsType<MarkdownListItem>(b).Depth),
            b => Assert.Equal("1.", Assert.IsType<MarkdownListItem>(b).Marker),
            b => Assert.Equal("2.", Assert.IsType<MarkdownListItem>(b).Marker),
            b => Assert.Equal("Plain paragraph spanning lines.", Assert.IsType<MarkdownParagraph>(b).Inlines.Single().Text),
            b => Assert.Equal("code line", Assert.IsType<MarkdownCodeBlock>(b).Code),
            b => Assert.Equal("quoted", Assert.IsType<MarkdownQuote>(b).Inlines.Single().Text),
            b => Assert.IsType<MarkdownRule>(b));
    }

    [Fact]
    public void ParsesInlineCodeLinksAndBareUrls()
    {
        var inlines = MarkdownParser.ParseInlines("Use `Export` — see [the docs](https://example.com/docs) or https://github.com/x/y.");

        Assert.Contains(new MarkdownInline("Export", Code: true), inlines);
        Assert.Contains(new MarkdownInline("the docs", LinkUrl: "https://example.com/docs"), inlines);
        Assert.Contains(new MarkdownInline("https://github.com/x/y", LinkUrl: "https://github.com/x/y"), inlines);
        Assert.Equal(".", inlines[^1].Text);
    }

    [Fact]
    public void KeepsSnakeCaseAndLoneAsterisks()
    {
        var inlines = MarkdownParser.ParseInlines("rename snake_case_name and 2 * 3 = 6");

        Assert.Equal("rename snake_case_name and 2 * 3 = 6", string.Concat(inlines.Select(i => i.Text)));
        Assert.All(inlines, i => Assert.False(i.Italic));
    }

    [Fact]
    public void StripsHtmlAndHandlesEmptyInput()
    {
        Assert.Empty(MarkdownParser.Parse(null));
        Assert.Empty(MarkdownParser.Parse("   \n"));
        var paragraph = Assert.IsType<MarkdownParagraph>(MarkdownParser.Parse("<!-- x -->Hello <b>there</b>").Single());
        Assert.Equal("Hello there", string.Concat(paragraph.Inlines.Select(i => i.Text)));
    }
}
