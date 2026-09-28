//! Draw-list and raster tests: the C# `SkiaWorldRendererTests` ported to the Rust backend, plus
//! checks on the draw list itself (pass order, y-sort, culling, sprite bounds).

use base64::Engine as _;
use farm_render::tiny_skia::Pixmap;
use farm_render::{
    apply_graphics, build_world, css_color, editor_snapshot, encode_png, shell_snapshot, BuiltinArt, Color, DrawCmd,
    GraphicsSource, ImageStore, Sampling, SnapshotAtmosphere, SnapshotCamera, SnapshotCrop, SnapshotEntity,
    SnapshotItem, SnapshotNode, SnapshotOptions, SnapshotPop, SnapshotSprite, SnapshotTile, WorldRenderer,
    WorldSnapshot,
};
use std::sync::Arc;

const TS: f64 = 32.0;

fn scene(rows: &[&[&str]]) -> WorldSnapshot {
    WorldSnapshot {
        width: rows[0].len() as i32,
        height: rows.len() as i32,
        tile_size: TS,
        padding: 0.0,
        tile_gap: Some(0.0),
        player: SnapshotEntity { x: -10.0, y: -10.0, ..SnapshotEntity::default() },
        tiles: rows
            .iter()
            .map(|row| {
                row.iter().map(|t| SnapshotTile { background: (*t).to_owned(), ..SnapshotTile::default() }).collect()
            })
            .collect(),
        ..WorldSnapshot::default()
    }
}

/// A renderer drawing colored shapes only (no built-in art).
fn color_blocks() -> WorldRenderer {
    WorldRenderer { art: None, ..WorldRenderer::new() }
}

fn with_art() -> WorldRenderer {
    WorldRenderer::new()
}

fn pixel(pixmap: &Pixmap, x: f64, y: f64) -> Color {
    let p = pixmap.pixel(x as u32, y as u32).unwrap().demultiply();
    Color::rgba(p.red(), p.green(), p.blue(), p.alpha())
}

fn assert_color(expected_css: &str, actual: Color, tolerance: i32) {
    let expected = css_color::parse(Some(expected_css));
    let close = |a: u8, b: u8| (i32::from(a) - i32::from(b)).abs() <= tolerance;
    assert!(
        close(expected.r, actual.r) && close(expected.g, actual.g) && close(expected.b, actual.b),
        "expected {expected} got {actual}"
    );
}

fn difference(a: Color, b: Color) -> i32 {
    (i32::from(a.r) - i32::from(b.r)).abs()
        + (i32::from(a.g) - i32::from(b.g)).abs()
        + (i32::from(a.b) - i32::from(b.b)).abs()
}

fn distinct_colors(pixmap: &Pixmap, x0: u32, y0: u32, width: u32, height: u32) -> usize {
    let mut colors: Vec<u32> = Vec::new();
    for y in y0..y0 + height {
        for x in x0..x0 + width {
            colors.push(pixel(pixmap, f64::from(x), f64::from(y)).to_u32());
        }
    }
    colors.sort();
    colors.dedup();
    colors.len()
}

fn data_url(width: u32, height: u32, pixels: &[Color]) -> String {
    let mut pixmap = Pixmap::new(width, height).unwrap();
    for (target, color) in pixmap.pixels_mut().iter_mut().zip(pixels) {
        *target = farm_render::tiny_skia::ColorU8::from_rgba(color.r, color.g, color.b, color.a).premultiply();
    }
    format!("data:image/png;base64,{}", base64::engine::general_purpose::STANDARD.encode(encode_png(&pixmap)))
}

fn sprite(url: &str, frame: f64) -> SnapshotSprite {
    SnapshotSprite {
        image_url: Arc::from(url),
        frame_width: 1.0,
        frame_height: 1.0,
        frame,
        ..SnapshotSprite::default()
    }
}

fn starter() -> farm_sim::GameProject {
    let fixture: serde_json::Value =
        serde_json::from_str(include_str!("../../../fixtures/golden/content/starter-farm.json")).unwrap();
    serde_json::from_value(fixture["project"].clone()).unwrap()
}

// ---- color blocks ----------------------------------------------------------------------------

#[test]
fn draws_tile_backgrounds_in_their_palette_colors() {
    let bitmap = color_blocks().render(&scene(&[&["grass", "water", "soil"], &["path", "wall", "unknown-type"]]), 1.0);
    assert_eq!((bitmap.width(), bitmap.height()), (96, 64));
    assert_color("#5b9a4a", pixel(&bitmap, 16.0, 16.0), 3);
    assert_color("#3075b0", pixel(&bitmap, 48.0, 16.0), 3);
    assert_color("#7a6545", pixel(&bitmap, 80.0, 16.0), 3);
    assert_color("#b5a48d", pixel(&bitmap, 16.0, 48.0), 3);
    assert_color("#5e5a68", pixel(&bitmap, 48.0, 48.0), 3);
    assert_color("#5b9a4a", pixel(&bitmap, 80.0, 48.0), 3); // unknown types fall back to grass
}

#[test]
fn draws_overlay_at_70_percent_and_objects_opaque() {
    let mut snapshot = scene(&[&["grass", "grass"]]);
    snapshot.tiles[0][0].overlay = Some("path".into());
    snapshot.tiles[0][1].object = Some("wall".into());
    let bitmap = color_blocks().render(&snapshot, 1.0);
    // 0.7 × path + 0.3 × grass (on top of the faint tint).
    let (path, grass) = (css_color::parse(Some("#b5a48d")), css_color::parse(Some("#5b9a4a")));
    let mix = |a: u8, b: u8| (0.7 * f64::from(a) + 0.3 * f64::from(b)) as u8;
    let blended = Color::rgb(mix(path.r, grass.r), mix(path.g, grass.g), mix(path.b, grass.b));
    assert_color(&blended.to_string(), pixel(&bitmap, 16.0, 16.0), 4);
    assert_color("#5e5a68", pixel(&bitmap, 48.0, 16.0), 3);
}

#[test]
fn draws_crop_stages_withered_crops_nodes_and_items() {
    let mut snapshot = scene(&[&["soil", "soil", "grass", "grass"]]);
    snapshot.tiles[0][0].crop = Some(SnapshotCrop { color_index: 1, ..SnapshotCrop::default() });
    snapshot.tiles[0][1].crop = Some(SnapshotCrop { color_index: 9, withered: true, ..SnapshotCrop::default() });
    snapshot.tiles[0][2].node = Some(SnapshotNode { color: "#aa0000".into(), ..SnapshotNode::default() });
    snapshot.tiles[0][3].item = Some(SnapshotItem::default());
    let bitmap = color_blocks().render(&snapshot, 1.0);
    assert_color("#558c52", pixel(&bitmap, 16.0, 16.0), 3);
    assert_color("#7d6a52", pixel(&bitmap, 48.0, 16.0), 3);
    assert_color("#aa0000", pixel(&bitmap, 80.0, 16.0), 3);
    assert_color("#d6bd24", pixel(&bitmap, 112.0, 16.0), 3);
    // Crop squares are 35% of the tile: the tile corner stays soil.
    assert_color("#7a6545", pixel(&bitmap, 3.0, 3.0), 3);
}

#[test]
fn draws_npcs_and_the_player_with_a_facing_dot() {
    let mut snapshot = scene(&[&["grass", "grass", "grass"], &["grass", "grass", "grass"]]);
    snapshot.npcs.push(SnapshotEntity::default());
    snapshot.player = SnapshotEntity { x: 2.5, y: 1.5, direction: "up".into(), ..SnapshotEntity::default() };
    let bitmap = color_blocks().render(&snapshot, 1.0);
    assert_color("#c28020", pixel(&bitmap, 16.0, 16.0), 3);
    assert_color("#276b3c", pixel(&bitmap, 2.0 * TS + 16.0, TS + 16.0), 3);
    let dot_top = pixel(&bitmap, 2.0 * TS + 16.0, TS + 5.0);
    assert!(dot_top.r > 200 && dot_top.g > 200, "expected the facing dot at the top, got {dot_top}");
    assert_color("#276b3c", pixel(&bitmap, 2.0 * TS + 16.0, TS + 24.0), 3);
}

#[test]
fn camera_translates_and_culls_the_world() {
    let mut snapshot = scene(&[&["water", "grass", "soil"]]);
    snapshot.camera = Some(SnapshotCamera { x: TS, y: 0.0, width: TS, height: TS });
    let bitmap = color_blocks().render(&snapshot, 1.0);
    assert_eq!(bitmap.width(), 32);
    assert_color("#5b9a4a", pixel(&bitmap, 16.0, 16.0), 3);
}

#[test]
fn pixel_art_uses_nearest_neighbour_sampling() {
    let url = data_url(2, 1, &[Color::rgb(255, 0, 0), Color::rgb(0, 0, 255)]);
    let mut snapshot = scene(&[&["grass"]]);
    snapshot.tiles[0][0].image_url = Some(Arc::from(url.as_str()));
    snapshot.pixel_art = Some(true);
    let mut renderer = color_blocks();
    let crisp = renderer.render(&snapshot, 1.0);
    assert_color("#ff0000", pixel(&crisp, 15.0, 16.0), 3);
    assert_color("#0000ff", pixel(&crisp, 16.0, 16.0), 3);
    snapshot.pixel_art = Some(false);
    let smooth = renderer.render(&snapshot, 1.0);
    let mid = pixel(&smooth, 15.0, 16.0);
    assert!(mid.r < 250 && mid.b > 5, "smooth sampling should blend at the seam, got {mid}");
    assert_eq!(renderer.images.len(), 1, "only the creator image enters the cache");
}

#[test]
fn sprite_frames_are_cropped_from_the_sheet() {
    let url = data_url(2, 1, &[Color::rgb(255, 0, 0), Color::rgb(0, 255, 0)]);
    let mut snapshot = scene(&[&["water"]]);
    snapshot.tiles[0][0].art_layers = Some(vec![Some(sprite(&url, 1.0)), None, None]);
    let mut renderer = color_blocks();
    assert_color("#00ff00", pixel(&renderer.render(&snapshot, 1.0), 16.0, 16.0), 3);
    // Out-of-bounds frames fall back to the tile color.
    snapshot.tiles[0][0].art_layers = Some(vec![Some(sprite(&url, 5.0)), None, None]);
    assert_color("#3075b0", pixel(&renderer.render(&snapshot, 1.0), 16.0, 16.0), 3);
}

#[test]
fn edit_grid_uses_one_pixel_seams() {
    let mut snapshot = scene(&[&["water", "water"]]);
    snapshot.tile_gap = Some(1.0);
    snapshot.grid_overlay = true;
    let bitmap = color_blocks().render(&snapshot, 1.0);
    assert_eq!(bitmap.width(), 65);
    assert!(pixel(&bitmap, TS, 16.0).a < 40, "the seam column stays (nearly) transparent");
    assert_color("#3075b0", pixel(&bitmap, TS + 17.0, 16.0), 3);
}

#[test]
fn scale_multiplies_the_bitmap_size() {
    let bitmap = color_blocks().render(&scene(&[&["water", "grass"]]), 2.0);
    assert_eq!((bitmap.width(), bitmap.height()), (128, 64));
    assert_color("#3075b0", pixel(&bitmap, 60.0, 30.0), 3);
    assert_color("#5b9a4a", pixel(&bitmap, 70.0, 30.0), 3);
}

// ---- built-in art -----------------------------------------------------------------------------

#[test]
fn starter_farm_renders_with_built_in_art() {
    let project = starter();
    let content = farm_sim::create_content_from_project(&project);
    let state = farm_sim::create_game_state(&project, None);
    let scene = state.world.scenes.iter().find(|s| s.id == state.player.scene_id).unwrap();
    let snapshot = shell_snapshot(&content, &state, scene, &SnapshotOptions { tile_size: TS, ..Default::default() });
    let bitmap = with_art().render(&snapshot, 1.0);
    assert!(distinct_colors(&bitmap, 32 * 5, 32, 32, 32) >= 3, "grass should be textured");
    assert!(distinct_colors(&bitmap, 0, 0, bitmap.width(), bitmap.height()) > 40);

    let (tx, ty) = scene
        .tiles
        .iter()
        .flatten()
        .find(|t| t.node.as_ref().is_some_and(|n| n.type_id == "node-tree"))
        .map(|t| (t.x, t.y))
        .unwrap();
    let trunk = pixel(&bitmap, tx * TS + 16.0, ty * TS + 29.0);
    let grass = pixel(&bitmap, (tx + 2.0) * TS + 16.0, ty * TS + 29.0);
    assert!(difference(trunk, grass) > 40, "tree {trunk} should differ from grass {grass}");
    let canopy = pixel(&bitmap, tx * TS + 16.0, (ty - 1.0) * TS + 20.0);
    let plain = pixel(&bitmap, (tx + 2.0) * TS + 16.0, (ty - 1.0) * TS + 20.0);
    assert!(difference(canopy, plain) > 40, "canopy {canopy} should cover the tile above, got grass {plain}");
    let wall = pixel(&bitmap, 16.0, 16.0);
    assert!(difference(wall, css_color::parse(Some("#5e5a68"))) > 10, "wall tiles should be textured stone");
}

#[test]
fn soil_states_and_crop_stages_change_the_tile() {
    let mut snapshot = scene(&[&["soil", "soil", "soil", "soil"]]);
    snapshot.tiles[0][1].watered = true;
    snapshot.tiles[0][2].fertilized = true;
    snapshot.tiles[0][3].crop = Some(SnapshotCrop {
        crop_id: Some("wheat".into()),
        color_index: 3,
        stages: 4,
        mature: true,
        ..SnapshotCrop::default()
    });
    let bitmap = with_art().render(&snapshot, 1.0);
    let sum = |tile: u32| -> u32 {
        (0..32)
            .flat_map(|y| (0..32).map(move |x| (x, y)))
            .map(|(x, y)| {
                let c = pixel(&bitmap, f64::from(tile * 32 + x), f64::from(y));
                u32::from(c.r) + u32::from(c.g) + u32::from(c.b)
            })
            .sum()
    };
    assert!(f64::from(sum(1)) < f64::from(sum(0)) * 0.85, "watered soil should be darker");
    assert!(distinct_colors(&bitmap, 64, 0, 32, 32) > distinct_colors(&bitmap, 0, 0, 32, 32), "fertilizer speckles");
    let greenish = (0..32)
        .flat_map(|y| (0..32).map(move |x| (x, y)))
        .filter(|&(x, y)| {
            let p = pixel(&bitmap, f64::from(96 + x), f64::from(y));
            i32::from(p.g) > i32::from(p.r) + 10 || (p.r > 200 && p.g > 180)
        })
        .count();
    assert!(greenish > 15, "a ripe wheat plant should show green/yellow pixels, found {greenish}");
}

#[test]
fn creator_art_wins_over_built_in_art() {
    let url = data_url(1, 1, &[Color::rgb(255, 0, 255)]);
    let mut snapshot = scene(&[&["grass", "grass"]]);
    snapshot.tiles[0][0].art_layers = Some(vec![Some(sprite(&url, 0.0)), None, None]);
    snapshot.tiles[0][1].node = Some(SnapshotNode {
        type_id: Some("node-rock".into()),
        sprite: Some(sprite(&url, 0.0)),
        ..SnapshotNode::default()
    });
    snapshot.npcs.push(SnapshotEntity {
        x: 1.0,
        appearance: Some("farmer".into()),
        sprite: Some(sprite(&url, 0.0)),
        ..SnapshotEntity::default()
    });
    let bitmap = with_art().render(&snapshot, 1.0);
    assert_color("#ff00ff", pixel(&bitmap, 16.0, 16.0), 3);
    assert_color("#ff00ff", pixel(&bitmap, TS + 16.0, 16.0), 3);
}

fn atmosphere(minutes: f64, weather: &str, season: &str) -> Option<SnapshotAtmosphere> {
    Some(SnapshotAtmosphere {
        time_minutes: minutes,
        weather_id: Some(weather.into()),
        season: Some(season.into()),
        weather_overlay: None,
    })
}

#[test]
fn atmosphere_tint_changes_pixels_between_noon_and_midnight() {
    let mut snapshot = scene(&[&["grass", "grass"], &["grass", "grass"]]);
    let mut renderer = with_art();
    snapshot.atmosphere = atmosphere(720.0, "sun", "spring");
    let noon = renderer.render(&snapshot, 1.0);
    snapshot.atmosphere = atmosphere(0.0, "sun", "spring");
    let midnight = renderer.render(&snapshot, 1.0);
    snapshot.atmosphere = None;
    let none = renderer.render(&snapshot, 1.0);
    let (day, night) = (pixel(&noon, 16.0, 16.0), pixel(&midnight, 16.0, 16.0));
    assert_eq!(pixel(&none, 16.0, 16.0), day, "midday: no tint");
    assert!(night.r < day.r && night.g < day.g, "night {night} should be darker than day {day}");
    assert!(night.b > night.r, "night should be cool blue, got {night}");
    snapshot.atmosphere = atmosphere(18.0 * 60.0 + 40.0, "sun", "spring");
    let evening = pixel(&renderer.render(&snapshot, 1.0), 16.0, 16.0);
    assert!(evening.b < day.b && evening.r >= evening.b, "dusk should be warm, got {evening}");
}

#[test]
fn seasons_and_weather_change_the_frame_deterministically() {
    let mut snapshot = scene(&[&["grass", "grass"], &["grass", "grass"]]);
    snapshot.tiles[1][1].node = Some(SnapshotNode { type_id: Some("node-tree".into()), ..SnapshotNode::default() });
    let mut renderer = with_art();
    snapshot.atmosphere = atmosphere(720.0, "sun", "spring");
    let spring = renderer.render(&snapshot, 1.0);
    snapshot.atmosphere = atmosphere(720.0, "sun", "fall");
    let fall = renderer.render(&snapshot, 1.0);
    snapshot.atmosphere = atmosphere(720.0, "sun", "winter");
    let winter = renderer.render(&snapshot, 1.0);
    assert!(difference(pixel(&spring, 16.0, 16.0), pixel(&fall, 16.0, 16.0)) > 30, "autumn grass should be ochre");
    assert!(difference(pixel(&spring, 16.0, 16.0), pixel(&winter, 16.0, 16.0)) > 30, "winter grass should be frosty");
    // The tree canopy takes the foliage tint.
    assert_ne!(pixel(&spring, 48.0, 40.0), pixel(&fall, 48.0, 40.0));

    snapshot.atmosphere = atmosphere(720.0, "rain", "spring");
    snapshot.tick = 40.0;
    let rain_a = renderer.render(&snapshot, 1.0);
    let rain_b = with_art().render(&snapshot, 1.0);
    assert_eq!(rain_a.data(), rain_b.data(), "weather is a pure function of the tick (and caches don't matter)");
    snapshot.tick = 41.0;
    assert_ne!(rain_a.data(), renderer.render(&snapshot, 1.0).data(), "rain moves between ticks");
    snapshot.atmosphere = atmosphere(720.0, "snow", "winter");
    assert_ne!(renderer.render(&snapshot, 1.0).data(), winter.data(), "snow flakes are drawn");
}

#[test]
fn player_behind_a_tree_is_partly_covered_and_in_front_covers_it() {
    let make = |player_y: f64| {
        let mut s = scene(&[&["grass"], &["grass"], &["grass"], &["grass"]]);
        s.tiles[2][0].node = Some(SnapshotNode { type_id: Some("node-tree".into()), ..SnapshotNode::default() });
        s.player = SnapshotEntity { x: 0.5, y: player_y, ..SnapshotEntity::default() };
        s
    };
    let mut empty = scene(&[&["grass"], &["grass"], &["grass"], &["grass"]]);
    empty.player = SnapshotEntity { x: 0.5, y: 1.5, ..SnapshotEntity::default() };
    let mut renderer = with_art();
    let behind = renderer.render(&make(1.5), 1.0);
    let no_tree = renderer.render(&empty, 1.0);
    let tree_only = renderer.render(&make(-10.0), 1.0);
    let (mut covered, mut visible) = (0, 0);
    for y in 32..64 {
        for x in 0..32 {
            let (canopy, with_player, grass) = (
                pixel(&tree_only, f64::from(x), f64::from(y)),
                pixel(&behind, f64::from(x), f64::from(y)),
                pixel(&no_tree, f64::from(x), f64::from(y)),
            );
            if difference(canopy, grass) > 40 && with_player == canopy {
                covered += 1;
            }
            if with_player != canopy {
                visible += 1;
            }
        }
    }
    assert!(covered > 50, "the canopy should cover part of the player ({covered} px)");
    assert!(visible > 20, "the player should still peek out ({visible} px)");
    let front = renderer.render(&make(2.5), 1.0);
    let overlaps = (64..96)
        .flat_map(|y| (0..32).map(move |x| (x, y)))
        .filter(|&(x, y)| pixel(&front, f64::from(x), f64::from(y)) != pixel(&tree_only, f64::from(x), f64::from(y)))
        .count();
    assert!(overlaps > 50, "the player in front should cover the trunk ({overlaps} px)");
}

#[test]
fn walking_player_animates_with_the_tick_and_idles_when_still() {
    let mut snapshot = scene(&[&["grass", "grass"], &["grass", "grass"]]);
    snapshot.player =
        SnapshotEntity { x: 1.0, y: 1.0, direction: "right".into(), moving: true, ..SnapshotEntity::default() };
    let ticks = f64::from(BuiltinArt::embedded().find("char-player").unwrap().ticks_per_frame.unwrap());
    let mut renderer = with_art();
    snapshot.tick = ticks;
    let frame1 = renderer.render(&snapshot, 1.0);
    snapshot.tick = ticks * 2.0;
    let frame2 = renderer.render(&snapshot, 1.0);
    assert_ne!(frame1.data(), frame2.data(), "walk frames should differ");
    snapshot.player.moving = false;
    let idle_a = renderer.render(&snapshot, 1.0);
    snapshot.tick = ticks * 3.0;
    assert_eq!(idle_a.data(), renderer.render(&snapshot, 1.0).data(), "standing still shows one idle frame");
}

#[test]
fn edit_mode_tiles_at_28_pixels_stay_consistent() {
    let project = starter();
    let content = farm_sim::create_content_from_project(&project);
    let snapshot = editor_snapshot(&project, &content, &project.scenes[0], 28.0, 12.0);
    let bitmap = with_art().render(&snapshot, 1.0);
    let (w, h) = snapshot.world_pixel_size();
    assert_eq!((bitmap.width(), bitmap.height()), (w.ceil() as u32, h.ceil() as u32));
    assert!(pixel(&bitmap, 12.0 + 28.0, 12.0 + 14.0).a < 40, "seams stay (nearly) transparent");
    assert!(distinct_colors(&bitmap, 12 + 29, 12 + 29, 28, 28) >= 3, "28-px tiles are still textured");
}

#[test]
fn pops_draw_bold_text_with_an_outline() {
    let mut snapshot = scene(&[&["water", "water", "water"]]);
    let plain = color_blocks().render(&snapshot, 1.0);
    snapshot.pops = Some(vec![SnapshotPop { x: 1.0, y: 0.0, text: "+15g".into(), color: None, age: 0.0 }]);
    let with_pop = color_blocks().render(&snapshot, 1.0);
    let changed = (0..32)
        .flat_map(|y| (0..96).map(move |x| (x, y)))
        .filter(|&(x, y)| pixel(&plain, f64::from(x), f64::from(y)) != pixel(&with_pop, f64::from(x), f64::from(y)))
        .count();
    assert!(changed > 30, "the pop should be visible ({changed} px)");
    // The text is yellow somewhere and has a dark outline somewhere.
    let colors: Vec<Color> = (0..32)
        .flat_map(|y| (0..96).map(move |x| (x, y)))
        .map(|(x, y)| pixel(&with_pop, f64::from(x), f64::from(y)))
        .collect();
    assert!(colors.iter().any(|c| c.r > 200 && c.g > 170 && c.b < 150), "yellow fill");
    assert!(colors.iter().any(|c| u32::from(c.r) + u32::from(c.g) + u32::from(c.b) < 150), "dark outline");
}

// ---- the draw list itself ---------------------------------------------------------------------

#[test]
fn world_is_one_save_restore_pair_with_clip_and_camera_first() {
    let mut snapshot = scene(&[&["grass", "grass"]]);
    snapshot.camera = Some(SnapshotCamera { x: 5.5, y: -2.0, width: 40.0, height: 20.0 });
    let list = build_world(&snapshot, None, &mut ImageStore::default());
    assert_eq!(list.commands[0], DrawCmd::Save);
    assert!(matches!(list.commands[1], DrawCmd::ClipRect { rect } if rect.width == 40.0 && rect.height == 20.0));
    assert!(matches!(list.commands[2], DrawCmd::FillRect { anti_alias: false, .. }));
    assert_eq!(list.commands[3], DrawCmd::Translate { dx: -5.5, dy: 2.0 });
    assert_eq!(list.commands.last(), Some(&DrawCmd::Restore));
    let saves = list.commands.iter().filter(|c| matches!(c, DrawCmd::Save)).count();
    let restores = list.commands.iter().filter(|c| matches!(c, DrawCmd::Restore)).count();
    assert_eq!(saves, restores);
}

#[test]
fn entities_are_y_sorted_with_stable_ties() {
    // Three NPCs: two on the same row (ties keep insertion order), one above them.
    let mut snapshot = scene(&[&["grass", "grass"], &["grass", "grass"]]);
    let npc =
        |x: f64, y: f64, color: &str| SnapshotEntity { x, y, color: Some(color.into()), ..SnapshotEntity::default() };
    snapshot.npcs = vec![npc(0.0, 1.0, "#010101"), npc(1.0, 1.0, "#020202"), npc(0.0, 0.0, "#030303")];
    snapshot.player = SnapshotEntity { x: 1.0, y: 0.0, ..SnapshotEntity::default() };
    let list = build_world(&snapshot, None, &mut ImageStore::default());
    let order: Vec<Color> = list
        .commands
        .iter()
        .filter_map(|c| match c {
            DrawCmd::FillRoundRect { color, .. } => Some(*color),
            _ => None,
        })
        .collect();
    let rgb = |v: u8| Color::rgb(v, v, v);
    let player = css_color::parse(Some("#276b3c"));
    assert_eq!(order, vec![rgb(3), player, rgb(1), rgb(2)]);
}

#[test]
fn camera_culls_tiles_outside_the_viewport_plus_one_row_of_objects() {
    let rows: Vec<Vec<&str>> = (0..10).map(|_| vec!["grass"; 10]).collect();
    let rows: Vec<&[&str]> = rows.iter().map(Vec::as_slice).collect();
    let mut snapshot = scene(&rows);
    for row in &mut snapshot.tiles {
        for tile in row {
            tile.node = Some(SnapshotNode::default());
        }
    }
    snapshot.camera = Some(SnapshotCamera { x: 64.0, y: 64.0, width: 64.0, height: 64.0 });
    let list = build_world(&snapshot, None, &mut ImageStore::default());
    let ground =
        list.commands.iter().filter(|c| matches!(c, DrawCmd::FillRect { rect, .. } if rect.width == 32.0)).count();
    // Columns and rows 2..=4 (the ceil reaches one tile past the edge): 3 × 3 tiles.
    assert_eq!(ground, 9);
    // Objects: one extra row below the viewport (rows 2..=5), same columns.
    let nodes = list.commands.iter().filter(|c| matches!(c, DrawCmd::StrokeCircle { .. })).count();
    assert_eq!(nodes, 12);
}

#[test]
fn sprites_outside_their_image_are_not_drawn() {
    let url = data_url(2, 2, &[Color::rgb(1, 2, 3); 4]);
    let mut snapshot = scene(&[&["grass", "grass", "grass"]]);
    let frame = |x: f64, y: f64, w: f64, h: f64| SnapshotSprite {
        image_url: Arc::from(url.as_str()),
        source_x: Some(x),
        source_y: Some(y),
        frame_width: w,
        frame_height: h,
        ..SnapshotSprite::default()
    };
    snapshot.tiles[0][0].art_layers = Some(vec![Some(frame(1.0, 1.0, 1.0, 1.0)), None, None]);
    snapshot.tiles[0][1].art_layers = Some(vec![Some(frame(1.0, 0.0, 2.0, 1.0)), None, None]);
    snapshot.tiles[0][2].art_layers = Some(vec![Some(frame(-1.0, 0.0, 1.0, 1.0)), None, None]);
    let mut images = ImageStore::default();
    let list = build_world(&snapshot, None, &mut images);
    let images_drawn: Vec<_> = list
        .commands
        .iter()
        .filter_map(|c| match c {
            DrawCmd::Image { src, dst, sampling, opacity, .. } => Some((*src, *dst, *sampling, *opacity)),
            _ => None,
        })
        .collect();
    assert_eq!(images_drawn.len(), 1);
    let (src, dst, sampling, opacity) = images_drawn[0];
    assert_eq!((src.x, src.y, src.width, src.height), (1.0, 1.0, 1.0, 1.0));
    assert_eq!((dst.x, dst.y, dst.width, dst.height), (0.0, 0.0, 32.0, 32.0));
    assert_eq!((sampling, opacity), (Sampling::Nearest, 1.0));
}

#[test]
fn graphics_decorate_the_shell_snapshot_with_creator_art() {
    let mut project = starter();
    let url = data_url(1, 1, &[Color::rgb(255, 0, 255)]);
    project.custom_assets.push(farm_sim::schema::CustomAsset {
        id: "magenta".into(),
        name: "Magenta".into(),
        r#type: "art".into(),
        data_url: url.clone(),
        width: Some(1.0),
        height: Some(1.0),
        ..Default::default()
    });
    let visual = farm_sim::schema::VisualRef { asset_id: "magenta".into(), ..Default::default() };
    project.player_visual = Some(visual.clone());
    // A plain tile (nothing drawn over its background).
    let (tx, ty) = project.scenes[0]
        .tiles
        .iter()
        .enumerate()
        .find_map(|(y, row)| {
            row.iter()
                .position(|t| t.overlay.is_none() && t.object.is_none() && t.node.is_none() && t.item.is_none())
                .map(|x| (x, y))
        })
        .unwrap();
    project.scenes[0].tiles[ty][tx].visuals =
        Some(farm_sim::schema::TileVisuals { background: Some(visual), ..Default::default() });
    let content = farm_sim::create_content_from_project(&project);
    let state = farm_sim::create_game_state(&project, None);
    let scene = &state.world.scenes[0];
    let mut snapshot =
        shell_snapshot(&content, &state, scene, &SnapshotOptions { tile_size: 32.0, ..Default::default() });
    let presentation = farm_sim::Presentation::from_project(&project);
    let source = GraphicsSource::from_state(&presentation, &content, &state);
    apply_graphics(&mut snapshot, &source, scene, 2.0, false);
    assert_eq!(snapshot.pixel_art, Some(true));
    assert_eq!(snapshot.player.sprite.as_ref().map(|s| s.image_url.to_string()), Some(url.clone()));
    assert_eq!(snapshot.tiles[ty][tx].art_layer(0).map(|s| s.image_url.to_string()), Some(url));
    // Sprites share the source's URL allocation (no per-frame copies of data URLs).
    assert!(Arc::ptr_eq(&snapshot.player.sprite.as_ref().unwrap().image_url, &source.assets.last().unwrap().image_url));
    snapshot.atmosphere = None;
    let bitmap = with_art().render(&snapshot, 1.0);
    assert_color("#ff00ff", pixel(&bitmap, tx as f64 * 32.0 + 4.0, ty as f64 * 32.0 + 4.0), 3);
}
