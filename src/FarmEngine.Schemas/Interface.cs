namespace FarmEngine.Schemas;

// Port of packages/engine-schemas/src/interface.ts.

/// <summary>TS <c>GamePanelSchema.entries[].kind</c> enum.</summary>
public static class GamePanelEntryKinds
{
    public const string Text = "text";
    public const string Money = "money";
    public const string Energy = "energy";
    public const string Day = "day";
    public const string Item = "item";
    public const string Flag = "flag";
    public const string Action = "action";

    public static readonly IReadOnlyList<string> All = [Text, Money, Energy, Day, Item, Flag, Action];
}

/// <summary>TS <c>GamePanelSchema.entries</c> item (inline object).</summary>
public sealed record GamePanelEntry
{
    public string Label { get; init; } = "";
    /// <summary>One of <see cref="GamePanelEntryKinds"/>.</summary>
    public string Kind { get; init; } = "";
    public string Value { get; init; } = "";
}

public sealed record GamePanel
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string? VisibleFlag { get; init; }
    /// <summary>At most 40 entries.</summary>
    public List<GamePanelEntry> Entries { get; init; } = [];
}
