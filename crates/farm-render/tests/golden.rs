//! Golden images: the start scene of every sample project, rendered through the play (shell) and
//! Edit Mode (editor) snapshots, must match the PNGs in `fixtures/render/`.
//!
//! Regenerate them after an intended change with `FARM_RENDER_BLESS=1 cargo test -p farm-render
//! --test golden`, then look at the images before committing.

use farm_render::{
    apply_graphics, editor_snapshot, encode_png, images::decode_image, shell_snapshot, GraphicsSource, SnapshotOptions,
    SnapshotPop, WorldRenderer, WorldSnapshot,
};
use farm_sim::{GameProject, Presentation};
use std::path::PathBuf;

/// Small tiles keep the checked-in images small; the art is drawn at half its native scale.
const TILE_SIZE: f64 = 16.0;
/// Pixels that may differ by more than [`CHANNEL_TOLERANCE`] (anti-aliasing and float noise).
const MAX_DIFFERENT_FRACTION: f64 = 0.002;
const CHANNEL_TOLERANCE: i32 = 3;

fn root() -> PathBuf {
    [env!("CARGO_MANIFEST_DIR"), "..", ".."].iter().collect()
}

fn project(name: &str) -> GameProject {
    let path = root().join("fixtures").join("golden").join("content").join(format!("{name}.json"));
    let fixture: serde_json::Value = serde_json::from_str(&std::fs::read_to_string(path).unwrap()).unwrap();
    serde_json::from_value(fixture["project"].clone()).unwrap()
}

fn shell(project: &GameProject, pops: bool) -> WorldSnapshot {
    let content = farm_sim::create_content_from_project(project);
    let state = farm_sim::create_game_state(project, Some("render-golden"));
    let scene = state.world.scenes.iter().find(|s| s.id == state.player.scene_id).unwrap();
    let options = SnapshotOptions { tile_size: TILE_SIZE, padding: 6.0, ..Default::default() };
    let mut snapshot = shell_snapshot(&content, &state, scene, &options);
    if pops {
        snapshot.pops = Some(vec![
            SnapshotPop { x: 3.0, y: 3.0, text: "+15g".into(), color: None, age: 0.25 },
            SnapshotPop { x: 7.0, y: 5.0, text: "Wheat".into(), color: Some("#7fd6cc".into()), age: 0.0 },
        ]);
    }
    let source = GraphicsSource::from_state(&Presentation::from_project(project), &content, &state);
    apply_graphics(&mut snapshot, &source, scene, state.clock.tick as f64, false);
    snapshot
}

fn editor(project: &GameProject) -> WorldSnapshot {
    let content = farm_sim::create_content_from_project(project);
    let scene = project.scenes.iter().find(|s| s.id == project.player.scene_id).unwrap_or(&project.scenes[0]);
    let mut snapshot = editor_snapshot(project, &content, scene, TILE_SIZE - 2.0, 6.0);
    apply_graphics(&mut snapshot, &GraphicsSource::from_project(project), scene, 0.0, false);
    snapshot
}

fn check(name: &str, snapshot: &WorldSnapshot) -> Result<(), String> {
    let pixmap = WorldRenderer::new().render(snapshot, 1.0);
    let path = root().join("fixtures").join("render").join(format!("{name}.png"));
    if std::env::var_os("FARM_RENDER_BLESS").is_some() {
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(&path, encode_png(&pixmap)).unwrap();
        return Ok(());
    }
    let bytes = std::fs::read(&path)
        .map_err(|e| format!("{name}: {e}; run with FARM_RENDER_BLESS=1 to create {}", path.display()))?;
    let golden = decode_image(&bytes).map_err(|e| format!("{name}: {e}"))?;
    if (golden.width(), golden.height()) != (pixmap.width(), pixmap.height()) {
        return Err(format!(
            "{name}: size {}×{} differs from the golden {}×{}",
            pixmap.width(),
            pixmap.height(),
            golden.width(),
            golden.height()
        ));
    }
    let different = pixmap
        .data()
        .chunks_exact(4)
        .zip(golden.pixels().chunks_exact(4))
        .filter(|(a, b)| a.iter().zip(b.iter()).any(|(x, y)| (i32::from(*x) - i32::from(*y)).abs() > CHANNEL_TOLERANCE))
        .count();
    let fraction = different as f64 / f64::from(pixmap.width() * pixmap.height());
    if fraction > MAX_DIFFERENT_FRACTION {
        let actual = std::env::temp_dir().join(format!("farm-render-{name}.png"));
        std::fs::write(&actual, encode_png(&pixmap)).ok();
        return Err(format!(
            "{name}: {different} pixels differ ({:.3}%); actual frame written to {}",
            fraction * 100.0,
            actual.display()
        ));
    }
    Ok(())
}

#[test]
fn sample_projects_match_their_golden_images() {
    let mut failures = Vec::new();
    for name in ["starter-farm", "cozy-garden", "quest-rpg", "blank"] {
        let project = project(name);
        let shell = shell(&project, name == "starter-farm");
        for (kind, snapshot) in [("shell", shell), ("editor", editor(&project))] {
            if let Err(message) = check(&format!("{name}-{kind}"), &snapshot) {
                failures.push(message);
            }
        }
    }
    assert!(failures.is_empty(), "{}", failures.join("\n"));
}
