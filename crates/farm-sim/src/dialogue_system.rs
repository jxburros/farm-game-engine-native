//! Dialogue (port of `DialogueSystem.cs` / dialogue.ts).
//!
//! Dialogue state machine (port of dialogue.ts). The dialogue UI renders from `state.dialogue`;
//! choices flow back as commands.

use crate::effects::{message_levels, Effect};
use crate::engine_types::{Effects, EngineContext};
use crate::events;
use crate::inventory;
use crate::quests;
use crate::schema::{Dialogue, DialogueOption, DialogueState, GameState, ShopSession};
use crate::social;
use serde_json::Value;

/// `!string.IsNullOrEmpty(value)`: the string when it is present and non-empty.
fn non_empty(value: Option<&str>) -> Option<&str> {
    value.filter(|s| !s.is_empty())
}

pub fn find_dialogue<'a>(ctx: &'a EngineContext, npc_id: &str, dialogue_id: &str) -> Option<&'a Dialogue> {
    // NPC-owned dialogues first (interaction entry point), then the global list.
    let npc = ctx.content.npcs.iter().find(|n| n.id == npc_id);
    let owned = npc.and_then(|npc| npc.dialogue.iter().find(|d| d.id == dialogue_id));
    if owned.is_some() {
        return owned;
    }
    ctx.content.dialogues.iter().find(|d| d.id == dialogue_id)
}

pub fn handle_choose_dialogue_option(ctx: &EngineContext, state: &mut GameState, index: i32) -> Effects {
    let Some(dialogue_ref) = state.dialogue.clone() else {
        return Vec::new();
    };
    let dialogue = find_dialogue(ctx, &dialogue_ref.npc_id, &dialogue_ref.dialogue_id);
    // Index over the VISIBLE options (M4: friendship/item/flag gates) so the
    // UI and the engine always agree.
    let chosen: Option<usize> = dialogue.and_then(|dialogue| {
        let visible = social::visible_dialogue_option_indices(ctx, state, dialogue);
        // JS arr[index]: undefined for negative, fractional or out-of-range indices.
        // A fractional or negative index (read as −1) picks nothing.
        usize::try_from(index).ok().and_then(|index| visible.get(index).copied())
    });
    let (Some(dialogue), Some(option_index)) = (dialogue, chosen) else {
        state.dialogue = None;
        return Vec::new();
    };
    let option: DialogueOption = dialogue.options[option_index].clone();

    // Costs and room come first: an option the player can't pay for, or whose item doesn't
    // fit, does nothing (the conversation stays open).
    let take_money = option.take_money.filter(|money| *money > 0);
    if take_money.is_some_and(|money| state.player.money < money) {
        return vec![Effect::message(message_levels::ERROR, "Not enough money!")];
    }
    let mut item_grant = None;
    if let Some(give_item) = non_empty(option.give_item.as_deref()) {
        if let Some(item) = ctx.content.items.iter().find(|i| i.id == give_item) {
            // `option.giveItemQuantity || 1`: undefined, 0 and NaN all fall back to 1.
            let quantity = option.give_item_quantity.filter(|q| *q != 0).unwrap_or(1);
            let result =
                inventory::add_item(&state.player.inventory, item, quantity, state.player.max_inventory_size, None);
            if !result.added {
                return vec![Effect::message(message_levels::ERROR, "Inventory is full!")];
            }
            item_grant = Some((item, quantity, result.inventory));
        }
    }

    let mut effects = Vec::new();

    if let Some(take_money) = take_money {
        state.player.money -= take_money;
        effects.push(Effect::message(message_levels::INFO, format!("Paid ${take_money}")));
    }

    // Remember the choice: the option's flag, and the once-only marker (the same flag when the
    // option has one).
    if let Some(event_flag) = non_empty(option.event_flag.as_deref()) {
        state.flags.insert(event_flag.to_owned(), Value::Bool(true));
    }
    if option.once == Some(true) {
        state.flags.insert(social::once_flag(dialogue, option_index), Value::Bool(true));
    }

    if let Some(give_money) = option.give_money.filter(|money| *money != 0) {
        state.player.money = state.player.money.saturating_add(give_money);
        effects.push(Effect::message(message_levels::SUCCESS, format!("Received ${give_money}")));
    }

    if let Some((item, quantity, inventory)) = item_grant {
        state.player.inventory = inventory;
        let suffix = if quantity > 1 { format!(" x{quantity}") } else { String::new() };
        effects.push(Effect::message(message_levels::SUCCESS, format!("Received {}{}", item.name, suffix)));
    }

    // Quest-giver binding (M3): the option starts a quest if it's available.
    if let Some(offer_quest_id) = non_empty(option.offer_quest_id.as_deref()) {
        effects.extend(quests::start_quest_by_id(ctx, state, offer_quest_id));
    }
    // Creator-defined action bound to this option (extensibility layer) —
    // runs before the dialogue advances, so its outcomes can gate/warp/etc.
    if let Some(action_id) = non_empty(option.action_id.as_deref()) {
        let before_action = state.dialogue.clone();
        effects.extend(events::perform_action(ctx, state, action_id).effects);
        // An action that opened another conversation (or closed this one) decides where the
        // dialogue goes; the option's own next dialogue and shop don't apply.
        if state.dialogue != before_action {
            return effects;
        }
    }

    // `state = currentState with { Dialogue = dialogueRef }`
    state.dialogue = Some(dialogue_ref.clone());

    // Shop-opening options close the dialogue and start a shop session.
    if let Some(open_shop_id) = non_empty(option.open_shop_id.as_deref()) {
        let shop_exists = ctx.content.shops.iter().any(|shop| shop.id == open_shop_id);
        state.dialogue = None;
        if shop_exists {
            state.shop = Some(ShopSession { shop_id: open_shop_id.to_owned() });
        } else {
            effects.push(Effect::message(message_levels::ERROR, "That shop does not exist."));
        }
        return effects;
    }

    let mut next_dialogue: Option<DialogueState> = None;
    if let Some(next_dialogue_id) = non_empty(option.next_dialogue_id.as_deref()) {
        if let Some(next) = find_dialogue(ctx, &dialogue_ref.npc_id, next_dialogue_id) {
            next_dialogue = Some(DialogueState { npc_id: dialogue_ref.npc_id.clone(), dialogue_id: next.id.clone() });
        }
    }

    state.dialogue = next_dialogue;
    effects
}

pub fn handle_close_dialogue(state: &mut GameState) -> Effects {
    if state.dialogue.is_none() {
        return Vec::new();
    }
    state.dialogue = None;
    Vec::new()
}
