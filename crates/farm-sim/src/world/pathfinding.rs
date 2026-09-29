//! A* pathfinding (port of `World/Pathfinding.cs` / world/pathfinding.ts).
//!
//! Simple A* pathfinding on a scene grid (M3 NPC schedules). 4-directional, uniform cost,
//! Manhattan heuristic. Deterministic: ties broken by insertion order.

use crate::schema::{NodeTypeDefinition, Scene};
use indexmap::{IndexMap, IndexSet};

/// TS `PathPoint`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct PathPoint {
    pub x: i32,
    pub y: i32,
}

/// TS `Walkability`: the scene grid, node definitions by id, and extra blocked tiles keyed `"x,y"`.
#[derive(Debug, Clone)]
pub struct Walkability<'a> {
    pub scene: &'a Scene,
    pub node_types: Option<&'a IndexMap<String, NodeTypeDefinition>>,
    pub blocked: Option<&'a IndexSet<String>>,
}

const NEIGHBORS: [(i32, i32); 4] = [(0, -1), (0, 1), (-1, 0), (1, 0)];

pub fn is_walkable(w: &Walkability<'_>, x: i32, y: i32) -> bool {
    let scene = w.scene;
    if x < 0 || x >= scene.width || y < 0 || y >= scene.height {
        return false;
    }
    let tile = &scene.tiles[y as usize][x as usize];
    if tile.collision {
        return false;
    }
    if let Some(node) = &tile.node {
        if node.remaining_health > 0 {
            let definition = w.node_types.and_then(|types| types.get(&node.type_id));
            if definition.is_none_or(|def| def.blocks_movement) {
                return false;
            }
        }
    }
    if w.blocked.is_some_and(|blocked| blocked.contains(&key(x, y))) {
        return false;
    }
    true
}

struct OpenNode {
    x: i32,
    y: i32,
    f: i64,
    g: i64,
}

/// TS `` `${x},${y}` ``.
fn key(x: i32, y: i32) -> String {
    format!("{x},{y}")
}

/// Find a path from start to goal (exclusive of start, inclusive of goal). Returns `None` when
/// unreachable. Bounded by the scene size.
/// Manhattan distance from `from` to `(x, y)`.
fn manhattan(from: PathPoint, x: i32, y: i32) -> i64 {
    (i64::from(x) - i64::from(from.x)).abs() + (i64::from(y) - i64::from(from.y)).abs()
}

pub fn find_path(w: &Walkability<'_>, start: PathPoint, goal: PathPoint) -> Option<Vec<PathPoint>> {
    if start.x == goal.x && start.y == goal.y {
        return Some(Vec::new());
    }
    if !is_walkable(w, goal.x, goal.y) {
        return None;
    }

    let mut open = vec![OpenNode { x: start.x, y: start.y, f: manhattan(start, goal.x, goal.y), g: 0 }];
    // neighbor key → (previous key, previous x, previous y)
    let mut came_from: IndexMap<String, (String, i32, i32)> = IndexMap::new();
    let mut g_score: IndexMap<String, i64> = IndexMap::new();
    g_score.insert(key(start.x, start.y), 0);
    let mut closed: IndexSet<String> = IndexSet::new();

    let max_iterations = i64::from(w.scene.width) * i64::from(w.scene.height) * 4;

    // `for (iterations = 0; open.Count > 0 && iterations < maxIterations; iterations++)`: the
    // counter is bumped before each body so `continue` counts the iteration too.
    let mut iterations: i64 = 0;
    while !open.is_empty() && iterations < max_iterations {
        iterations += 1;
        // Lowest f wins; stable for determinism.
        let mut best_index = 0;
        for (i, node) in open.iter().enumerate().skip(1) {
            if node.f < open[best_index].f {
                best_index = i;
            }
        }
        let current = open.remove(best_index);
        let current_key = key(current.x, current.y);
        if closed.contains(&current_key) {
            continue;
        }
        closed.insert(current_key.clone());

        if current.x == goal.x && current.y == goal.y {
            // Reconstruct (goal → start), then reverse and drop the start tile.
            let mut path = Vec::new();
            let start_key = key(start.x, start.y);
            let mut cursor: Option<String> = Some(current_key);
            let mut cx = current.x;
            let mut cy = current.y;
            while let Some(current_cursor) = cursor {
                if current_cursor.is_empty() || current_cursor == start_key {
                    break;
                }
                path.push(PathPoint { x: cx, y: cy });
                match came_from.get(&current_cursor) {
                    Some((previous_key, px, py)) => {
                        cursor = Some(previous_key.clone());
                        cx = *px;
                        cy = *py;
                    }
                    None => cursor = None,
                }
            }
            path.reverse();
            return Some(path);
        }

        for (dx, dy) in NEIGHBORS {
            let nx = current.x + dx;
            let ny = current.y + dy;
            let neighbor_key = key(nx, ny);
            if closed.contains(&neighbor_key) {
                continue;
            }
            if !is_walkable(w, nx, ny) {
                continue;
            }
            let tentative_g = current.g + 1;
            if g_score.get(&neighbor_key).is_some_and(|known| *known <= tentative_g) {
                continue;
            }
            g_score.insert(neighbor_key.clone(), tentative_g);
            came_from.insert(neighbor_key, (current_key.clone(), current.x, current.y));
            open.push(OpenNode {
                x: nx,
                y: ny,
                f: tentative_g + manhattan(PathPoint { x: nx, y: ny }, goal.x, goal.y),
                g: tentative_g,
            });
        }
    }

    None
}
