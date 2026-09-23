using System.Text.Json;
using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Runtime;

/// <summary>What creator panels read (TS <c>PanelState</c>).</summary>
/// <param name="Flags">Values are <c>boolean | string | number</c>.</param>
/// <param name="Blocked">A modal interaction (dialogue/shop/minigame/host menu) is open: actions are disabled.</param>
public sealed record PanelState(
    double Money,
    double Energy,
    double Day,
    IReadOnlyDictionary<string, JsonElement> Flags,
    IReadOnlyList<InventorySlot> Inventory,
    bool Blocked = false)
{
    /// <summary>The exported shell's view of the running game (game-shell main.ts).</summary>
    public static PanelState FromGameState(GameState state, bool hostModalOpen = false) => new(
        state.Player.Money,
        state.Player.Energy,
        state.Clock.Day,
        state.Flags,
        state.Player.Inventory,
        state.Dialogue is not null || state.Minigame is not null || state.Shop is not null || hostModalOpen);

    /// <summary>The editor play view's projection (src/components/GamePanels.tsx): the synced project.</summary>
    public static PanelState FromProject(GameProject project, bool blocked) => new(
        project.Player.Money,
        project.Player.Energy ?? 0,
        project.CurrentDay,
        project.EventFlags.ToDictionary(kv => kv.Key, kv => Js.Value(kv.Value)),
        project.Player.Inventory,
        blocked);
}

/// <summary>One rendered panel entry. Text is always plain text; game content is never markup.</summary>
/// <param name="Kind">One of <see cref="GamePanelEntryKinds"/>.</param>
/// <param name="Text">Button label for actions; <c>"{label}: {value}"</c> (or just the value) otherwise.</param>
/// <param name="ActionId">The action to perform when an action entry is clicked (null otherwise).</param>
/// <param name="Enabled">Action buttons are disabled while <see cref="PanelState.Blocked"/>.</param>
public sealed record PanelEntryView(string Kind, string Text, string? ActionId, bool Enabled);

/// <param name="Hidden">The panel's <c>visibleFlag</c> is set and currently falsy.</param>
public sealed record PanelView(string Id, string Title, bool Hidden, List<PanelEntryView> Entries);

/// <summary>
/// Creator-defined game panels (port of game-panels.ts <c>mountGamePanels</c>),
/// host-agnostic: <see cref="Render"/> turns panel definitions + live state
/// into plain view models the host draws; <see cref="Mount"/> keeps the TS
/// update/dispose lifecycle and routes action clicks.
/// </summary>
public static class GamePanels
{
    /// <summary>The display value of a non-action entry.</summary>
    public static string EntryValue(GamePanelEntry entry, PanelState state) => entry.Kind switch
    {
        GamePanelEntryKinds.Text => entry.Value,
        GamePanelEntryKinds.Flag => Js.Truthy(state.Flags.TryGetValue(entry.Value, out var flag) ? flag : null) ? "Yes" : "No",
        GamePanelEntryKinds.Item => Js.Num(state.Inventory.Where(s => s.Item.Id == entry.Value).Aggregate(0.0, (n, s) => n + s.Quantity)),
        GamePanelEntryKinds.Money => Js.Num(state.Money),
        GamePanelEntryKinds.Energy => Js.Num(state.Energy),
        GamePanelEntryKinds.Day => Js.Num(state.Day),
        // TS `state[entry.kind]` for an unknown kind → undefined.
        _ => "undefined",
    };

    /// <summary>Build the panel views for the current state.</summary>
    public static List<PanelView> Render(IEnumerable<GamePanel> panels, PanelState state) =>
        panels.Select(panel => new PanelView(
            panel.Id,
            panel.Title,
            !string.IsNullOrEmpty(panel.VisibleFlag) && !Js.Truthy(state.Flags.TryGetValue(panel.VisibleFlag, out var flag) ? flag : null),
            panel.Entries.Select(entry => entry.Kind == GamePanelEntryKinds.Action
                ? new PanelEntryView(entry.Kind, entry.Label, entry.Value, !state.Blocked)
                : new PanelEntryView(entry.Kind, $"{entry.Label}{(entry.Label.Length > 0 ? ": " : "")}{EntryValue(entry, state)}", null, false))
                .ToList()))
            .ToList();

    /// <summary>TS <c>mountGamePanels(root, panels, getState, run)</c> without the DOM.</summary>
    public static MountedGamePanels Mount(IReadOnlyList<GamePanel> panels, Func<PanelState> getState, Action<string> run) =>
        new(panels, getState, run);
}

/// <summary>A live set of panels: call <see cref="Update"/> each frame (or on change) and draw <see cref="Views"/>.</summary>
public sealed class MountedGamePanels
{
    private IReadOnlyList<GamePanel> _panels;
    private readonly Func<PanelState> _getState;
    private readonly Action<string> _run;

    internal MountedGamePanels(IReadOnlyList<GamePanel> panels, Func<PanelState> getState, Action<string> run)
    {
        _panels = panels;
        _getState = getState;
        _run = run;
    }

    /// <summary>The latest rendered views (empty until the first <see cref="Update"/>, and after <see cref="Dispose"/>).</summary>
    public IReadOnlyList<PanelView> Views { get; private set; } = [];

    public void Update() => Views = GamePanels.Render(_panels, _getState());

    /// <summary>
    /// A click on an action entry: runs it unless a modal interaction blocks
    /// actions right now (checked live, like the TS button handler). Returns
    /// whether the action ran.
    /// </summary>
    public bool Click(string actionId)
    {
        if (_getState().Blocked) return false;
        if (!_panels.Any(panel => panel.Entries.Any(entry => entry.Kind == GamePanelEntryKinds.Action && entry.Value == actionId))) return false;
        _run(actionId);
        return true;
    }

    public void Dispose()
    {
        _panels = [];
        Views = [];
    }
}
