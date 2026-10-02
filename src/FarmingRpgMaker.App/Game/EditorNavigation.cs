namespace FarmingRpgMaker.App.Game;

/// <summary>
/// The Edit Mode tabs, in the order they appear (the value is the tab's index in the
/// <c>EditorTabs</c> control).
/// </summary>
public enum EditorTab
{
    Map = 0,
    Content = 1,
    Problems = 2,
    Settings = 3,
    Mods = 4,
    Art = 5,
    Workshop = 6,
    Interface = 7,
}

/// <summary>
/// Where the editor's links lead: the Workshop's "Build your game" shortcuts (web editor tab
/// keys) and the Problems panel's targets (F# problem target kinds). Pure lookups, so the
/// routing is testable without building the editor.
/// </summary>
public static class EditorNavigation
{
    /// <summary>
    /// The tab a Workshop shortcut opens and, for the Content tab, the category it shows; null
    /// for a key the editor has no page for.
    /// </summary>
    public static (EditorTab Tab, string? Category)? WorkshopTarget(string tabKey) => tabKey switch
    {
        // Art, the map and the Problems panel have their own tabs.
        "scenes" => (EditorTab.Map, null),
        "problems" => (EditorTab.Problems, null),
        "assets" => (EditorTab.Art, null),
        _ => WorkshopCategory(tabKey) is { } category ? (EditorTab.Content, category) : null,
    };

    private static string? WorkshopCategory(string tabKey) => tabKey switch
    {
        "npcs" => "NPCs",
        "actions" => "Actions",
        "events" => "Events",
        "nodes" => "Node types",
        "craft" => "Recipes",
        "crops" => "Crops",
        "wildlife" => "Animal species",
        "items" => "Items",
        "quests" => "Quests",
        _ => null,
    };

    /// <summary>
    /// The Content category that lists entries of a problem's target kind, or null for kinds
    /// that live elsewhere (scenes, settings, the interface, packs, art).
    /// </summary>
    public static string? ContentCategory(string targetKind) => targetKind switch
    {
        "npc" => "NPCs",
        "item" => "Items",
        "crop" => "Crops",
        "quest" => "Quests",
        "event" => "Events",
        "shop" => "Shops",
        "recipe" => "Recipes",
        "nodeType" => "Node types",
        "machineType" => "Machine types",
        "animalSpecies" => "Animal species",
        "fishTable" => "Fish tables",
        "action" => "Actions",
        "minigame" => "Minigames",
        _ => null,
    };
}
