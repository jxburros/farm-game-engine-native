//! Friendship and gifts (port of `Social.cs` / social.ts).
//!
//! NPC relationships & gifting (M4d) — port of social.ts: friendship points, per-NPC taste
//! tables, heart-gated dialogue options, birthdays.

use crate::effects::{message_levels, Effect};
use crate::engine_types::{Effects, EngineContext};
use crate::events;
use crate::game_time;
use crate::hooks::{GiftGivenHookPayload, HookEvent};
use crate::inventory;
use crate::messages;
use crate::quests;
use crate::schema::{
    gift_friendship_delta, gift_reactions, Dialogue, DialogueOption, GameState, Npc, NpcSocialState, NpcState,
    FRIENDSHIP_PER_HEART, MAX_FRIENDSHIP,
};
use crate::skills;
use crate::text;
use crate::units;
use crate::world::world_movement;

/// The NPC standing on tile (`x`, `y`) of the player's scene. When several share the tile (a
/// `spawnNPC` outcome or a schedule teleport skips occupancy checks), the first in content order
/// wins, then the smallest id among NPCs the content lacks: the pick never depends on the order
/// of `state.npcs`, which a save does not keep (#142).
pub fn npc_on_tile(ctx: &EngineContext, state: &GameState, x: i32, y: i32) -> Option<String> {
    let here =
        |npc: &NpcState| npc.scene_id == state.player.scene_id && npc.x == units::tiles(x) && npc.y == units::tiles(y);
    ctx.content
        .npcs
        .iter()
        .find(|def| state.npcs.get(&def.id).is_some_and(here))
        .map(|def| def.id.clone())
        .or_else(|| state.npcs.iter().filter(|(_, npc)| here(npc)).map(|(id, _)| id).min().cloned())
}

/// The player's friendship points with `npc_id` (0 when never met).
pub fn friendship_with(state: &GameState, npc_id: &str) -> i32 {
    state.social.get(npc_id).map(|social| social.friendship).unwrap_or(0)
}

/// Whole hearts of a friendship score.
pub fn hearts(friendship: i32) -> i32 {
    friendship.div_euclid(FRIENDSHIP_PER_HEART)
}

/// Returns one of [`gift_reactions`].
pub fn gift_reaction(npc: &Npc, item_id: &str) -> String {
    let Some(tastes) = &npc.gift_tastes else {
        return gift_reactions::NEUTRAL.to_owned();
    };
    let has = |list: &[String]| list.iter().any(|id| id == item_id);
    if has(&tastes.loved) {
        return gift_reactions::LOVED.to_owned();
    }
    if has(&tastes.liked) {
        return gift_reactions::LIKED.to_owned();
    }
    if has(&tastes.disliked) {
        return gift_reactions::DISLIKED.to_owned();
    }
    if has(&tastes.hated) {
        return gift_reactions::HATED.to_owned();
    }
    gift_reactions::NEUTRAL.to_owned()
}

/// TS `REACTION_LINES[reaction]`.
fn reaction_line(reaction: &str) -> messages::Arg {
    let template = match reaction {
        gift_reactions::LOVED => &messages::GIFT_LOVED,
        gift_reactions::LIKED => &messages::GIFT_LIKED,
        gift_reactions::NEUTRAL => &messages::GIFT_NEUTRAL,
        gift_reactions::DISLIKED => &messages::GIFT_DISLIKED,
        gift_reactions::HATED => &messages::GIFT_HATED,
        _ => return messages::Arg::text(""),
    };
    messages::Arg::Message(template.into())
}

/// `!string.IsNullOrEmpty(value)`: the string when it is present and non-empty.
fn non_empty(value: Option<&str>) -> Option<&str> {
    value.filter(|s| !s.is_empty())
}

/// Give the first matching inventory item to the NPC the player faces.
pub fn handle_give_gift(ctx: &EngineContext, state: &mut GameState, item_id: &str) -> Effects {
    let facing = world_movement::facing_target(state);
    let Some(npc_entry_id) = npc_on_tile(ctx, state, facing.x, facing.y) else {
        return vec![Effect::say(message_levels::INFO, &messages::NO_ONE_TO_GIVE)];
    };

    let Some(npc_def) = ctx.npc(&npc_entry_id) else {
        return Vec::new();
    };

    if !state.player.inventory.iter().any(|s| s.item.id == item_id) {
        return vec![Effect::say(message_levels::ERROR, &messages::DONT_HAVE_ITEM)];
    }

    let social = state.social.get(&npc_def.id).cloned().unwrap_or(NpcSocialState {
        friendship: 0,
        gifts_today: 0,
        last_gift_day: None,
    });
    let social_today = if social.last_gift_day == Some(state.clock.day) {
        social
    } else {
        NpcSocialState { gifts_today: 0, ..social }
    };
    if social_today.gifts_today >= 1 {
        return vec![Effect::say(message_levels::INFO, messages::ALREADY_GIFTED.with(&[&npc_def.name]))];
    }

    let reaction = gift_reaction(npc_def, item_id);
    let mut delta = gift_friendship_delta(&reaction).unwrap_or(0);
    let is_birthday = npc_def.birthday.as_ref().is_some_and(|birthday| {
        birthday.season == state.clock.season
            && birthday.day == game_time::clock_date(&ctx.content.settings.calendar, &state.clock).day_of_season
    });
    if is_birthday {
        delta *= 2;
    }

    let friendship = social_today.friendship.saturating_add(delta).clamp(0, MAX_FRIENDSHIP);

    state.player.inventory = inventory::remove_item(&state.player.inventory, item_id, 1);
    state.social.insert(
        npc_def.id.clone(),
        NpcSocialState {
            friendship,
            gifts_today: social_today.gifts_today.saturating_add(1),
            last_gift_day: Some(state.clock.day),
        },
    );

    let birthday =
        if is_birthday { messages::Arg::Message((&messages::GIFT_BIRTHDAY).into()) } else { messages::Arg::text("") };
    let mut effects = vec![Effect::say(
        if delta >= 0 { message_levels::SUCCESS } else { message_levels::INFO },
        messages::GIFT_REACTION.with_args(vec![
            messages::Arg::text(&npc_def.name),
            reaction_line(&reaction),
            birthday,
            messages::Arg::text(format!("{}{delta}", if delta >= 0 { "+" } else { "" })),
        ]),
    )];
    ctx.emit(HookEvent::GiftGiven(GiftGivenHookPayload {
        npc_id: npc_def.id.clone(),
        item_id: item_id.to_owned(),
        reaction: reaction.clone(),
    }));

    effects.extend(skills::grant_xp(ctx, state, "social", 4));

    effects.extend(quests::progress_quests(ctx, state, "gift", &npc_def.id, 1));
    effects
}

/// Dialogue options visible in the current state (M4): friendship gates,
/// required items and flags are finally honored. Used by BOTH the UI and
/// chooseDialogueOption so indices always agree.
pub fn visible_dialogue_options(ctx: &EngineContext, state: &GameState, dialogue: &Dialogue) -> Vec<DialogueOption> {
    visible_dialogue_option_indices(ctx, state, dialogue)
        .into_iter()
        .map(|index| dialogue.options[index].clone())
        .collect()
}

/// The flag a once-only option remembers being chosen by: its `eventFlag`, else one of its own
/// (`dialogue:{dialogueId}:option{index}`).
pub fn once_flag(dialogue: &Dialogue, index: usize) -> String {
    match dialogue.options.get(index).and_then(|option| non_empty(option.event_flag.as_deref())) {
        Some(flag) => flag.to_owned(),
        None => format!("dialogue:{}:option{index}", dialogue.id),
    }
}

/// The indices (into `dialogue.options`) of [`visible_dialogue_options`].
pub fn visible_dialogue_option_indices(ctx: &EngineContext, state: &GameState, dialogue: &Dialogue) -> Vec<usize> {
    let _ = ctx;
    dialogue
        .options
        .iter()
        .enumerate()
        .filter(|(index, option)| {
            if option.once == Some(true) && text::truthy(events::flag_value(state, &once_flag(dialogue, *index))) {
                return false;
            }
            if let Some(hidden_if_flag) = non_empty(option.hidden_if_flag.as_deref()) {
                if text::truthy(events::flag_value(state, hidden_if_flag)) {
                    return false;
                }
            }
            if let (Some(requires_friendship), Some(open)) = (option.requires_friendship, &state.dialogue) {
                if friendship_with(state, &open.npc_id) < requires_friendship {
                    return false;
                }
            }
            if let Some(requires_item) = non_empty(option.requires_item.as_deref()) {
                if !state.player.inventory.iter().any(|slot| slot.item.id == requires_item) {
                    return false;
                }
            }
            if let Some(requires_flag) = non_empty(option.requires_flag.as_deref()) {
                if !text::truthy(events::flag_value(state, requires_flag)) {
                    return false;
                }
            }
            true
        })
        .map(|(index, _)| index)
        .collect()
}
