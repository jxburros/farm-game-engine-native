//! Frame timing of the graphical player (simulation, UI, world raster, upscale and UI raster).
//!
//! Ignored by default; run it in release mode:
//! `cargo test --release -p farm-player --test bench -- --ignored --nocapture`.
// Wall-clock timing is what this test measures; it never feeds the simulation.
#![allow(clippy::disallowed_types, clippy::disallowed_methods)]

mod common;

use common::{press, Stores, FRAME};
use farm_player::Player;
use farm_sim::schema::ShopSession;
use std::time::Instant;

fn measure(label: &str, player: &mut Player, width: u32, height: u32, walk: bool) {
    let frames = 240;
    let events = |frame: usize| {
        if !walk {
            return Vec::new();
        }
        // Walk left and right so the camera and interpolation move.
        match frame % 120 {
            0 => vec![common::key_down("d")],
            60 => vec![common::key_up("d"), common::key_down("a")],
            119 => vec![common::key_up("a")],
            _ => Vec::new(),
        }
    };
    for frame in 0..30 {
        player.frame(FRAME, &events(frame), width, height).unwrap();
    }
    let started = Instant::now();
    for frame in 0..frames {
        player.frame(FRAME, &events(frame), width, height).unwrap();
    }
    let total = started.elapsed().as_secs_f64() * 1000.0 / frames as f64;
    let started = Instant::now();
    for frame in 0..frames {
        player.step(FRAME, &events(frame), width, height).unwrap();
    }
    let logic = started.elapsed().as_secs_f64() * 1000.0 / frames as f64;
    // The UI layer alone, rasterized onto the last frame.
    let list = player.ui_draw_list().clone();
    let mut pixmap = player.pixels().clone();
    let mut rasterizer = farm_render::Rasterizer::new();
    let images = farm_render::ImageStore::default();
    let transform = farm_render::tiny_skia::Transform::identity();
    rasterizer.render(&list, &images, &mut pixmap.as_mut(), transform);
    let started = Instant::now();
    for _ in 0..frames {
        rasterizer.render(&list, &images, &mut pixmap.as_mut(), transform);
    }
    let ui = started.elapsed().as_secs_f64() * 1000.0 / frames as f64;
    println!(
        "{label:<20} {width}×{height}: {total:.2} ms/frame ({logic:.2} ms simulation + UI layout, {:.2} ms pixels, of which UI {ui:.2} ms for {} draw commands)",
        total - logic,
        list.len()
    );
}

#[test]
#[ignore = "timing; run in release with --ignored --nocapture"]
fn frame_times() {
    for (width, height) in [(1280, 800), (1920, 1080)] {
        let stores = Stores::new();
        let mut title = stores.standalone(&common::starter());
        measure("title screen", &mut title, width, height, false);

        let mut game = stores.standalone(&common::starter());
        common::idle(&mut game, 1);
        press(&mut game, "enter");
        measure("gameplay (walking)", &mut game, width, height, true);

        let mut state = game.state().unwrap().clone();
        state.shop = Some(ShopSession { shop_id: "shop-general".into() });
        game.replace_state(state).unwrap();
        measure("shop open", &mut game, width, height, false);
    }
}

/// The slowest UI draw commands of the shop frame at 1920×1080 (profiling aid).
#[test]
#[ignore = "timing; run in release with --ignored --nocapture"]
fn slowest_ui_commands() {
    use farm_render::DrawCmd;
    let stores = Stores::new();
    let mut game = stores.standalone(&common::starter());
    common::idle(&mut game, 1);
    press(&mut game, "enter");
    let mut state = game.state().unwrap().clone();
    state.shop = Some(ShopSession { shop_id: "shop-general".into() });
    game.replace_state(state).unwrap();
    for _ in 0..5 {
        game.frame(FRAME, &[], 1920, 1080).unwrap();
    }
    let list = game.ui_draw_list().clone();
    let mut pixmap = game.pixels().clone();
    let images = farm_render::ImageStore::default();
    let mut rasterizer = farm_render::Rasterizer::new();
    let transform = farm_render::tiny_skia::Transform::identity();
    let mut prefix: Vec<Vec<DrawCmd>> = vec![Vec::new()];
    let mut timings = Vec::new();
    for command in &list.commands {
        match command {
            DrawCmd::Save => prefix.push(Vec::new()),
            DrawCmd::Restore => {
                prefix.pop();
            }
            DrawCmd::Scale { .. } | DrawCmd::Translate { .. } | DrawCmd::ClipRect { .. } => {
                prefix.last_mut().unwrap().push(command.clone());
            }
            other => {
                let mut commands: Vec<DrawCmd> = prefix.iter().flatten().cloned().collect();
                commands.push(other.clone());
                let single = farm_render::DrawList { commands };
                rasterizer.render(&single, &images, &mut pixmap.as_mut(), transform);
                let started = Instant::now();
                for _ in 0..20 {
                    rasterizer.render(&single, &images, &mut pixmap.as_mut(), transform);
                }
                let ms = started.elapsed().as_secs_f64() * 1000.0 / 20.0;
                let mut text = format!("{other:?}");
                text.truncate(140);
                timings.push((ms, text));
            }
        }
    }
    timings.sort_by(|a, b| b.0.total_cmp(&a.0));
    let total: f64 = timings.iter().map(|(ms, _)| ms).sum();
    println!("total {total:.2} ms over {} commands", timings.len());
    for (ms, text) in timings.iter().take(15) {
        println!("{ms:7.3} ms  {text}");
    }
}
