//! A* pathfinding (port of `World/Pathfinding.cs` / world/pathfinding.ts).

use crate::schema::{NodeTypeDefinition, Scene};
use indexmap::{IndexMap, IndexSet};

/// TS `PathPoint`.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct PathPoint {
    pub x: f64,
    pub y: f64,
}

/// TS `Walkability`: the scene grid, node definitions by id, and extra blocked tiles keyed `"x,y"`.
#[derive(Debug, Clone)]
pub struct Walkability<'a> {
    pub scene: &'a Scene,
    pub node_types: Option<&'a IndexMap<String, NodeTypeDefinition>>,
    pub blocked: Option<&'a IndexSet<String>>,
}

pub fn is_walkable(w: &Walkability<'_>, x: f64, y: f64) -> bool {
    let _ = (w, x, y);
    todo!("port Pathfinding.IsWalkable")
}

pub fn find_path(w: &Walkability<'_>, start: PathPoint, goal: PathPoint) -> Option<Vec<PathPoint>> {
    let _ = (w, start, goal);
    todo!("port Pathfinding.FindPath")
}
