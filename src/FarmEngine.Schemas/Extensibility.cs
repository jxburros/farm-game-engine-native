using System.Text.Json;
using System.Text.Json.Serialization;

namespace FarmEngine.Schemas;

// Port of packages/engine-schemas/src/extensibility.ts.
//
// Creator-defined actions & minigames — the "customize anything" layer.
//
// An ACTION is a callable, condition-gated bundle of event outcomes plus a
// plugin hook (`onAction`). Actions can be triggered from item use, dialogue
// options, event outcomes (`performAction`), hotkeys, or plugin mutations —
// so creators add new verbs without engine changes, and plugins attach
// arbitrary sandboxed logic to them.
//
// A MINIGAME is a declared interactive challenge. The simulation opens it
// (`state.minigame` set via the `startMinigame` command/outcome), the host
// runs an implementation registered for its `kind` (timing-bar built in;
// game code can register more), and the result re-enters the deterministic
// command log as `resolveMinigame { score }`. Score-tiered outcomes and the
// `onMinigameResolve` hook turn the score into game consequences.

public sealed record ActionDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>All must hold for the action to run (same vocabulary as events).</summary>
    public List<EventCondition> Conditions { get; init; } = [];
    /// <summary>Shown when a condition fails; silent when empty.</summary>
    public string FailMessage { get; init; } = "";
    /// <summary>Applied in order when the action runs (same vocabulary as events).</summary>
    public List<EventOutcome> Outcomes { get; init; } = [];
    /// <summary>Energy spent on a successful run (respects the energy toggle). nonnegative.</summary>
    public double EnergyCost { get; init; } = 0;
    /// <summary>
    /// Optional single-character play-mode hotkey (max length 1). Reserved
    /// gameplay keys (movement/tools/panels) are ignored by hosts.
    /// </summary>
    public string? Hotkey { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed record MinigameResultTier
{
    /// <summary>Tier applies when score ≥ minScore; the highest matching tier wins. 0..1.</summary>
    public double MinScore { get; init; }
    public List<EventOutcome> Outcomes { get; init; } = [];
}

public sealed record MinigameDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>
    /// Implementation key looked up in the host's minigame registry
    /// ('timing-bar' ships with the engine; game code registers custom kinds).
    /// Unknown kinds fall back to a neutral confirm that scores 0.5.
    /// </summary>
    public string Kind { get; init; } = "timing-bar";
    /// <summary>Kind-specific tuning, passed verbatim to the implementation (<c>z.unknown()</c> values).</summary>
    public OrderedDictionary<string, JsonElement> Config { get; init; } = [];
    /// <summary>Declarative consequences by score tier (highest matching minScore).</summary>
    public List<MinigameResultTier> ResultTiers { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>
/// Live minigame session in GameState. <c>context</c> carries deterministic
/// resolution inputs (e.g. the rod tier for the built-in fishing binding).
/// </summary>
public sealed record MinigameSession
{
    public string MinigameId { get; init; } = "";
    /// <summary>Values are <c>string | number | boolean</c>.</summary>
    public OrderedDictionary<string, JsonElement> Context { get; init; } = [];
}

public static class ExtensibilitySchema
{
    /// <summary>Minigame id that, when defined in content, gates fishing on a cast.</summary>
    public const string FishingMinigameId = "fishing";
}
