using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>
/// NPC relationships &amp; gifting (M4d) — port of social.ts: friendship points,
/// per-NPC taste tables, heart-gated dialogue options, birthdays.
/// </summary>
public static class Social
{
    public static double FriendshipWith(GameState state, string npcId) =>
        state.Social.TryGetValue(npcId, out var social) ? social.Friendship : 0;

    public static double Hearts(double friendship) => Math.Floor(friendship / SocialSchema.FriendshipPerHeart);

    /// <returns>One of <see cref="GiftReactions"/>.</returns>
    public static string GiftReaction(Npc npc, string itemId)
    {
        var tastes = npc.GiftTastes;
        if (tastes is null) return GiftReactions.Neutral;
        if (tastes.Loved.Contains(itemId)) return GiftReactions.Loved;
        if (tastes.Liked.Contains(itemId)) return GiftReactions.Liked;
        if (tastes.Disliked.Contains(itemId)) return GiftReactions.Disliked;
        if (tastes.Hated.Contains(itemId)) return GiftReactions.Hated;
        return GiftReactions.Neutral;
    }

    private static readonly IReadOnlyDictionary<string, string> ReactionLines = new Dictionary<string, string>
    {
        [GiftReactions.Loved] = "They love it!",
        [GiftReactions.Liked] = "They like it.",
        [GiftReactions.Neutral] = "They accept it politely.",
        [GiftReactions.Disliked] = "They don't seem thrilled…",
        [GiftReactions.Hated] = "They hate it!",
    };

    /// <summary>Give the first matching inventory item to the NPC the player faces.</summary>
    public static EngineStep HandleGiveGift(EngineContext ctx, GameState state, string itemId)
    {
        var facing = WorldMovement.FacingTarget(state);
        var targetX = facing.X;
        var targetY = facing.Y;
        string? npcEntryId = null;
        foreach (var (id, npc) in state.Npcs)
        {
            if (npc.SceneId == state.Player.SceneId && npc.X == targetX && npc.Y == targetY)
            {
                npcEntryId = id;
                break;
            }
        }
        if (npcEntryId is null) return EngineStep.Of(state, Effect.Message(MessageLevels.Info, "No one to give that to."));

        var npcDef = ctx.Content.Npcs.FirstOrDefault(npc => npc.Id == npcEntryId);
        if (npcDef is null) return EngineStep.Of(state);

        var slot = state.Player.Inventory.FirstOrDefault(s => s.Item.Id == itemId);
        if (slot is null) return EngineStep.Of(state, Effect.Message(MessageLevels.Error, "You don't have that item."));

        var social = state.Social.TryGetValue(npcDef.Id, out var existing) ? existing : new NpcSocialState { Friendship = 0, GiftsToday = 0 };
        var socialToday = social.LastGiftDay == state.Clock.Day ? social : social with { GiftsToday = 0 };
        if (socialToday.GiftsToday >= 1)
        {
            return EngineStep.Of(state, Effect.Message(MessageLevels.Info, $"{npcDef.Name} has already received a gift today."));
        }

        var reaction = GiftReaction(npcDef, itemId);
        var delta = SocialSchema.GiftFriendshipDeltas[reaction];
        var isBirthday = npcDef.Birthday is not null
            && npcDef.Birthday.Season == state.Clock.Season
            && npcDef.Birthday.Day == GameTime.DayOfSeason(ctx.Content.Settings.Calendar, state.Clock.Day);
        if (isBirthday) delta *= 2;

        var friendship = Math.Max(0, Math.Min(SocialSchema.MaxFriendship, socialToday.Friendship + delta));

        var nextState = state with
        {
            Player = state.Player with { Inventory = Inventory.RemoveItem(state.Player.Inventory, itemId, 1) },
            Social = new OrderedDictionary<string, NpcSocialState>(state.Social)
            {
                [npcDef.Id] = new NpcSocialState { Friendship = friendship, GiftsToday = socialToday.GiftsToday + 1, LastGiftDay = state.Clock.Day },
            },
        };

        var effects = new List<Effect>
        {
            Effect.Message(delta >= 0 ? MessageLevels.Success : MessageLevels.Info,
                $"{npcDef.Name}: {ReactionLines[reaction]}{(isBirthday ? " (Birthday!)" : "")} ({(delta >= 0 ? "+" : "")}{Js.Num(delta)})"),
        };
        ctx.Hooks?.Emit(HookNames.OnGiftGiven, new GiftGivenHookPayload(npcDef.Id, itemId, reaction));

        var xp = Skills.GrantXp(ctx, nextState, "social", 4);
        nextState = xp.State;
        effects.AddRange(xp.Effects);

        var quests = Quests.ProgressQuests(ctx, nextState, "gift", npcDef.Id, 1);
        return new EngineStep(quests.State, [.. effects, .. quests.Effects]);
    }

    /// <summary>
    /// Dialogue options visible in the current state (M4): friendship gates,
    /// required items and flags are finally honored. Used by BOTH the UI and
    /// chooseDialogueOption so indices always agree.
    /// </summary>
    public static List<DialogueOption> VisibleDialogueOptions(EngineContext ctx, GameState state, Dialogue dialogue) =>
        dialogue.Options.Where(option =>
        {
            if (option.RequiresFriendship is { } requiresFriendship && state.Dialogue is not null)
            {
                if (FriendshipWith(state, state.Dialogue.NpcId) < requiresFriendship) return false;
            }
            if (!string.IsNullOrEmpty(option.RequiresItem) && !state.Player.Inventory.Any(slot => slot.Item.Id == option.RequiresItem)) return false;
            if (!string.IsNullOrEmpty(option.RequiresFlag) && !Js.Truthy(GameEvents.FlagValue(state, option.RequiresFlag))) return false;
            return true;
        }).ToList();
}
