namespace FarmingRpgMaker.App.Markdown;

/// <summary>Inline span of Markdown text.</summary>
public sealed record MarkdownInline(string Text, bool Bold = false, bool Italic = false, bool Code = false, string? LinkUrl = null);

/// <summary>Block-level Markdown element (the subset GitHub release notes use).</summary>
public abstract record MarkdownBlock;

public sealed record MarkdownHeading(int Level, IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock;

public sealed record MarkdownParagraph(IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock;

/// <summary>A list item; <paramref name="Marker"/> is "•" or "1." etc.</summary>
public sealed record MarkdownListItem(string Marker, int Depth, IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock;

public sealed record MarkdownCodeBlock(string Code) : MarkdownBlock;

public sealed record MarkdownQuote(IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock;

public sealed record MarkdownRule : MarkdownBlock;
