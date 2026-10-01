//! A* pathfinding (port of `World/Pathfinding.cs` / world/pathfinding.ts).
//!
//! Simple A* pathfinding on a scene grid (M3 NPC schedules). 4-directional, uniform cost,
//! Manhattan heuristic. Deterministic: among open nodes of equal cost the one added first wins
//! (the reference scanned its open list in insertion order; here a binary heap orders by cost,
//! then insertion sequence, which picks the same node).

use crate::schema::{MachineTypeDefinition, NodeTypeDefinition, Scene, Tile};
use std::cmp::Reverse;
use std::collections::BinaryHeap;

/// TS `PathPoint`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct PathPoint {
    pub x: i32,
    pub y: i32,
}

/// TS `Walkability`: the scene grid, the node and machine definitions, and extra blocked tiles
/// (other NPCs, the player).
#[derive(Debug, Clone, Copy)]
pub struct Walkability<'a> {
    pub scene: &'a Scene,
    pub node_types: &'a [NodeTypeDefinition],
    pub machine_types: &'a [MachineTypeDefinition],
    pub blocked: &'a [PathPoint],
}

const NEIGHBORS: [(i32, i32); 4] = [(0, -1), (0, 1), (-1, 0), (1, 0)];

/// Whether a tile's terrain, gathering node or placed machine blocks walking. Unknown node and
/// machine types block (safe fallback); a depleted node does not. Shared by the player's
/// collision and NPC pathfinding, so NPCs never walk through what stops the player.
pub fn tile_blocks(tile: &Tile, node_types: &[NodeTypeDefinition], machine_types: &[MachineTypeDefinition]) -> bool {
    if tile.collision {
        return true;
    }
    if let Some(node) = &tile.node {
        if node.remaining_health > 0 {
            let definition = node_types.iter().find(|def| def.id == node.type_id);
            if definition.is_none_or(|def| def.blocks_movement) {
                return true;
            }
        }
    }
    if let Some(machine) = &tile.machine {
        let definition = machine_types.iter().find(|def| def.id == machine.type_id);
        if definition.is_none_or(|def| def.blocks_movement) {
            return true;
        }
    }
    false
}

pub fn is_walkable(w: &Walkability<'_>, x: i32, y: i32) -> bool {
    let Some(tile) = w.scene.tile(x, y) else {
        return false;
    };
    if tile_blocks(tile, w.node_types, w.machine_types) {
        return false;
    }
    !w.blocked.iter().any(|point| point.x == x && point.y == y)
}

/// Manhattan distance from `from` to `(x, y)`.
fn manhattan(from: PathPoint, x: i32, y: i32) -> i64 {
    (i64::from(x) - i64::from(from.x)).abs() + (i64::from(y) - i64::from(from.y)).abs()
}

/// An open-list entry: `(f, insertion sequence, x, y, g)`, ordered by its first fields.
type OpenNode = (i64, u64, i32, i32, i64);

/// Flat per-tile bookkeeping for one search (`y * width + x`).
struct Grid {
    width: i32,
    height: i32,
}

impl Grid {
    fn index(&self, x: i32, y: i32) -> Option<usize> {
        (x >= 0 && x < self.width && y >= 0 && y < self.height).then(|| y as usize * self.width as usize + x as usize)
    }

    fn point(&self, index: usize) -> (i32, i32) {
        ((index % self.width as usize) as i32, (index / self.width as usize) as i32)
    }
}

/// [`is_walkable`] per tile, computed once per search.
struct WalkCache {
    cells: Vec<u8>,
}

impl WalkCache {
    const UNKNOWN: u8 = 0;
    const WALKABLE: u8 = 1;
    const BLOCKED: u8 = 2;

    fn new(cells: usize) -> Self {
        Self { cells: vec![Self::UNKNOWN; cells] }
    }

    fn walkable(&mut self, w: &Walkability<'_>, index: usize, x: i32, y: i32) -> bool {
        if self.cells[index] == Self::UNKNOWN {
            self.cells[index] = if is_walkable(w, x, y) { Self::WALKABLE } else { Self::BLOCKED };
        }
        self.cells[index] == Self::WALKABLE
    }
}

/// Whether `goal` can be reached from `start` over walkable tiles (the start tile itself need not
/// be walkable, as in the search). Grows a region from both ends, one ring at a time from the
/// smaller frontier, and stops when they meet or when either side is enclosed: an unreachable
/// goal behind walls (or an NPC boxed in) costs only the small side, not the whole scene.
fn connected(w: &Walkability<'_>, grid: &Grid, cache: &mut WalkCache, start: usize, goal: usize) -> bool {
    const FROM_START: u8 = 1;
    const FROM_GOAL: u8 = 2;
    let mut seen = vec![0_u8; cache.cells.len()];
    seen[start] = FROM_START;
    seen[goal] = FROM_GOAL;
    let mut frontiers = [vec![start], vec![goal]];
    loop {
        let side = usize::from(frontiers[1].len() < frontiers[0].len());
        let (mine, theirs) = if side == 0 { (FROM_START, FROM_GOAL) } else { (FROM_GOAL, FROM_START) };
        if frontiers[side].is_empty() {
            return false;
        }
        let mut next = Vec::new();
        for &cell in &frontiers[side] {
            let (x, y) = grid.point(cell);
            for (dx, dy) in NEIGHBORS {
                let Some(neighbor) = grid.index(x + dx, y + dy) else {
                    continue;
                };
                if seen[neighbor] == theirs {
                    return true;
                }
                if seen[neighbor] == mine {
                    continue;
                }
                // The start tile is entered from the goal side even when it isn't walkable.
                if neighbor != start && !cache.walkable(w, neighbor, x + dx, y + dy) {
                    continue;
                }
                seen[neighbor] = mine;
                next.push(neighbor);
            }
        }
        frontiers[side] = next;
    }
}

/// Find a path from start to goal (exclusive of start, inclusive of goal). Returns `None` when
/// unreachable. Bounded by the scene size.
pub fn find_path(w: &Walkability<'_>, start: PathPoint, goal: PathPoint) -> Option<Vec<PathPoint>> {
    if start.x == goal.x && start.y == goal.y {
        return Some(Vec::new());
    }
    if !is_walkable(w, goal.x, goal.y) {
        return None;
    }
    let grid = Grid { width: w.scene.width.max(0), height: w.scene.height.max(0) };
    // An NPC standing outside its scene has nowhere to path from.
    let start_index = grid.index(start.x, start.y)?;
    let goal_index = grid.index(goal.x, goal.y)?;
    let cells = grid.width as usize * grid.height as usize;
    let mut walkable = WalkCache::new(cells);

    // Unreachable goals are common (a schedule target behind a locked door, the player standing
    // in a doorway) and are searched for again every game minute. Settling reachability first is
    // cheap when either end is enclosed; the path itself still comes from A* alone.
    if !connected(w, &grid, &mut walkable, start_index, goal_index) {
        return None;
    }

    let mut g_score = vec![i64::MAX; cells];
    // The index each tile was reached from (`usize::MAX`: none).
    let mut came_from = vec![usize::MAX; cells];
    let mut closed = vec![false; cells];

    // Min-heap on (f, insertion sequence): the reference took the first lowest-f node of its
    // insertion-ordered open list.
    let mut open: BinaryHeap<Reverse<OpenNode>> = BinaryHeap::new();
    let mut sequence: u64 = 0;
    open.push(Reverse((manhattan(start, goal.x, goal.y), sequence, start.x, start.y, 0)));
    g_score[start_index] = 0;

    let max_iterations = i64::from(grid.width) * i64::from(grid.height) * 4;

    // `for (iterations = 0; open.Count > 0 && iterations < maxIterations; iterations++)`: the
    // counter is bumped before each body so `continue` counts the iteration too.
    let mut iterations: i64 = 0;
    while iterations < max_iterations {
        let Some(Reverse((_, _, x, y, g))) = open.pop() else {
            break;
        };
        iterations += 1;
        let Some(current) = grid.index(x, y) else {
            continue;
        };
        if closed[current] {
            continue;
        }
        closed[current] = true;

        if x == goal.x && y == goal.y {
            // Reconstruct (goal → start), then reverse and drop the start tile.
            let mut path = Vec::new();
            let mut cursor = current;
            while cursor != start_index {
                path.push(PathPoint {
                    x: (cursor % grid.width as usize) as i32,
                    y: (cursor / grid.width as usize) as i32,
                });
                cursor = came_from[cursor];
                if cursor == usize::MAX {
                    break;
                }
            }
            path.reverse();
            return Some(path);
        }

        for (dx, dy) in NEIGHBORS {
            let (nx, ny) = (x + dx, y + dy);
            let Some(neighbor) = grid.index(nx, ny) else {
                continue;
            };
            if closed[neighbor] {
                continue;
            }
            if !walkable.walkable(w, neighbor, nx, ny) {
                continue;
            }
            let tentative_g = g + 1;
            if g_score[neighbor] <= tentative_g {
                continue;
            }
            g_score[neighbor] = tentative_g;
            came_from[neighbor] = current;
            sequence += 1;
            open.push(Reverse((
                tentative_g + manhattan(PathPoint { x: nx, y: ny }, goal.x, goal.y),
                sequence,
                nx,
                ny,
                tentative_g,
            )));
        }
    }

    None
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::schema::TileMachine;
    use crate::world::tiles;

    fn open_scene(width: i32, height: i32) -> Scene {
        tiles::create_empty_scene("s", "S", width, height)
    }

    fn walk<'a>(scene: &'a Scene, machine_types: &'a [MachineTypeDefinition]) -> Walkability<'a> {
        Walkability { scene, node_types: &[], machine_types, blocked: &[] }
    }

    #[test]
    fn finds_the_shortest_path_and_prefers_the_earliest_neighbor_on_ties() {
        let scene = open_scene(5, 5);
        let path = find_path(&walk(&scene, &[]), PathPoint { x: 0, y: 0 }, PathPoint { x: 2, y: 2 }).unwrap();
        assert_eq!(path.len(), 4);
        assert_eq!(path.last(), Some(&PathPoint { x: 2, y: 2 }));
        // Same answer every time (deterministic tie-break).
        assert_eq!(find_path(&walk(&scene, &[]), PathPoint { x: 0, y: 0 }, PathPoint { x: 2, y: 2 }), Some(path));
    }

    #[test]
    fn walled_off_goals_are_unreachable() {
        let mut scene = open_scene(8, 8);
        for y in 0..8 {
            scene.tile_mut(4, y).unwrap().collision = true;
        }
        assert_eq!(find_path(&walk(&scene, &[]), PathPoint { x: 0, y: 0 }, PathPoint { x: 6, y: 6 }), None);
    }

    #[test]
    fn placed_machines_block_unless_their_type_lets_walkers_through() {
        let mut scene = open_scene(3, 1);
        scene.tile_mut(1, 0).unwrap().machine =
            Some(TileMachine { type_id: "keg".to_owned(), ..TileMachine::default() });
        // Unknown machine types block.
        assert!(!is_walkable(&walk(&scene, &[]), 1, 0));
        assert_eq!(find_path(&walk(&scene, &[]), PathPoint { x: 0, y: 0 }, PathPoint { x: 2, y: 0 }), None);
        let keg =
            MachineTypeDefinition { id: "keg".to_owned(), blocks_movement: true, ..MachineTypeDefinition::default() };
        assert!(!is_walkable(&walk(&scene, std::slice::from_ref(&keg)), 1, 0));
        let rug =
            MachineTypeDefinition { id: "keg".to_owned(), blocks_movement: false, ..MachineTypeDefinition::default() };
        assert!(is_walkable(&walk(&scene, std::slice::from_ref(&rug)), 1, 0));
    }

    #[test]
    fn ragged_grids_and_outside_starts_never_panic() {
        let mut scene = open_scene(4, 4);
        scene.tiles[2].truncate(1);
        scene.tiles.truncate(3);
        let w = walk(&scene, &[]);
        assert!(!is_walkable(&w, 3, 2));
        assert!(!is_walkable(&w, 0, 3));
        assert_eq!(find_path(&w, PathPoint { x: 0, y: 0 }, PathPoint { x: 3, y: 3 }), None);
        assert_eq!(find_path(&w, PathPoint { x: -5, y: 0 }, PathPoint { x: 1, y: 1 }), None);
    }
}
