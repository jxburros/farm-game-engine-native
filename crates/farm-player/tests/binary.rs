//! The `farm-player` executable: `--screenshot` renders without a window, and the windowed game
//! runs a few frames under a virtual X server (`xvfb-run`) and exits cleanly.

mod common;

use std::path::PathBuf;
use std::process::Command;

fn scratch(name: &str) -> PathBuf {
    let folder = PathBuf::from(env!("CARGO_TARGET_TMPDIR")).join("farm-player-binary");
    std::fs::create_dir_all(&folder).unwrap();
    folder.join(name)
}

fn cart() -> PathBuf {
    common::root().join("fixtures").join("golden").join("cartridges").join("project-v8.cart")
}

#[test]
fn screenshot_renders_frames_without_a_window() {
    let out = scratch("title.png");
    let script = scratch("script.json");
    // New Game on frame 2, then a few frames of play.
    std::fs::write(&script, r#"{"steps":[{"at":2,"press":"enter"}]}"#).unwrap();
    let output = Command::new(env!("CARGO_BIN_EXE_farm-player"))
        .args(["--screenshot", out.to_str().unwrap(), "--cart", cart().to_str().unwrap()])
        .args(["--size", "320x200", "--frames", "8", "--script", script.to_str().unwrap()])
        .output()
        .unwrap();
    assert!(output.status.success(), "{}", String::from_utf8_lossy(&output.stderr));
    let png = std::fs::read(&out).unwrap();
    let image = farm_render::images::decode_image(&png).unwrap();
    assert_eq!((image.width(), image.height()), (320, 200));

    let bad = Command::new(env!("CARGO_BIN_EXE_farm-player")).args(["--size", "12"]).output().unwrap();
    assert!(!bad.status.success());
    assert!(String::from_utf8_lossy(&bad.stderr).contains("--size"));
}

/// Runs the real window under Xvfb for a few frames. Skipped (with a note) where no virtual
/// display can be started, unless `FARM_REQUIRE_DISPLAY=1` (CI, which installs Xvfb) makes that a
/// failure: a missing display must not turn the test green there.
#[cfg(all(feature = "desktop", target_os = "linux"))]
#[test]
fn the_window_opens_renders_and_exits() {
    let required = std::env::var("FARM_REQUIRE_DISPLAY").is_ok_and(|value| value == "1");
    let available = Command::new("xvfb-run").arg("--help").output().is_ok_and(|output| output.status.success());
    if !available {
        assert!(!required, "FARM_REQUIRE_DISPLAY=1 but xvfb-run is not installed");
        eprintln!("skipped: xvfb-run is not installed");
        return;
    }
    let user = scratch("user");
    let output = Command::new("xvfb-run")
        .args(["-a", "-s", "-screen 0 1280x800x24", env!("CARGO_BIN_EXE_farm-player")])
        .args(["--cart", cart().to_str().unwrap(), "--exit-after-frames", "5", "--windowed"])
        .env("FARM_PLAYER_USER_DIR", &user)
        .output()
        .unwrap();
    let stderr = String::from_utf8_lossy(&output.stderr);
    if !output.status.success() && (stderr.contains("Cannot open a window") || stderr.contains("xvfb-run: error")) {
        assert!(!required, "FARM_REQUIRE_DISPLAY=1 but no display server could be started: {stderr}");
        eprintln!("skipped: no display server could be started: {stderr}");
        return;
    }
    assert!(output.status.success(), "{stderr}");
    assert!(!stderr.contains("Crash log"), "{stderr}");
}
