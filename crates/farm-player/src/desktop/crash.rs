//! Crash logs (docs/EXPORT.md "Crash reports"): a panic hook writes `crash-<time>.log` next to
//! the saves with the game version, the cartridge hash and the recent command log, and prints its
//! path. The simulation is deterministic, so the last save plus the command log replays a crash.

use std::backtrace::Backtrace;
use std::fs;
use std::io::Write as _;
use std::path::PathBuf;
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

#[derive(Debug, Default)]
struct Context {
    folder: Option<PathBuf>,
    /// The player's latest crash report (published every so often).
    report: String,
    /// The log written for this run, if any.
    log: Option<PathBuf>,
}

static CONTEXT: Mutex<Context> = Mutex::new(Context { folder: None, report: String::new(), log: None });

fn now() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |time| time.as_secs())
}

/// Installs the panic hook; crash logs go to `folder` (the saves folder).
pub fn install(folder: Option<PathBuf>) {
    if let Ok(mut context) = CONTEXT.lock() {
        context.folder = folder;
    }
    let default = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |info| {
        default(info);
        let detail = format!("Panic: {info}\n\nBacktrace:\n{}\n", Backtrace::force_capture());
        if let Some(path) = write(&detail) {
            eprintln!("Crash log: {}", path.display());
        }
    }));
}

/// Publishes what a crash log should say about the game right now.
pub fn publish(report: String) {
    if let Ok(mut context) = CONTEXT.lock() {
        context.report = report;
    }
}

/// Appends `detail` to this run's crash log, creating it with the published report first.
/// Returns the log's path.
pub fn write(detail: &str) -> Option<PathBuf> {
    let mut context = CONTEXT.try_lock().ok()?;
    let path = match &context.log {
        Some(path) => path.clone(),
        None => {
            let folder = context.folder.clone().unwrap_or_else(std::env::temp_dir);
            fs::create_dir_all(&folder).ok()?;
            let path = folder.join(format!("crash-{}.log", now()));
            let header = format!(
                "Farming RPG Maker player crash\nPlayer version: {}\nTime: {}\n\n{}\n",
                env!("CARGO_PKG_VERSION"),
                now(),
                context.report
            );
            fs::write(&path, header).ok()?;
            context.log = Some(path.clone());
            path
        }
    };
    let mut file = fs::OpenOptions::new().append(true).open(&path).ok()?;
    file.write_all(detail.as_bytes()).ok()?;
    Some(path)
}
