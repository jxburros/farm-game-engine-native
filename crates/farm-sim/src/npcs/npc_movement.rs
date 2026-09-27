//! NPC schedules and movement (port of `Npcs/NpcMovement.cs` / npcs/movement.ts).
//!
//! NPC movement & schedules (M3). Runs once per whole in-game minute from advanceTick.
//! Patterns: stationary, wander (radius around home), patrol (waypoints), plus time-based
//! schedules (walk to a destination via A*; cross-scene destinations teleport — documented v1
//! simplification).
//!
//! Determinism: wander draws flow through the seeded RNG in state.

use crate::engine_types::EngineContext;
use crate::js;
use crate::rng::Rng;
use crate::schema::{GameState, GridPoint, NodeTypeDefinition, Npc, NpcScheduleEntry, NpcState};
use crate::weather;
use crate::world::pathfinding::{self, PathPoint, Walkability};
use crate::world::world_movement;
use indexmap::{IndexMap, IndexSet};
use std::cmp::Ordering;

const WANDER_PERIOD_MINUTES: f64 = 3.0;

const DIRECTIONS: [(f64, f64); 4] = [(0.0, -1.0), (0.0, 1.0), (-1.0, 0.0), (1.0, 0.0)];

/// C# `MakeWalkability`: runs `f` with the walkability of `scene_id` for `npc_id` (other NPCs in
/// the scene and the player's tile block); `None` when the scene is missing. The node map is
/// built once per [`advance_npcs`] call and passed in, since it never changes during one.
fn with_walkability<R>(
    state: &GameState,
    node_types: &IndexMap<String, NodeTypeDefinition>,
    npc_id: &str,
    scene_id: &str,
    f: impl FnOnce(&Walkability<'_>) -> R,
) -> Option<R> {
    let scene = state.world.scenes.iter().find(|s| s.id == scene_id)?;
    let mut blocked: IndexSet<String> = IndexSet::new();
    // Other NPCs in the scene block.
    for (other_id, other) in &state.npcs {
        if other_id != npc_id && other.scene_id == scene_id {
            blocked.insert(format!("{},{}", js::num(other.x), js::num(other.y)));
        }
    }
    // The player blocks too (fractional position → occupied tile).
    if state.player.scene_id == scene_id {
        blocked.insert(format!("{},{}", js::num(state.player.x.floor()), js::num(state.player.y.floor())));
    }
    Some(f(&Walkability { scene, node_types: Some(node_types), blocked: Some(&blocked) }))
}

fn player_is_adjacent(state: &GameState, npc: &NpcState) -> bool {
    if state.player.scene_id != npc.scene_id {
        return false;
    }
    (state.player.x.floor() - npc.x).abs() + (state.player.y.floor() - npc.y).abs() <= 1.0
}

/// Latest schedule entry whose minute has passed (entries sorted by minute).
fn active_schedule_entry(def: &Npc, time_minutes: f64) -> Option<&NpcScheduleEntry> {
    let schedule = def.schedule.as_ref()?;
    if schedule.is_empty() {
        return None;
    }
    let mut sorted: Vec<&NpcScheduleEntry> = schedule.iter().collect();
    sorted.sort_by(|a, b| compare_numbers(a.minute - b.minute));
    let mut active = None;
    for entry in sorted {
        if entry.minute <= time_minutes {
            active = Some(entry);
        }
    }
    active
}

/// JS comparator result → sign (NaN compares equal, as in Array.prototype.sort).
fn compare_numbers(diff: f64) -> Ordering {
    if diff < 0.0 {
        Ordering::Less
    } else if diff > 0.0 {
        Ordering::Greater
    } else {
        Ordering::Equal
    }
}

fn step_along_path(
    state: &GameState,
    node_types: &IndexMap<String, NodeTypeDefinition>,
    npc_id: &str,
    npc: &NpcState,
) -> NpcState {
    let Some(next) = npc.path.as_ref().and_then(|path| path.first()) else {
        return npc.clone();
    };
    let walkable =
        with_walkability(state, node_types, npc_id, &npc.scene_id, |w| pathfinding::is_walkable(w, next.x, next.y));
    if walkable != Some(true) {
        // Blocked: drop the path; it will be recomputed next minute.
        return NpcState { path: None, ..npc.clone() };
    }
    NpcState {
        x: next.x,
        y: next.y,
        path: npc.path.as_ref().map(|path| path.iter().skip(1).cloned().collect()),
        ..npc.clone()
    }
}

fn to_grid_path(path: Option<Vec<PathPoint>>) -> Option<Vec<GridPoint>> {
    path.map(|points| points.into_iter().map(|p| GridPoint { x: p.x, y: p.y }).collect())
}

/// C# returned a new state; here NPCs advance in place.
///
/// The C# tracks `changed`/reference equality to decide whether to write an NPC back; every
/// write here stores the same value the C# would, so the tracking is dropped.
pub fn advance_npcs(ctx: &EngineContext, state: &mut GameState, minutes: f64) {
    if minutes <= 0.0 {
        return;
    }
    let movers: Vec<&Npc> = ctx
        .content
        .npcs
        .iter()
        .filter(|def| def.can_move || def.schedule.as_ref().is_some_and(|schedule| !schedule.is_empty()))
        .collect();
    if movers.is_empty() {
        return;
    }

    let mut rng = Rng::new(state.rng.clone());

    // Storms keep scheduled NPCs home (M4b).
    let stay_inside = weather::current_weather(ctx, state).is_some_and(|w| w.npcs_stay_inside);
    let node_types = world_movement::by_id(&ctx.content.node_types, |def| def.id.as_str());

    for def in movers {
        let Some(mut npc) = state.npcs.get(&def.id).cloned() else {
            continue;
        };

        // Player proximity pause: never move while the player stands beside.
        if player_is_adjacent(state, &npc) {
            continue;
        }

        let schedule_target = if stay_inside { None } else { active_schedule_entry(def, state.clock.time_minutes) };

        if let Some(target) = schedule_target {
            if target.scene_id != npc.scene_id {
                // Cross-scene schedule: teleport (v1 simplification).
                npc.scene_id = target.scene_id.clone();
                npc.x = target.x;
                npc.y = target.y;
                npc.path = None;
                state.npcs.insert(def.id.clone(), npc);
                continue;
            }

            if npc.x != target.x || npc.y != target.y {
                if npc.path.as_ref().is_none_or(|path| path.is_empty()) {
                    let path = with_walkability(state, &node_types, &def.id, &npc.scene_id, |w| {
                        pathfinding::find_path(
                            w,
                            PathPoint { x: npc.x, y: npc.y },
                            PathPoint { x: target.x, y: target.y },
                        )
                    })
                    .flatten();
                    npc.path = to_grid_path(path);
                }
                let stepped = step_along_path(state, &node_types, &def.id, &npc);
                state.npcs.insert(def.id.clone(), stepped);
                continue;
            }
        }

        if !def.can_move {
            continue;
        }

        if def.move_pattern.as_deref() == Some("patrol") {
            if let Some(patrol_points) = def.patrol_points.as_ref().filter(|points| !points.is_empty()) {
                let index = npc.patrol_index.unwrap_or(0.0) % patrol_points.len() as f64;
                let waypoint = &patrol_points[index as usize];
                if npc.x == waypoint.x && npc.y == waypoint.y {
                    npc.patrol_index = Some((index + 1.0) % patrol_points.len() as f64);
                    npc.path = None;
                    state.npcs.insert(def.id.clone(), npc);
                    continue;
                }
                if npc.path.as_ref().is_none_or(|path| path.is_empty()) {
                    let path = with_walkability(state, &node_types, &def.id, &npc.scene_id, |w| {
                        pathfinding::find_path(
                            w,
                            PathPoint { x: npc.x, y: npc.y },
                            PathPoint { x: waypoint.x, y: waypoint.y },
                        )
                    })
                    .flatten();
                    npc.path = to_grid_path(path);
                }
                let stepped = step_along_path(state, &node_types, &def.id, &npc);
                state.npcs.insert(def.id.clone(), stepped);
                continue;
            }
        }

        if def.move_pattern.as_deref() == Some("wander") {
            // Move at most once per WANDER_PERIOD_MINUTES; use the minute counter
            // so behavior is time-based rather than frame-based.
            if state.clock.time_minutes.floor() % WANDER_PERIOD_MINUTES != 0.0 {
                continue;
            }
            let pick = DIRECTIONS[rng.int(0.0, 3.0) as usize];
            let nx = npc.x + pick.0;
            let ny = npc.y + pick.1;
            let radius = def.wander_radius.unwrap_or(3.0);
            if (nx - def.x).abs() > radius || (ny - def.y).abs() > radius {
                continue;
            }
            let walkable =
                with_walkability(state, &node_types, &def.id, &npc.scene_id, |w| pathfinding::is_walkable(w, nx, ny));
            if walkable == Some(true) {
                npc.x = nx;
                npc.y = ny;
                state.npcs.insert(def.id.clone(), npc);
            }
        }
    }

    state.rng = rng.state;
}
