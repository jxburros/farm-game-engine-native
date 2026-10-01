//! NPC schedules and movement (port of `Npcs/NpcMovement.cs` / npcs/movement.ts).
//!
//! NPC movement & schedules (M3). Runs once per whole in-game minute from advanceTick.
//! Patterns: stationary, wander (radius around home), patrol (waypoints), plus time-based
//! schedules (walk to a destination via A*; cross-scene destinations teleport — documented v1
//! simplification).
//!
//! Determinism: wander draws flow through the seeded RNG in state.

use crate::engine_types::EngineContext;
use crate::rng::Rng;
use crate::schema::{GameState, GridPoint, Npc, NpcScheduleEntry, NpcState};
use crate::units;
use crate::weather;
use crate::world::pathfinding::{self, PathPoint, Walkability};
use crate::world::world_movement;

const WANDER_PERIOD_MINUTES: u32 = 3;

const DIRECTIONS: [(i32, i32); 4] = [(0, -1), (0, 1), (-1, 0), (1, 0)];

/// C# `MakeWalkability`: runs `f` with the walkability of `scene_id` for `npc_id` (other NPCs in
/// the scene and the player's tile block, as do placed machines like they block the player);
/// `None` when the scene is missing.
fn with_walkability<R>(
    ctx: &EngineContext,
    state: &GameState,
    npc_id: &str,
    scene_id: &str,
    f: impl FnOnce(&Walkability<'_>) -> R,
) -> Option<R> {
    let scene = state.world.scenes.iter().find(|s| s.id == scene_id)?;
    let mut blocked: Vec<PathPoint> = Vec::new();
    // Other NPCs in the scene block.
    for (other_id, other) in &state.npcs {
        if other_id != npc_id && other.scene_id == scene_id {
            // An NPC standing between tiles blocks no tile (v8 wrote the key "2.5,3").
            if other.x % units::TILE == 0 && other.y % units::TILE == 0 {
                blocked.push(PathPoint { x: units::tile_of(other.x), y: units::tile_of(other.y) });
            }
        }
    }
    // The player blocks too (fractional position → occupied tile).
    if state.player.scene_id == scene_id {
        let player = world_movement::player_tile(state);
        blocked.push(PathPoint { x: player.x, y: player.y });
    }
    Some(f(&Walkability {
        scene,
        node_types: &ctx.content.node_types,
        machine_types: &ctx.content.machine_types,
        blocked: &blocked,
        index: Some(ctx.index()),
    }))
}

fn player_is_adjacent(state: &GameState, npc: &NpcState) -> bool {
    if state.player.scene_id != npc.scene_id {
        return false;
    }
    let player = world_movement::player_tile(state);
    let distance = (i64::from(units::tiles(player.x)) - i64::from(npc.x)).abs()
        + (i64::from(units::tiles(player.y)) - i64::from(npc.y)).abs();
    distance <= i64::from(units::TILE)
}

/// Latest schedule entry whose minute has passed (entries sorted by minute).
fn active_schedule_entry(def: &Npc, time_minutes: u32) -> Option<&NpcScheduleEntry> {
    let schedule = def.schedule.as_ref()?;
    if schedule.is_empty() {
        return None;
    }
    let mut sorted: Vec<&NpcScheduleEntry> = schedule.iter().collect();
    sorted.sort_by(|a, b| a.minute.cmp(&b.minute));
    let mut active = None;
    for entry in sorted {
        if u64::from(entry.minute) * u64::from(units::MINUTE) <= u64::from(time_minutes) {
            active = Some(entry);
        }
    }
    active
}

/// JS comparator result → sign (NaN compares equal, as in Array.prototype.sort).
fn step_along_path(ctx: &EngineContext, state: &GameState, npc_id: &str, npc: &NpcState) -> NpcState {
    let Some(next) = npc.path.as_ref().and_then(|path| path.first()) else {
        return npc.clone();
    };
    let walkable = with_walkability(ctx, state, npc_id, &npc.scene_id, |w| pathfinding::is_walkable(w, next.x, next.y));
    if walkable != Some(true) {
        // Blocked: drop the path; it will be recomputed next minute.
        return NpcState { path: None, ..npc.clone() };
    }
    NpcState {
        x: units::tiles(next.x),
        y: units::tiles(next.y),
        path: npc.path.as_ref().map(|path| path.iter().skip(1).cloned().collect()),
        ..npc.clone()
    }
}

/// The tile an NPC stands on (NPC positions are tile-aligned).
fn npc_tile(npc: &NpcState) -> PathPoint {
    PathPoint { x: units::tile_of(npc.x), y: units::tile_of(npc.y) }
}

fn to_grid_path(path: Option<Vec<PathPoint>>) -> Option<Vec<GridPoint>> {
    path.map(|points| points.into_iter().map(|p| GridPoint { x: p.x, y: p.y }).collect())
}

/// C# returned a new state; here NPCs advance in place.
///
/// The C# tracks `changed`/reference equality to decide whether to write an NPC back; every
/// write here stores the same value the C# would, so the tracking is dropped.
pub fn advance_npcs(ctx: &EngineContext, state: &mut GameState, minutes: u32) {
    if minutes == 0 {
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

    for def in movers {
        let Some(mut npc) = state.npcs.get(&def.id).cloned() else {
            continue;
        };

        // Player proximity pause: never move while the player stands beside.
        if player_is_adjacent(state, &npc) {
            continue;
        }

        // Indoors the storm does not matter: an NPC keeps a schedule that stays inside (#34).
        let indoor = |scene_id: &str| state.world.scenes.iter().any(|scene| scene.id == scene_id && scene.is_indoor());
        let schedule_target = active_schedule_entry(def, state.clock.time_minutes)
            .filter(|target| !stay_inside || (indoor(&npc.scene_id) && indoor(&target.scene_id)));

        if let Some(target) = schedule_target {
            if target.scene_id != npc.scene_id {
                // Cross-scene schedule: teleport (v1 simplification).
                npc.scene_id = target.scene_id.clone();
                npc.x = units::tiles(target.x);
                npc.y = units::tiles(target.y);
                npc.path = None;
                state.npcs.insert(def.id.clone(), npc);
                continue;
            }

            if npc.x != units::tiles(target.x) || npc.y != units::tiles(target.y) {
                if npc.path.as_ref().is_none_or(|path| path.is_empty()) {
                    let path = with_walkability(ctx, state, &def.id, &npc.scene_id, |w| {
                        pathfinding::find_path(w, npc_tile(&npc), PathPoint { x: target.x, y: target.y })
                    })
                    .flatten();
                    npc.path = to_grid_path(path);
                }
                let stepped = step_along_path(ctx, state, &def.id, &npc);
                state.npcs.insert(def.id.clone(), stepped);
                continue;
            }
        }

        if !def.can_move {
            continue;
        }

        if def.move_pattern.as_deref() == Some("patrol") {
            if let Some(patrol_points) = def.patrol_points.as_ref().filter(|points| !points.is_empty()) {
                let count = patrol_points.len();
                let index = npc.patrol_index.unwrap_or(0) as usize % count;
                let waypoint = &patrol_points[index];
                if npc.x == units::tiles(waypoint.x) && npc.y == units::tiles(waypoint.y) {
                    npc.patrol_index = Some(((index + 1) % count) as u32);
                    npc.path = None;
                    state.npcs.insert(def.id.clone(), npc);
                    continue;
                }
                if npc.path.as_ref().is_none_or(|path| path.is_empty()) {
                    let path = with_walkability(ctx, state, &def.id, &npc.scene_id, |w| {
                        pathfinding::find_path(w, npc_tile(&npc), PathPoint { x: waypoint.x, y: waypoint.y })
                    })
                    .flatten();
                    npc.path = to_grid_path(path);
                }
                let stepped = step_along_path(ctx, state, &def.id, &npc);
                state.npcs.insert(def.id.clone(), stepped);
                continue;
            }
        }

        if def.move_pattern.as_deref() == Some("wander") {
            // Move at most once per WANDER_PERIOD_MINUTES; use the minute counter
            // so behavior is time-based rather than frame-based.
            if !units::whole_minute(state.clock.time_minutes).is_multiple_of(WANDER_PERIOD_MINUTES) {
                continue;
            }
            let pick = DIRECTIONS[rng.int(0, 3) as usize];
            let nx = npc.x.saturating_add(units::tiles(pick.0));
            let ny = npc.y.saturating_add(units::tiles(pick.1));
            let radius = i64::from(units::TILE) * i64::from(def.wander_radius.unwrap_or(3));
            if (i64::from(nx) - i64::from(def.x)).abs() > radius || (i64::from(ny) - i64::from(def.y)).abs() > radius {
                continue;
            }
            let walkable = with_walkability(ctx, state, &def.id, &npc.scene_id, |w| {
                pathfinding::is_walkable(w, units::tile_of(nx), units::tile_of(ny))
            });
            if walkable == Some(true) {
                npc.x = nx;
                npc.y = ny;
                state.npcs.insert(def.id.clone(), npc);
            }
        }
    }

    state.rng = rng.state;
}
