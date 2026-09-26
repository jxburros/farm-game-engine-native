//! Quests (port of `Quests/Quests.cs` / quests/quests.ts).

use crate::engine_types::{Effects, EngineContext};
use crate::schema::{GameState, Quest};

/// `kind` is 'collect' | 'harvest' | 'talk' | 'visit' | 'craft' | 'gift'.
pub fn progress_quests(
    ctx: &EngineContext,
    state: &mut GameState,
    kind: &str,
    target_id: &str,
    amount: f64,
) -> Effects {
    let _ = (ctx, state, kind, target_id, amount);
    todo!("port Quests.ProgressQuests")
}

pub fn is_quest_available(quest: &Quest, state: &GameState) -> bool {
    let _ = (quest, state);
    todo!("port Quests.IsQuestAvailable")
}

pub fn start_quest_by_id(ctx: &EngineContext, state: &mut GameState, quest_id: &str) -> Effects {
    let _ = (ctx, state, quest_id);
    todo!("port Quests.StartQuestById")
}

pub fn complete_quest_by_id(ctx: &EngineContext, state: &mut GameState, quest_id: &str) -> Effects {
    let _ = (ctx, state, quest_id);
    todo!("port Quests.CompleteQuestById")
}

/// C# returned a new state; here auto-start quests activate in place.
pub fn auto_start_quests(ctx: &EngineContext, state: &mut GameState) {
    let _ = (ctx, state);
    todo!("port Quests.AutoStartQuests")
}
