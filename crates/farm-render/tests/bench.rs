//! Frame timing of the CPU renderer on a play viewport (20×13 tiles of 32 px, 640×416).
//!
//! Ignored by default; run it in release mode:
//! `cargo test --release -p farm-render --test bench -- --ignored --nocapture`.
// Wall-clock timing is what this test measures; it never feeds the simulation.
#![allow(clippy::disallowed_types, clippy::disallowed_methods)]

use farm_render::{
    apply_graphics, build_world, compute_camera, shell_snapshot, tiny_skia, GraphicsSource, SnapshotOptions,
    TileWindow, WorldRenderer,
};
use std::time::Instant;

#[test]
#[ignore = "timing; run in release with --ignored --nocapture"]
fn play_viewport_frame_time() {
    let fixture: serde_json::Value =
        serde_json::from_str(include_str!("../../../fixtures/golden/content/starter-farm.json")).unwrap();
    let project: farm_sim::GameProject = serde_json::from_value(fixture["project"].clone()).unwrap();
    let content = farm_sim::create_content_from_project(&project);
    let mut state = farm_sim::create_game_state(&project, Some("bench"));
    // Midday rain: modulate tint, weather particles and blurred shadows are all on the frame.
    state.clock.weather_id = "rain".into();
    state.clock.time_minutes = farm_sim::units::minutes(19 * 60);
    let scene = state.world.scenes.iter().find(|s| s.id == state.player.scene_id).unwrap().clone();
    let (ts, padding) = (32.0, 12.0);
    let (view_width, view_height) = (20.0 * ts, 13.0 * ts);
    let world = (f64::from(scene.width) * ts + padding * 2.0, f64::from(scene.height) * ts + padding * 2.0);
    let source = GraphicsSource::from_state(&farm_sim::Presentation::from_project(&project), &content, &state);
    let mut renderer = WorldRenderer::new();
    let mut pixmap = tiny_skia::Pixmap::new(view_width as u32, view_height as u32).unwrap();

    let frames = 300;
    let (mut snapshot_ms, mut build_ms, mut raster_ms) = (0.0, 0.0, 0.0);
    for frame in 0..frames {
        let started = Instant::now();
        let px = padding + (2.0 + f64::from(frame % 40) * 0.25) * ts;
        let camera = compute_camera(px, padding + 6.0 * ts, world.0, world.1, view_width, view_height);
        let window = TileWindow::for_camera(&camera, padding, ts, 2);
        let options = SnapshotOptions {
            tile_size: ts,
            padding,
            pixel_x: Some(px),
            pixel_y: None,
            camera: Some(camera),
            tile_window: Some(window),
        };
        let mut snapshot = shell_snapshot(&content, &state, &scene, &options);
        snapshot.tick = f64::from(frame);
        apply_graphics(&mut snapshot, &source, &scene, f64::from(frame), true);
        let built = Instant::now();
        let list = build_world(&snapshot, renderer.art, &mut renderer.images);
        let listed = Instant::now();
        pixmap.fill(tiny_skia::Color::TRANSPARENT);
        renderer.rasterizer.render(&list, &renderer.images, &mut pixmap.as_mut(), tiny_skia::Transform::identity());
        let done = Instant::now();
        if frame >= 20 {
            snapshot_ms += (built - started).as_secs_f64() * 1000.0;
            build_ms += (listed - built).as_secs_f64() * 1000.0;
            raster_ms += (done - listed).as_secs_f64() * 1000.0;
        }
    }
    let measured = f64::from(frames - 20);
    println!(
        "640×416 play viewport: snapshot {:.3} ms, draw list {:.3} ms, raster {:.3} ms, total {:.3} ms/frame",
        snapshot_ms / measured,
        build_ms / measured,
        raster_ms / measured,
        (snapshot_ms + build_ms + raster_ms) / measured
    );
}
