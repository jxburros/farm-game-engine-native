//! Quests (port of `Quests/Quests.cs` / quests/quests.ts).
//!
//! Quest progression over GameState: progress clamped to target, auto-completion when all
//! objectives finish; rewards that don't fit the inventory raise an error message rather than
//! vanishing silently.

use crate::effects::{message_levels, Effect};
use crate::engine_types::{Effects, EngineContext};
use crate::inventory;
use crate::schema::{GameState, Quest, QuestObjective, QuestObjectiveProgress, QuestProgress};
use crate::skills;
use indexmap::IndexMap;

fn get_quest_definition<'a>(ctx: &'a EngineContext, quest_id: &str) -> Option<&'a Quest> {
    ctx.content.quests.iter().find(|quest| quest.id == quest_id)
}

fn objective_target(objective: &QuestObjective) -> u32 {
    // ?? not ||: an authored target of 0 means "already satisfied", not 1.
    objective.target_item_quantity.or(objective.target_crop_quantity).unwrap_or(1)
}

/// Complete a quest: move it to completed, grant rewards.
fn complete_quest(ctx: &EngineContext, state: &mut GameState, quest_id: &str) -> Effects {
    let Some(quest) = get_quest_definition(ctx, quest_id) else { return vec![] };

    state.player.active_quests.retain(|id| id != quest_id);
    // A repeatable quest completed again is listed once.
    if !state.player.completed_quests.iter().any(|id| id == quest_id) {
        state.player.completed_quests.push(quest_id.to_owned());
    }

    if let Some(money) = quest.rewards.money.filter(|money| *money != 0) {
        state.player.money = state.player.money.saturating_add(money);
    }

    let mut effects = Vec::new();
    if let Some(rewards) = &quest.rewards.items {
        for reward in rewards {
            let Some(item) = ctx.content.items.iter().find(|i| i.id == reward.item_id) else { continue };
            let result = inventory::add_item(
                &state.player.inventory,
                item,
                reward.quantity,
                state.player.max_inventory_size,
                None,
            );
            let lost = result.rejected(reward.quantity);
            state.player.inventory = result.inventory;
            if !result.added {
                // A dropped reward is a player-visible loss — say so (with what was actually
                // lost: the part that fit is kept) instead of silently discarding it.
                effects.push(Effect::message(
                    message_levels::ERROR,
                    format!("Inventory full — quest reward lost: {lost}× {}", item.name),
                ));
            }
        }
    }
    if let Some(experience) = quest.rewards.experience.filter(|xp| *xp > 0) {
        let skill = quest.rewards.skill.as_deref().filter(|skill| !skill.is_empty()).unwrap_or(DEFAULT_REWARD_SKILL);
        effects.extend(skills::grant_xp(ctx, state, skill, experience));
    }

    // TS: { ...state.quests[questId], status: 'completed' }. A missing entry spreads to nothing,
    // so the completed entry has no `objectives` key (unlike the C#, which wrote an empty map).
    match state.quests.get_mut(quest_id) {
        Some(existing) => existing.status = "completed".to_owned(),
        None => {
            state
                .quests
                .insert(quest_id.to_owned(), QuestProgress { status: "completed".to_owned(), objectives: None });
        }
    }

    let mut all = vec![Effect::QuestCompleted { quest_id: quest_id.to_owned() }];
    all.extend(effects);
    all
}

/// Progress matching objectives of active quests.
/// kind: 'harvest' matches targetCropType, 'collect' targetItemId,
/// 'talk' targetNPCId, 'visit' targetSceneId, 'craft' targetItemId, 'gift' targetNPCId.
/// Crafting (by hand or collected from a machine) reports each output item with its quantity,
/// so a craft objective names the item made (the editor's item picker).
///
/// `kind` is 'collect' | 'harvest' | 'talk' | 'visit' | 'craft' | 'gift'.
pub fn progress_quests(
    ctx: &EngineContext,
    state: &mut GameState,
    kind: &str,
    target_id: &str,
    amount: u32,
) -> Effects {
    let mut effects = Vec::new();

    let active_quests = state.player.active_quests.clone();
    for quest_id in &active_quests {
        let Some(quest) = get_quest_definition(ctx, quest_id) else { continue };
        let Some(progress) = state.quests.get(quest_id) else { continue };
        if progress.status != "active" {
            continue;
        }

        let mut changed = false;
        let mut objectives: IndexMap<String, QuestObjectiveProgress> = progress.objectives.clone().unwrap_or_default();

        for objective in &quest.objectives {
            let objective_state = objectives
                .get(&objective.id)
                .cloned()
                .unwrap_or(QuestObjectiveProgress { progress: 0, completed: false });
            if objective.r#type != kind || objective_state.completed {
                continue;
            }

            let matches = (kind == "harvest" && objective.target_crop_type.as_deref() == Some(target_id))
                || (kind == "collect" && objective.target_item_id.as_deref() == Some(target_id))
                || (kind == "talk" && objective.target_npc_id.as_deref() == Some(target_id))
                || (kind == "visit" && objective.target_scene_id.as_deref() == Some(target_id))
                || (kind == "craft" && objective.target_item_id.as_deref() == Some(target_id))
                || (kind == "gift" && objective.target_npc_id.as_deref() == Some(target_id));
            if !matches {
                continue;
            }

            let target = objective_target(objective);
            let new_progress = objective_state.progress.saturating_add(amount).min(target);
            objectives.insert(
                objective.id.clone(),
                QuestObjectiveProgress { progress: new_progress, completed: new_progress >= target },
            );
            changed = true;
        }

        if !changed {
            continue;
        }

        let all_complete =
            quest.objectives.iter().all(|objective| objectives.get(&objective.id).is_some_and(|o| o.completed));
        if let Some(entry) = state.quests.get_mut(quest_id) {
            entry.objectives = Some(objectives);
        }

        if all_complete {
            effects.extend(complete_quest(ctx, state, quest_id));
        }
    }

    effects
}

/// Availability window check (M3): seasons + absolute-day range.
pub fn is_quest_available(quest: &Quest, state: &GameState) -> bool {
    if let Some(seasons) = quest.available_seasons.as_ref().filter(|seasons| !seasons.is_empty()) {
        if !seasons.contains(&state.clock.season) {
            return false;
        }
    }
    if quest.available_from_day.is_some_and(|from| state.clock.day < from) {
        return false;
    }
    if quest.available_to_day.is_some_and(|to| state.clock.day > to) {
        return false;
    }
    true
}

/// The skill quest `experience` goes to when the quest names none.
pub const DEFAULT_REWARD_SKILL: &str = "farming";

/// Whether `quest` can start now: not active, not completed (unless repeatable), its
/// prerequisites completed and inside its availability window.
fn can_start(quest: &Quest, state: &GameState) -> bool {
    if state.player.active_quests.contains(&quest.id) {
        return false;
    }
    if quest.repeatable != Some(true) && state.player.completed_quests.contains(&quest.id) {
        return false;
    }
    let prerequisites_met = quest
        .prerequisites
        .as_deref()
        .unwrap_or(&[])
        .iter()
        .all(|id| state.player.completed_quests.iter().any(|completed| completed == id));
    prerequisites_met && is_quest_available(quest, state)
}

/// Mark `quest` active. A repeatable quest starting again begins with its objectives reset.
fn activate(state: &mut GameState, quest: &Quest) {
    state.player.active_quests.push(quest.id.clone());
    let restarting = state.player.completed_quests.contains(&quest.id);
    let progress = if restarting {
        QuestProgress { status: "active".to_owned(), objectives: Some(IndexMap::new()) }
    } else {
        activated(&state.quests, &quest.id)
    };
    state.quests.insert(quest.id.clone(), progress);
}

/// Start a quest by id (dialogue offers, event outcomes).
pub fn start_quest_by_id(ctx: &EngineContext, state: &mut GameState, quest_id: &str) -> Effects {
    let Some(quest) = get_quest_definition(ctx, quest_id) else { return vec![] };
    if !can_start(quest, state) {
        return vec![];
    }
    activate(state, quest);
    vec![Effect::message(message_levels::INFO, format!("New quest: {}", quest.name))]
}

/// TS `{ ...(quests[id] ?? { objectives: {} }), status: 'active' }`.
fn activated(quests: &IndexMap<String, QuestProgress>, quest_id: &str) -> QuestProgress {
    match quests.get(quest_id) {
        Some(existing) => QuestProgress { status: "active".to_owned(), ..existing.clone() },
        None => QuestProgress { status: "active".to_owned(), objectives: Some(IndexMap::new()) },
    }
}

/// The nightly restart of repeatable auto-start quests: each completed one that can start
/// again (prerequisites, availability) begins anew with its objectives reset.
pub fn restart_repeatable_quests(ctx: &EngineContext, state: &mut GameState) -> Effects {
    let mut effects = Vec::new();
    for quest in &ctx.content.quests {
        if quest.auto_start != Some(true)
            || quest.repeatable != Some(true)
            || !state.player.completed_quests.contains(&quest.id)
            || !can_start(quest, state)
        {
            continue;
        }
        activate(state, quest);
        effects.push(Effect::message(message_levels::INFO, format!("New quest: {}", quest.name)));
    }
    effects
}

/// Force-complete a quest by id (event outcome).
pub fn complete_quest_by_id(ctx: &EngineContext, state: &mut GameState, quest_id: &str) -> Effects {
    if get_quest_definition(ctx, quest_id).is_none() {
        return vec![];
    }
    if !state.player.active_quests.iter().any(|id| id == quest_id) {
        return vec![];
    }
    complete_quest(ctx, state, quest_id)
}

/// Activate autoStart quests whose prerequisites are complete.
///
/// C# returned a new state; here auto-start quests activate in place.
pub fn auto_start_quests(ctx: &EngineContext, state: &mut GameState) {
    for quest in &ctx.content.quests {
        if quest.auto_start != Some(true) || !can_start(quest, state) {
            continue;
        }
        activate(state, quest);
    }
}
