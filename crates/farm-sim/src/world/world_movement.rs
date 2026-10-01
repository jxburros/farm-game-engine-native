//! Player movement and collision (port of `World/WorldMovement.cs` / world/movement.ts).
//!
//! Player movement is FREE (Stardew-style), not grid-based: the position is a fractional
//! tile-unit coordinate (the center of the player's collision box) integrated every tick from a
//! held movement intent. Tiles remain the unit of terrain, collision, farming and interaction
//! targeting — the occupied tile is `Math.floor(x/y)`.
//!
//! Positions are integers in 1/8192 tile ([`units::TILE`]); speed is 1/8192 tile per tick
//! (docs/NUMERICS.md).

use crate::effects::{message_levels, Effect};
use crate::engine_types::{Effects, EngineContext};
use crate::events::{self, EventPosition};
use crate::inventory::{self, AddItemOptions};
use crate::mines;
use crate::quests;
use crate::schema::{GameState, MachineTypeDefinition, MoveIntent, NodeTypeDefinition, NpcState, Scene};
use crate::units;
use crate::world::{pathfinding, tiles};
use indexmap::IndexMap;

/// Half-extents of the player's collision box, in position units (0.3 tile, rounded to the
/// grid). Kept below half a tile so one-tile gaps stay walkable.
pub const PLAYER_HALF_WIDTH: i32 = 2458;
pub const PLAYER_HALF_HEIGHT: i32 = 2458;

/// Collision skin so a clamped position never re-overlaps the blocking tile (one position
/// unit; v8 used 1e-4 tile).
const COLLISION_EPSILON: i32 = 1;

/// Diagonal input is normalized so it isn't √2 faster than cardinal input: the per-tick speed
/// is scaled by 46341/65536 (1/√2 to six digits) and rounded.
const INV_SQRT2_NUMERATOR: i64 = 46341;
const INV_SQRT2_DENOMINATOR: i64 = 65536;

/// TS `getDirectionVector` result.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct DirectionVector {
    pub dx: i32,
    pub dy: i32,
}

/// Integer tile coordinates.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct TilePoint {
    pub x: i32,
    pub y: i32,
}

pub fn get_direction_vector(direction: &str) -> DirectionVector {
    match direction {
        "up" => DirectionVector { dx: 0, dy: -1 },
        "down" => DirectionVector { dx: 0, dy: 1 },
        "left" => DirectionVector { dx: -1, dy: 0 },
        "right" => DirectionVector { dx: 1, dy: 0 },
        _ => DirectionVector { dx: 0, dy: 0 },
    }
}

/// The tile the player currently occupies (position is the box center).
pub fn player_tile(state: &GameState) -> TilePoint {
    TilePoint { x: units::tile_of(state.player.x), y: units::tile_of(state.player.y) }
}

/// The tile the player is facing (interaction/tool target).
pub fn facing_target(state: &GameState) -> TilePoint {
    let DirectionVector { dx, dy } = get_direction_vector(&state.player.direction);
    let origin = player_tile(state);
    TilePoint { x: origin.x + dx, y: origin.y + dy }
}

/// Facing derived from a movement intent. On diagonals the current facing is kept when it
/// already matches one axis (so walking down-left while facing down keeps facing down);
/// otherwise the horizontal axis wins.
pub fn direction_from_intent(intent: &MoveIntent, current: &str) -> String {
    if intent.dx == 0 && intent.dy == 0 {
        return current.to_owned();
    }
    let horizontal = if intent.dx < 0 {
        Some("left")
    } else if intent.dx > 0 {
        Some("right")
    } else {
        None
    };
    let vertical = if intent.dy < 0 {
        Some("up")
    } else if intent.dy > 0 {
        Some("down")
    } else {
        None
    };
    if let (Some(horizontal), Some(vertical)) = (horizontal, vertical) {
        if current == horizontal || current == vertical {
            return current.to_owned();
        }
        return horizontal.to_owned();
    }
    // TS `(horizontal ?? vertical) as Direction`: one of them is set here.
    horizontal.or(vertical).unwrap_or_default().to_owned()
}

/// Collision check against tiles, gathering nodes, machines and NPC occupancy, for the tile
/// `(x, y)`. Outside the scene (or where a malformed grid has no tile) is blocked.
pub fn can_move_to(
    scene: &Scene,
    x: i32,
    y: i32,
    npcs: &IndexMap<String, NpcState>,
    exclude_npc_id: Option<&str>,
    node_types: &[NodeTypeDefinition],
    machine_types: &[MachineTypeDefinition],
) -> bool {
    let Some(tile) = scene.tile(x, y) else {
        return false;
    };
    if pathfinding::tile_blocks(tile, node_types, machine_types) {
        return false;
    }
    // Iterates in insertion order (Object.entries); order doesn't affect the result.
    for (npc_id, npc) in npcs {
        if exclude_npc_id == Some(npc_id.as_str()) {
            continue;
        }
        if npc.scene_id == scene.id && npc.x == units::tiles(x) && npc.y == units::tiles(y) {
            return false;
        }
    }
    true
}

/// Where a warp or door into `scene` at tile `(x, y)` lands the player: the tile itself when it
/// is inside the scene and walkable, otherwise the nearest walkable tile (Manhattan rings around
/// the destination clamped into the scene, scanned top to bottom, left before right). `None`
/// when the scene has no walkable tile at all. NPCs are ignored: they move on.
///
/// Destinations are authored (and plugins pick them at run time), and a scene can be resized
/// after its doors were placed: landing outside the grid or inside a wall would leave the player
/// unable to move (a soft-lock that a save then keeps).
pub fn landing_tile(ctx: &EngineContext, scene: &Scene, x: i32, y: i32) -> Option<TilePoint> {
    let open = |tx: i32, ty: i32| {
        scene
            .tile(tx, ty)
            .is_some_and(|tile| !pathfinding::tile_blocks(tile, &ctx.content.node_types, &ctx.content.machine_types))
    };
    if open(x, y) {
        return Some(TilePoint { x, y });
    }
    if scene.width <= 0 || scene.height <= 0 {
        return None;
    }
    let cx = x.clamp(0, scene.width - 1);
    let cy = y.clamp(0, scene.height - 1);
    let max_radius = scene.width.saturating_add(scene.height);
    for radius in 0..=max_radius {
        for dy in -radius..=radius {
            let rest = radius - dy.abs();
            let (tx, ty) = (cx.saturating_sub(rest), cy.saturating_add(dy));
            if open(tx, ty) {
                return Some(TilePoint { x: tx, y: ty });
            }
            if rest != 0 && open(cx.saturating_add(rest), ty) {
                return Some(TilePoint { x: cx.saturating_add(rest), y: ty });
            }
        }
    }
    None
}

/// Puts the player on tile `(x, y)` of `scene_id` (an existing scene of the world), or on the
/// nearest walkable tile when that one is outside the scene or blocked (see [`landing_tile`]).
/// Returns the tile landed on and, when it moved, a message naming the authored destination.
pub fn land_player(ctx: &EngineContext, state: &mut GameState, scene_id: &str, x: i32, y: i32) -> (TilePoint, Effects) {
    let wanted = TilePoint { x, y };
    let (landed, effects) = match find_scene(state, scene_id) {
        None => (wanted, Vec::new()),
        Some(scene) => match landing_tile(ctx, scene, x, y) {
            Some(tile) if tile == wanted => (tile, Vec::new()),
            Some(tile) => (
                tile,
                vec![Effect::message(
                    message_levels::ERROR,
                    format!(
                        "({x},{y}) in {} can't be stood on; landed on ({},{}) instead.",
                        scene.name, tile.x, tile.y
                    ),
                )],
            ),
            // Nowhere walkable (the scene is all wall): stay inside it at least.
            None => (
                TilePoint { x: x.clamp(0, scene.width.max(1) - 1), y: y.clamp(0, scene.height.max(1) - 1) },
                Vec::new(),
            ),
        },
    };
    state.player.scene_id = scene_id.to_owned();
    state.player.x = units::tile_center(landed.x);
    state.player.y = units::tile_center(landed.y);
    (landed, effects)
}

pub fn find_scene<'a>(state: &'a GameState, scene_id: &str) -> Option<&'a Scene> {
    state.world.scenes.iter().find(|scene| scene.id == scene_id)
}

/// Index of `find_scene`'s result: handlers that mutate the state hold the index, not a borrow.
fn find_scene_index(state: &GameState, scene_id: &str) -> Option<usize> {
    state.world.scenes.iter().position(|scene| scene.id == scene_id)
}

/// TS `byId`: definitions keyed by id, insertion order kept.
pub fn by_id<T: Clone>(defs: &[T], id: impl Fn(&T) -> &str) -> IndexMap<String, T> {
    let mut map = IndexMap::new();
    for def in defs {
        map.insert(id(def).to_owned(), def.clone());
    }
    map
}

struct CollisionContext<'a> {
    scene: &'a Scene,
    npcs: &'a IndexMap<String, NpcState>,
    node_types: &'a [NodeTypeDefinition],
    machine_types: &'a [MachineTypeDefinition],
}

fn make_collision_context<'a>(ctx: &'a EngineContext, state: &'a GameState, scene: &'a Scene) -> CollisionContext<'a> {
    CollisionContext {
        scene,
        npcs: &state.npcs,
        node_types: &ctx.content.node_types,
        machine_types: &ctx.content.machine_types,
    }
}

fn blocked_tile(c: &CollisionContext<'_>, x: i32, y: i32) -> bool {
    !can_move_to(c.scene, x, y, c.npcs, None, c.node_types, c.machine_types)
}

/// The longest step [`move_axis_step`] takes at once: under half a tile, so checking the one
/// cell the leading edge enters cannot skip a cell.
const MAX_AXIS_STEP: i32 = units::TILE / 2 - 1;

/// Move the box center along one axis by `delta`, in steps of at most [`MAX_AXIS_STEP`] so a
/// fast player (a large `playerSpeed`) cannot tunnel through a one-tile wall. Ordinary speeds
/// (about 0.2 tile per tick) take a single step.
fn move_axis(c: &CollisionContext<'_>, x: i32, y: i32, delta: i32, axis_x: bool) -> i32 {
    let mut position = if axis_x { x } else { y };
    let mut remaining = delta;
    while remaining != 0 {
        let step = remaining.clamp(-MAX_AXIS_STEP, MAX_AXIS_STEP);
        let (sx, sy) = if axis_x { (position, y) } else { (x, position) };
        let next = move_axis_step(c, sx, sy, step, axis_x);
        if next != position.saturating_add(step) {
            // Stopped against a wall (or at the end of the coordinate range).
            return next;
        }
        remaining -= step;
        position = next;
    }
    position
}

/// One step of [`move_axis`], clamping against the first blocked tile column/row the leading
/// edge would enter. Axis-separated resolution gives natural wall sliding. `delta` is shorter
/// than half a tile, so the single-cell check cannot tunnel.
fn move_axis_step(c: &CollisionContext<'_>, x: i32, y: i32, delta: i32, axis_x: bool) -> i32 {
    if delta == 0 {
        return if axis_x { x } else { y };
    }
    let along_half = if axis_x { PLAYER_HALF_WIDTH } else { PLAYER_HALF_HEIGHT };
    let cross_half = if axis_x { PLAYER_HALF_HEIGHT } else { PLAYER_HALF_WIDTH };
    let cross = if axis_x { y } else { x };
    let from = if axis_x { x } else { y };
    let mut next = from.saturating_add(delta);

    // Saturating throughout: a save or a plugin can put the position anywhere in the i32 range.
    let cross_start = units::tile_of(cross.saturating_sub(cross_half).saturating_add(COLLISION_EPSILON));
    let cross_end = units::tile_of(cross.saturating_add(cross_half).saturating_sub(COLLISION_EPSILON));
    let leading_edge = if delta > 0 { next.saturating_add(along_half) } else { next.saturating_sub(along_half) };
    let leading_cell = units::tile_of(leading_edge);
    let current_leading_cell = units::tile_of(if delta > 0 {
        from.saturating_add(along_half).saturating_sub(COLLISION_EPSILON)
    } else {
        from.saturating_sub(along_half).saturating_add(COLLISION_EPSILON)
    });

    if leading_cell != current_leading_cell {
        for cc in cross_start..=cross_end {
            let tx = if axis_x { leading_cell } else { cc };
            let ty = if axis_x { cc } else { leading_cell };
            if blocked_tile(c, tx, ty) {
                next = if delta > 0 {
                    units::tiles(leading_cell).saturating_sub(along_half).saturating_sub(COLLISION_EPSILON)
                } else {
                    units::tiles(leading_cell.saturating_add(1))
                        .saturating_add(along_half)
                        .saturating_add(COLLISION_EPSILON)
                };
                break;
            }
        }
    }
    next
}

/// C# `SettleResult` minus the state (updated in place).
struct SettleResult {
    effects: Effects,
    aborted: bool,
}

/// What a pickup the inventory has no room for does to the step that found it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum FullInventory {
    /// The whole step is refused and the state is left exactly as it was (grid steps: the
    /// player never entered the tile).
    AbortStep,
    /// The item stays on the ground and everything else the tile does still happens (free
    /// movement: the player is on the tile, and standing there never settles it again).
    LeaveItem,
}

/// The world's index of `scene_id`, adding the scene from the content first when the world does
/// not have it yet: a scene of an enabled content pack (packs add scenes to the content only), or
/// a project scene added after a save was made. The added scene's grid is normalized.
pub fn ensure_scene(ctx: &EngineContext, state: &mut GameState, scene_id: &str) -> Option<usize> {
    if let Some(index) = find_scene_index(state, scene_id) {
        return Some(index);
    }
    let mut scene = ctx.content.scenes.iter().find(|scene| scene.id == scene_id)?.clone();
    tiles::normalize_scene_grid(&mut scene);
    state.world.scenes.push(scene);
    Some(state.world.scenes.len() - 1)
}

/// Whether `scene_id` is in the world or can be added to it by [`ensure_scene`].
pub fn scene_exists(ctx: &EngineContext, state: &GameState, scene_id: &str) -> bool {
    find_scene(state, scene_id).is_some() || ctx.content.scenes.iter().any(|scene| scene.id == scene_id)
}

/// Everything that happens when the player's occupied TILE changes: unlocked transitions fire,
/// ground items are picked up, quests progress, revealed mine ladders descend, and
/// enter-triggered events evaluate at the final position. Shared by grid steps (`move` command)
/// and free movement.
///
/// `entered_scene_index` is the C# `enteredScene` (a scene of `state.world.scenes`). The state
/// is only touched once the pickup is known to fit, or (`LeaveItem`) known not to happen: an
/// abort leaves it exactly as it was.
fn settle_tile_entry(
    ctx: &EngineContext,
    state: &mut GameState,
    entered_scene_index: usize,
    tile_x: i32,
    tile_y: i32,
    full_inventory: FullInventory,
) -> SettleResult {
    let mut effects: Effects = Vec::new();
    let mut picked_up_item_id: Option<String> = None;
    let mut changed_scene = false;

    // Decisions first (the C# reads them from the old state into locals).
    let entered_scene = &state.world.scenes[entered_scene_index];
    let transition = entered_scene
        .transitions
        .iter()
        .find(|t| t.from_x == tile_x && t.from_y == tile_y && t.locked != Some(true))
        .filter(|t| scene_exists(ctx, state, &t.to_scene_id))
        .map(|t| (t.to_scene_id.clone(), t.to_x, t.to_y));

    // Item pickup checks the tile stepped onto in the ORIGINAL scene
    // (historical behavior, even if a transition just fired).
    let ground_item = entered_scene.tile(tile_x, tile_y).and_then(|tile| tile.item.clone());
    let picked_up_inventory = match &ground_item {
        Some(item) => {
            let result = inventory::add_item(
                &state.player.inventory,
                item,
                1,
                state.player.max_inventory_size,
                Some(AddItemOptions { require_stackable_for_merge: Some(true) }),
            );
            if result.added {
                Some(result.inventory)
            } else if full_inventory == FullInventory::AbortStep {
                return SettleResult { effects: vec![Effect::message("error", "Inventory is full!")], aborted: true };
            } else {
                effects.push(Effect::message("error", "Inventory is full!"));
                None
            }
        }
        None => None,
    };
    let entered_scene_id = entered_scene.id.clone();

    // Everything decided: apply.
    if let Some((to_scene_id, to_x, to_y)) = transition {
        if let Some(target_index) = ensure_scene(ctx, state, &to_scene_id) {
            let target_name = state.world.scenes[target_index].name.clone();
            let (landed, landing_effects) = land_player(ctx, state, &to_scene_id, to_x, to_y);
            changed_scene = true;
            effects.push(Effect::SceneChanged { scene_id: to_scene_id, x: landed.x, y: landed.y });
            effects.push(Effect::message("success", format!("Entered {target_name}")));
            effects.extend(landing_effects);
        }
    }

    if let (Some(item), Some(next_inventory)) = (ground_item, picked_up_inventory) {
        state.player.inventory = next_inventory;
        picked_up_item_id = Some(item.id.clone());
        effects.push(Effect::message("success", format!("Picked up {}", item.name)));

        if let Some(tile) = find_scene_index(state, &entered_scene_id)
            .and_then(|index| state.world.scenes[index].tile_mut(tile_x, tile_y))
        {
            tile.item = None;
        }
    }

    // The C# `player.SceneId` local: the player as settled above, before quests run.
    let landed_scene_id = state.player.scene_id.clone();

    if let Some(item_id) = &picked_up_item_id {
        effects.extend(quests::progress_quests(ctx, state, "collect", item_id, 1));
    }
    if changed_scene {
        effects.extend(quests::progress_quests(ctx, state, "visit", &landed_scene_id, 1));
    }

    // Mine ladders (M4): stepping onto a revealed ladder descends a floor.
    let final_tile = player_tile(state);
    let landed_on_ladder = find_scene(state, &state.player.scene_id)
        .and_then(|landed_scene| landed_scene.tile(final_tile.x, final_tile.y))
        .is_some_and(|landed_tile| landed_tile.ladder_down == Some(true));
    if landed_on_ladder && mines::is_mine_floor(state, &state.player.scene_id) {
        let to_floor = state.mine.current_floor.saturating_add(1);
        effects.extend(mines::descend_mine(ctx, state, to_floor));
        return SettleResult { effects, aborted: false };
    }

    // Enter-triggered events fire at the final position (M3)
    effects.extend(events::evaluate_events(
        ctx,
        state,
        "enter",
        Some(EventPosition { x: final_tile.x, y: final_tile.y }),
    ));

    SettleResult { effects, aborted: false }
}

/// Discrete one-tile step (the legacy `move` command; still the primitive for scripted movement
/// and tests). Historical semantics preserved exactly: the facing direction updates even on a
/// blocked move, and a pickup into a full inventory aborts the whole move (including the
/// direction change).
pub fn handle_move(ctx: &EngineContext, state: &mut GameState, dir: &str) -> Effects {
    let Some(scene_index) = find_scene_index(state, &state.player.scene_id) else {
        return Vec::new();
    };

    let DirectionVector { dx, dy } = get_direction_vector(dir);
    let from = player_tile(state);
    let new_x = from.x + dx;
    let new_y = from.y + dy;

    if !can_move_to(
        &state.world.scenes[scene_index],
        new_x,
        new_y,
        &state.npcs,
        None,
        &ctx.content.node_types,
        &ctx.content.machine_types,
    ) {
        state.player.direction = dir.to_owned();
        return Vec::new();
    }

    // The C# builds a candidate player and drops it on abort; the three fields the candidate
    // changes are remembered here and restored instead (settling touches nothing on abort).
    let previous = (state.player.direction.clone(), state.player.x, state.player.y);
    state.player.direction = dir.to_owned();
    state.player.x = units::tile_center(new_x);
    state.player.y = units::tile_center(new_y);
    let mut effects: Effects = vec![Effect::PlayerMoved { x: new_x, y: new_y }];

    let settled = settle_tile_entry(ctx, state, scene_index, new_x, new_y, FullInventory::AbortStep);
    if settled.aborted {
        // Full-inventory pickup aborts the whole move, direction change included.
        state.player.direction = previous.0;
        state.player.x = previous.1;
        state.player.y = previous.2;
        return settled.effects;
    }
    effects.extend(settled.effects);
    effects
}

/// Integrate free movement for one tick from the held intent. Runs inside `advanceTick`, so
/// replay determinism needs only the intent-change commands in the log — never per-tick
/// positions.
pub fn integrate_movement(ctx: &EngineContext, state: &mut GameState) -> Effects {
    let intent_dx = state.player.move_intent.dx;
    let intent_dy = state.player.move_intent.dy;
    if intent_dx == 0 && intent_dy == 0 {
        return Vec::new();
    }

    let Some(scene_index) = find_scene_index(state, &state.player.scene_id) else {
        return Vec::new();
    };

    // TS `ctx.content.settings.movement?.playerSpeed ?? 4.5`: the config is always present here
    // and defaults to 4.5 tiles per second. The setting is already per tick.
    let speed = ctx.content.settings.movement.player_speed;
    let speed = if intent_dx != 0 && intent_dy != 0 {
        let scaled = units::div_round(i64::from(speed) * INV_SQRT2_NUMERATOR, INV_SQRT2_DENOMINATOR);
        i32::try_from(scaled).unwrap_or(speed)
    } else {
        speed
    };
    let step_x = intent_dx.saturating_mul(speed);
    let step_y = intent_dy.saturating_mul(speed);

    let from_x = state.player.x;
    let from_y = state.player.y;
    let (nx, ny) = {
        let c = make_collision_context(ctx, state, &state.world.scenes[scene_index]);
        let nx = move_axis(&c, from_x, from_y, step_x, true);
        let ny = move_axis(&c, nx, from_y, step_y, false);
        (nx, ny)
    };
    if nx == from_x && ny == from_y {
        return Vec::new();
    }

    let prev_tile = player_tile(state);
    state.player.x = nx;
    state.player.y = ny;
    let mut effects: Effects = Vec::new();

    let tile_x = units::tile_of(nx);
    let tile_y = units::tile_of(ny);
    if tile_x != prev_tile.x || tile_y != prev_tile.y {
        effects.push(Effect::PlayerMoved { x: tile_x, y: tile_y });
        // A full inventory leaves the item on the ground; the move itself stands, and so do the
        // door, the ladder and the enter events of the tile.
        let settled = settle_tile_entry(ctx, state, scene_index, tile_x, tile_y, FullInventory::LeaveItem);
        effects.extend(settled.effects);
    }
    effects
}
