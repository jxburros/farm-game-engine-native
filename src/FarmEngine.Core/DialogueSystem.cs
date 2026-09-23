using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>
/// Dialogue state machine (port of dialogue.ts). The dialogue UI renders from
/// <c>state.dialogue</c>; choices flow back as commands.
/// </summary>
public static class DialogueSystem
{
    public static Dialogue? FindDialogue(EngineContext ctx, string npcId, string dialogueId)
    {
        // NPC-owned dialogues first (interaction entry point), then the global list.
        var npc = ctx.Content.Npcs.FirstOrDefault(n => n.Id == npcId);
        var owned = npc?.Dialogue?.FirstOrDefault(d => d.Id == dialogueId);
        if (owned is not null) return owned;
        return ctx.Content.Dialogues.FirstOrDefault(d => d.Id == dialogueId);
    }

    public static EngineStep HandleChooseDialogueOption(EngineContext ctx, GameState state, double index)
    {
        if (state.Dialogue is null) return EngineStep.Of(state);
        var dialogue = FindDialogue(ctx, state.Dialogue.NpcId, state.Dialogue.DialogueId);
        // Index over the VISIBLE options (M4: friendship/item/flag gates) so the
        // UI and the engine always agree.
        DialogueOption? option = null;
        if (dialogue is not null)
        {
            var visible = Social.VisibleDialogueOptions(ctx, state, dialogue);
            // JS arr[index]: undefined for negative, fractional or out-of-range indices.
            if (Js.IsInteger(index) && index >= 0 && index < visible.Count) option = visible[(int)index];
        }
        if (dialogue is null || option is null)
        {
            return EngineStep.Of(state with { Dialogue = null });
        }

        var effects = new List<Effect>();
        var player = state.Player;

        if (option.GiveMoney is { } giveMoney && giveMoney != 0 && !double.IsNaN(giveMoney))
        {
            player = player with { Money = player.Money + giveMoney };
            effects.Add(Effect.Message(MessageLevels.Success, $"Received ${Js.Num(giveMoney)}"));
        }

        if (!string.IsNullOrEmpty(option.GiveItem))
        {
            var item = ctx.Content.Items.FirstOrDefault(i => i.Id == option.GiveItem);
            if (item is not null)
            {
                // `option.giveItemQuantity || 1`: undefined, 0 and NaN all fall back to 1.
                var quantity = option.GiveItemQuantity is { } q && q != 0 && !double.IsNaN(q) ? q : 1;
                var result = Inventory.AddItem(player.Inventory, item, quantity, player.MaxInventorySize);
                if (result.Added)
                {
                    player = player with { Inventory = result.Inventory };
                    effects.Add(Effect.Message(MessageLevels.Success, $"Received {item.Name}{(quantity > 1 ? $" x{Js.Num(quantity)}" : "")}"));
                }
                else
                {
                    effects.Add(Effect.Message(MessageLevels.Error, "Inventory is full!"));
                }
            }
        }

        // Quest-giver binding (M3): the option starts a quest if it's available.
        var dialogueRef = state.Dialogue;
        var currentState = state with { Player = player };
        if (!string.IsNullOrEmpty(option.OfferQuestId))
        {
            var offered = Quests.StartQuestById(ctx, currentState, option.OfferQuestId);
            currentState = offered.State;
            effects.AddRange(offered.Effects);
        }
        // Creator-defined action bound to this option (extensibility layer) —
        // runs before the dialogue advances, so its outcomes can gate/warp/etc.
        if (!string.IsNullOrEmpty(option.ActionId))
        {
            var acted = GameEvents.PerformAction(ctx, currentState, option.ActionId);
            currentState = acted.State;
            effects.AddRange(acted.Effects);
        }

        state = currentState with { Dialogue = dialogueRef };
        player = currentState.Player;

        // Shop-opening options close the dialogue and start a shop session.
        if (!string.IsNullOrEmpty(option.OpenShopId))
        {
            var shopExists = ctx.Content.Shops.Any(shop => shop.Id == option.OpenShopId);
            return new EngineStep(
                state with
                {
                    Player = player,
                    Dialogue = null,
                    Shop = shopExists ? new ShopSession { ShopId = option.OpenShopId } : state.Shop,
                },
                shopExists ? effects : [.. effects, Effect.Message(MessageLevels.Error, "That shop does not exist.")]);
        }

        DialogueState? nextDialogue = null;
        if (!string.IsNullOrEmpty(option.NextDialogueId))
        {
            var next = FindDialogue(ctx, dialogueRef.NpcId, option.NextDialogueId);
            if (next is not null)
            {
                nextDialogue = new DialogueState { NpcId = dialogueRef.NpcId, DialogueId = next.Id };
            }
        }

        return new EngineStep(state with { Player = player, Dialogue = nextDialogue }, effects);
    }

    public static EngineStep HandleCloseDialogue(GameState state)
    {
        if (state.Dialogue is null) return EngineStep.Of(state);
        return EngineStep.Of(state with { Dialogue = null });
    }
}
