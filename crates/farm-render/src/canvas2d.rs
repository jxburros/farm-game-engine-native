//! Port of `Canvas2d.cs`: constants and pure helpers of the reference renderer
//! (packages/renderer-canvas2d/src/index.ts). The drawing itself is [`crate::build_world`].

use crate::css_color::{self, Color};
use crate::num::is_integer;
use crate::snapshot::SnapshotCamera;

/// Color per tile type (the web CSS oklch values).
pub const TILE_COLORS: &[(&str, &str)] = &[
    ("grass", "#5b9a4a"),
    ("soil", "#7a6545"),
    ("water", "#3075b0"),
    ("path", "#b5a48d"),
    ("wall", "#5e5a68"),
    ("door", "#8a6a3f"),
    ("floor", "#b5a48d"),
];

pub const CROP_STAGE_COLORS: [&str; 4] = ["#407546", "#558c52", "#72a94c", "#d6bd24"];
pub const WITHERED_CROP_COLOR: &str = "#7d6a52";
pub const PLAYER_COLOR: &str = "#276b3c";
pub const PLAYER_BORDER_COLOR: &str = "#ffffff80";
pub const NPC_COLOR: &str = "#c28020";
pub const NPC_BORDER_COLOR: &str = "#00000030";
pub const ITEM_COLOR: &str = "#d6bd24";

/// The CSS color of a tile type, if it has one.
pub fn tile_color_css(tile_type: &str) -> Option<&'static str> {
    TILE_COLORS.iter().find(|(name, _)| *name == tile_type).map(|(_, css)| *css)
}

/// Color for a tile type, falling back to `fallback_type`'s color (the TS fallback) when unknown.
pub fn tile_color(tile_type: Option<&str>, fallback_type: &str) -> Color {
    let css = tile_type.and_then(tile_color_css).or_else(|| tile_color_css(fallback_type));
    css_color::parse(css)
}

/// Unit vector per facing direction (unknown directions fall back to down).
pub fn direction_offset(direction: Option<&str>) -> (i32, i32) {
    match direction {
        Some("up") => (0, -1),
        Some("left") => (-1, 0),
        Some("right") => (1, 0),
        _ => (0, 1),
    }
}

/// Follow camera over a `world_width`×`world_height` pixel world: centers the target, clamps to
/// the world edges, and centers the whole world when it is smaller than the viewport. Pure;
/// hosts call it per frame.
pub fn compute_camera(
    target_px: f64,
    target_py: f64,
    world_width: f64,
    world_height: f64,
    view_width: f64,
    view_height: f64,
) -> SnapshotCamera {
    fn axis(target: f64, world: f64, view: f64) -> f64 {
        if world <= view {
            return -(view - world) / 2.0;
        }
        crate::num::cs_min(crate::num::cs_max(target - (view / 2.0), 0.0), world - view)
    }

    SnapshotCamera {
        x: axis(target_px, world_width, view_width),
        y: axis(target_py, world_height, view_height),
        width: view_width,
        height: view_height,
    }
}

/// World-pixel origin (top-left of the tile-sized draw box) for an entity coordinate:
/// fractional coordinates are free-movement box centers, integer coordinates are legacy tile
/// indices.
pub fn entity_pixel_origin(coord: f64, padding: f64, pitch: f64) -> f64 {
    if is_integer(coord) {
        padding + (coord * pitch)
    } else {
        padding + ((coord - 0.5) * pitch)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn camera_centers_the_target_inside_a_larger_world() {
        let cam = compute_camera(500.0, 400.0, 2000.0, 1500.0, 480.0, 320.0);
        assert_eq!((cam.x, cam.y, cam.width, cam.height), (260.0, 240.0, 480.0, 320.0));
    }

    #[test]
    fn camera_clamps_to_world_edges() {
        let top_left = compute_camera(10.0, 10.0, 2000.0, 1500.0, 480.0, 320.0);
        assert_eq!((top_left.x, top_left.y), (0.0, 0.0));
        let bottom_right = compute_camera(1999.0, 1499.0, 2000.0, 1500.0, 480.0, 320.0);
        assert_eq!((bottom_right.x, bottom_right.y), (2000.0 - 480.0, 1500.0 - 320.0));
    }

    #[test]
    fn camera_centers_a_world_smaller_than_the_viewport() {
        let cam = compute_camera(100.0, 50.0, 200.0, 100.0, 480.0, 320.0);
        assert_eq!((cam.x, cam.y), (-140.0, -110.0));
    }

    #[test]
    fn entity_origin_treats_integers_as_tiles_and_fractions_as_centers() {
        assert_eq!(entity_pixel_origin(3.0, 8.0, 32.0), 8.0 + 3.0 * 32.0);
        assert_eq!(entity_pixel_origin(3.5, 8.0, 32.0), 8.0 + 3.0 * 32.0);
        assert_eq!(entity_pixel_origin(3.25, 8.0, 32.0), 8.0 + 2.75 * 32.0);
    }

    #[test]
    fn tile_colors_and_directions() {
        assert_eq!(tile_color(Some("water"), "grass"), Color::rgb(0x30, 0x75, 0xb0));
        assert_eq!(tile_color(Some("lava"), "grass"), Color::rgb(0x5b, 0x9a, 0x4a));
        assert_eq!(tile_color(None, "wall"), Color::rgb(0x5e, 0x5a, 0x68));
        assert_eq!(direction_offset(Some("up")), (0, -1));
        assert_eq!(direction_offset(Some("sideways")), (0, 1));
        assert_eq!(direction_offset(None), (0, 1));
    }
}
