using System.Diagnostics;

namespace FarmEngine.Core;

/// <summary>Hook names (port of the <c>HookPayloads</c> keys in hooks.ts).</summary>
public static class HookNames
{
    public const string OnDayStart = "onDayStart";
    public const string OnDayEnd = "onDayEnd";
    public const string OnSeasonChange = "onSeasonChange";
    public const string OnYearStart = "onYearStart";
    public const string OnCropHarvest = "onCropHarvest";
    public const string OnResourceGather = "onResourceGather";
    public const string OnNPCInteract = "onNPCInteract";
    public const string OnRecipeCraft = "onRecipeCraft";
    public const string OnGiftGiven = "onGiftGiven";
    public const string OnRelationshipChange = "onRelationshipChange";
    public const string OnWeatherRoll = "onWeatherRoll";
    public const string OnCommand = "onCommand";
    public const string OnEffect = "onEffect";
    /// <summary>A creator-defined action ran (extensibility layer).</summary>
    public const string OnAction = "onAction";
    /// <summary>A minigame resolved with a score in [0, 1].</summary>
    public const string OnMinigameResolve = "onMinigameResolve";

    public static readonly IReadOnlyList<string> All =
    [
        OnDayStart, OnDayEnd, OnSeasonChange, OnYearStart, OnCropHarvest, OnResourceGather,
        OnNPCInteract, OnRecipeCraft, OnGiftGiven, OnRelationshipChange, OnWeatherRoll,
        OnCommand, OnEffect, OnAction, OnMinigameResolve,
    ];
}

// Typed payloads — plain records so plugins receive the same JSON the TS engine sends.
public sealed record DayHookPayload(double Day, string Season, double Year);
public sealed record SeasonChangeHookPayload(string Season, string PreviousSeason, double Year);
public sealed record YearStartHookPayload(double Year);
public sealed record CropHarvestHookPayload(string CropType, double Quantity, string Quality);
public sealed record GatherDrop(string ItemId, double Quantity);
public sealed record ResourceGatherHookPayload(string NodeTypeId, List<GatherDrop> Drops);
public sealed record NpcInteractHookPayload(string NpcId);
public sealed record RecipeCraftHookPayload(string RecipeId);
public sealed record GiftGivenHookPayload(string NpcId, string ItemId, string Reaction);
public sealed record RelationshipChangeHookPayload(string NpcId, double Friendship);
public sealed record WeatherRollHookPayload(string WeatherId, double Day);
public sealed record CommandHookPayload(string CommandType);
public sealed record EffectHookPayload(string EffectType);
public sealed record ActionHookPayload(string ActionId);
public sealed record MinigameResolveHookPayload(string MinigameId, double Score);

/// <summary>
/// Typed hook/event bus (port of hooks.ts). Internal systems are its first
/// consumers; it is also the public mod extension surface — hooks receive
/// structured, JSON-serializable payloads. Listeners may return a value;
/// <see cref="Emit"/> ignores it, <see cref="Collect"/> gathers them.
/// </summary>
public sealed class HookBus
{
    private readonly Dictionary<string, List<Func<object, object?>>> _listeners = new();

    public Action On(string hook, Func<object, object?> listener)
    {
        if (!_listeners.TryGetValue(hook, out var list))
        {
            list = [];
            _listeners[hook] = list;
        }
        if (!list.Contains(listener)) list.Add(listener);
        return () => Off(hook, listener);
    }

    /// <summary>Typed convenience overload.</summary>
    public Action On<TPayload>(string hook, Action<TPayload> listener) =>
        On(hook, payload =>
        {
            if (payload is TPayload typed) listener(typed);
            return null;
        });

    public void Off(string hook, Func<object, object?> listener)
    {
        if (_listeners.TryGetValue(hook, out var list)) list.Remove(listener);
    }

    public void Emit(string hook, object payload)
    {
        if (!_listeners.TryGetValue(hook, out var list)) return;
        foreach (var listener in list.ToArray())
        {
            try
            {
                listener(payload);
            }
            catch (Exception error)
            {
                // Listener failures must never break the simulation step.
                Debug.WriteLine($"hook listener for {hook} threw: {error}");
            }
        }
    }

    /// <summary>Emit and gather listener return values in subscription order.</summary>
    public List<object?> Collect(string hook, object payload)
    {
        var results = new List<object?>();
        if (!_listeners.TryGetValue(hook, out var list)) return results;
        foreach (var listener in list.ToArray())
        {
            try
            {
                results.Add(listener(payload));
            }
            catch (Exception error)
            {
                Debug.WriteLine($"hook listener for {hook} threw: {error}");
            }
        }
        return results;
    }
}
