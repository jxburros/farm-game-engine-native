//! Skills (port of `Skills.cs` / skills.ts).
//!
//! Player skills (M4g): XP per action category; levels unlock recipes and grant small passive
//! modifiers. Toggleable per project.

use crate::effects::{message_levels, Effect};
use crate::engine_types::{Effects, EngineContext};
use crate::schema::{GameState, SkillState};

/// The level `xp` reaches on a level curve (the last threshold it meets; 0 below the first).
pub fn skill_level_for_xp(curve: &[u32], xp: u32) -> u32 {
    let mut level = 0;
    for (i, threshold) in curve.iter().enumerate() {
        if xp >= *threshold {
            level = u32::try_from(i).unwrap_or(u32::MAX);
        }
    }
    level
}

/// TS `skill.charAt(0).toUpperCase() + skill.slice(1)` (C# `skill[..1].ToUpperInvariant() + skill[1..]`).
fn skill_label(skill: &str) -> String {
    let mut chars = skill.chars();
    match chars.next() {
        Some(first) => first.to_uppercase().chain(chars).collect(),
        None => String::new(),
    }
}

/// Grant `xp` in `skill`, announcing a level-up. Nothing happens with skills off.
pub fn grant_xp(ctx: &EngineContext, state: &mut GameState, skill: &str, xp: u32) -> Effects {
    if !ctx.content.settings.skills_enabled || xp == 0 {
        return Vec::new();
    }

    let current = state.player.skills.get(skill).cloned().unwrap_or(SkillState { xp: 0, level: 0 });
    let new_xp = current.xp.saturating_add(xp);
    let new_level = skill_level_for_xp(&ctx.content.settings.skill_level_curve, new_xp);
    let mut effects = Vec::new();
    if new_level > current.level {
        let label = skill_label(skill);
        effects.push(Effect::message(message_levels::SUCCESS, format!("{label} level {new_level}!")));
    }

    // `dict[skill] = …`: an existing key keeps its position, a new one is appended.
    state.player.skills.insert(skill.to_owned(), SkillState { xp: new_xp, level: new_level });
    effects
}

/// The player's level in `skill` (0 untrained).
pub fn skill_level(state: &GameState, skill: &str) -> u32 {
    state.player.skills.get(skill).map_or(0, |s| s.level)
}

/// Farming levels add bonus yield: +1 per 4 levels.
pub fn farming_yield_bonus(state: &GameState) -> u32 {
    skill_level(state, "farming") / 4
}
