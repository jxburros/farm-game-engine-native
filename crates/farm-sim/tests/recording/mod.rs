//! The record and bless switches of the golden tests (`FARM_RECORD_GOLDENS`,
//! `FARM_PLAYER_BLESS`, `FARM_RENDER_BLESS`; docs/NUMERICS.md "Goldens").
//!
//! - A switch is on only when set to exactly `1`; `0` or an empty value leaves it off.
//! - Recording rewrites only the files whose contents change, then fails the test with a
//!   summary: a run that recorded never counts as a passing run, so a forgotten switch (or a
//!   blind re-bless in CI, which refuses the switches anyway) cannot turn a regression green.
//! - When `FARM_RECORD_GOLDENS` changed a file, it also stamps the engine commit it recorded on
//!   into `fixtures/golden/SOURCE.txt`.
//!
//! Shared through `#[path]` by the golden tests of farm-sim, farm-cart, farm-render and
//! farm-player, so it depends on nothing but std.
#![allow(dead_code)]

use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Mutex;

/// Whether the switch `variable` is on (set to exactly `1`).
pub fn enabled(variable: &str) -> bool {
    std::env::var(variable).is_ok_and(|value| value == "1")
}

pub fn repository_root() -> PathBuf {
    [env!("CARGO_MANIFEST_DIR"), "..", ".."].iter().collect()
}

/// One recording run: counts the files it looked at and the ones it rewrote.
#[derive(Debug)]
pub struct Recorder {
    variable: &'static str,
    what: &'static str,
    total: AtomicUsize,
    written: AtomicUsize,
}

impl Recorder {
    pub const fn new(variable: &'static str, what: &'static str) -> Self {
        Self { variable, what, total: AtomicUsize::new(0), written: AtomicUsize::new(0) }
    }

    pub fn enabled(&self) -> bool {
        enabled(self.variable)
    }

    /// Writes `bytes` to `path` unless the file already holds exactly them.
    pub fn write(&self, path: &Path, bytes: &[u8]) -> Result<(), String> {
        self.total.fetch_add(1, Ordering::SeqCst);
        if std::fs::read(path).is_ok_and(|old| old == bytes) {
            return Ok(());
        }
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent).map_err(|e| format!("create {}: {e}", parent.display()))?;
        }
        std::fs::write(path, bytes).map_err(|e| format!("write {}: {e}", path.display()))?;
        self.written.fetch_add(1, Ordering::SeqCst);
        eprintln!("{}: rewrote {}", self.variable, path.display());
        Ok(())
    }

    /// Notes a file the recording removed (a golden whose input is gone).
    pub fn removed(&self, path: &Path) {
        self.written.fetch_add(1, Ordering::SeqCst);
        eprintln!("{}: removed {}", self.variable, path.display());
    }

    /// The message a recording test fails with, after it wrote everything.
    pub fn summary(&self) -> String {
        format!(
            "{}=1 recorded {}: {} of {} files changed. A recording run always fails, so that it never \
             passes as a test run: review the diff (`git diff --stat -- fixtures`), then rerun without {}.",
            self.variable,
            self.what,
            self.written.load(Ordering::SeqCst),
            self.total.load(Ordering::SeqCst),
            self.variable
        )
    }

    /// Ends the recording: stamps `fixtures/golden/SOURCE.txt` when `FARM_RECORD_GOLDENS`
    /// changed a file, then fails with the summary.
    pub fn finish(&self) -> ! {
        if self.variable == "FARM_RECORD_GOLDENS" && self.written.load(Ordering::SeqCst) > 0 {
            stamp_source(self.what);
        }
        panic!("{}", self.summary());
    }
}

/// The marker line in `fixtures/golden/SOURCE.txt` that the stamps follow.
const STAMPS_HEADER: &str = "Last recorded (stamped by the recorders):";

/// SOURCE.txt is shared by the recorders of one test binary, which run in parallel.
static SOURCE_LOCK: Mutex<()> = Mutex::new(());

fn git(args: &[&str]) -> Option<String> {
    let output = Command::new("git").args(args).current_dir(repository_root()).output().ok()?;
    output.status.success().then(|| String::from_utf8_lossy(&output.stdout).trim().to_owned())
}

/// The engine commit a recording ran on, and whether the engine had uncommitted changes (the
/// fixtures themselves do not count: the recording just changed them).
fn engine_commit() -> String {
    let Some(commit) = git(&["rev-parse", "--short=12", "HEAD"]) else {
        return "unknown commit (no git)".to_owned();
    };
    match git(&["status", "--porcelain", "--untracked-files=no", "--", ".", ":(exclude)fixtures"]) {
        Some(changes) if changes.is_empty() => commit,
        Some(_) => format!("{commit} + uncommitted engine changes"),
        None => format!("{commit} (working tree unknown)"),
    }
}

/// Replaces (or adds) the `  <what>: <commit>` line under [`STAMPS_HEADER`].
fn stamp_source(what: &str) {
    let _guard = SOURCE_LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
    let path = repository_root().join("fixtures").join("golden").join("SOURCE.txt");
    let Ok(text) = std::fs::read_to_string(&path) else { return };
    let stamp = format!("  {what}: {}", engine_commit());
    let prefix = format!("  {what}: ");
    let mut lines: Vec<String> = text.lines().map(str::to_owned).collect();
    if let Some(line) = lines.iter_mut().find(|line| line.starts_with(&prefix)) {
        *line = stamp;
    } else if let Some(header) = lines.iter().position(|line| line == STAMPS_HEADER) {
        lines.insert(header + 1, stamp);
    } else {
        lines.extend([String::new(), STAMPS_HEADER.to_owned(), stamp]);
    }
    let _ = std::fs::write(&path, lines.join("\n") + "\n");
}
