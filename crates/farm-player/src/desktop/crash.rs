//! Crash logs (docs/EXPORT.md "Crash reports"): a panic hook writes `crash-<time>-<pid>.log` next
//! to the saves with the game version, the cartridge hash and the recent command log, and prints
//! its path. The simulation is deterministic, so the last save plus the command log replays a
//! crash.
//!
//! - Logs are created with `create_new` (never through an existing file or symlink) under a name
//!   that includes the process id, so two runs that crash in the same second keep both logs.
//! - The log stays open for the rest of the run; later details are appended to that file.
//! - The newest [`KEEP_LOGS`] logs of the folder are kept; older ones are removed.
//! - The report in a log is the one published last (every second or so). A panic outside
//!   [`crate::Player::frame`] (the audio thread, the window system) says how many frames old it
//!   is.

use std::backtrace::Backtrace;
use std::fs::{self, File};
use std::io::{ErrorKind, Write as _};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

/// Crash logs kept per folder.
pub const KEEP_LOGS: usize = 10;

#[derive(Debug, Default)]
struct Context {
    folder: Option<PathBuf>,
    /// The player's latest crash report (published every so often).
    report: String,
    /// The frame the report was published at.
    report_frame: u64,
    /// The log written for this run, if any, kept open.
    log: Option<(PathBuf, File)>,
}

static CONTEXT: Mutex<Context> =
    Mutex::new(Context { folder: None, report: String::new(), report_frame: 0, log: None });

/// Frames the game has run (updated every frame, without locking).
static FRAME: AtomicU64 = AtomicU64::new(0);

fn now() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |time| time.as_secs())
}

/// Installs the panic hook; crash logs go to `folder` (the saves folder), else the temporary
/// folder.
pub fn install(folder: Option<PathBuf>) {
    if let Ok(mut context) = CONTEXT.lock() {
        context.folder = folder;
    }
    let default = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |info| {
        default(info);
        let thread = std::thread::current();
        let detail = format!(
            "Panic on thread {}: {info}\n\nBacktrace:\n{}\n",
            thread.name().unwrap_or("unnamed"),
            Backtrace::force_capture()
        );
        if let Some(path) = write(&detail) {
            eprintln!("Crash log: {}", path.display());
        }
    }));
}

/// Publishes what a crash log should say about the game right now.
pub fn publish(report: String) {
    let frame = FRAME.load(Ordering::Relaxed);
    if let Ok(mut context) = CONTEXT.lock() {
        context.report = report;
        context.report_frame = frame;
    }
}

/// Records the frame the game is on (cheap; call it every frame).
pub fn set_frame(frame: u64) {
    FRAME.store(frame, Ordering::Relaxed);
}

/// A new log file in `folder` that did not exist before: `crash-<time>-<pid>.log`, with a counter
/// when that name is taken.
fn create_log(folder: &Path, time: u64) -> Option<(PathBuf, File)> {
    fs::create_dir_all(folder).ok()?;
    let pid = std::process::id();
    for attempt in 0..100u32 {
        let name = match attempt {
            0 => format!("crash-{time}-{pid}.log"),
            _ => format!("crash-{time}-{pid}-{attempt}.log"),
        };
        let path = folder.join(name);
        // `create_new` fails on any existing entry, symlinks included, so a log is never written
        // through a link someone else planted.
        match fs::OpenOptions::new().write(true).create_new(true).open(&path) {
            Ok(file) => return Some((path, file)),
            Err(error) if error.kind() == ErrorKind::AlreadyExists => continue,
            Err(_) => return None,
        }
    }
    None
}

/// Removes all but the newest `keep` crash logs of `folder` (by modification time, then name).
pub fn prune(folder: &Path, keep: usize) {
    let Ok(entries) = fs::read_dir(folder) else { return };
    let mut logs: Vec<(SystemTime, PathBuf)> = entries
        .flatten()
        .filter(|entry| {
            let name = entry.file_name();
            let name = name.to_string_lossy();
            name.starts_with("crash-") && name.ends_with(".log")
        })
        .filter(|entry| entry.file_type().is_ok_and(|kind| kind.is_file()))
        .map(|entry| (entry.metadata().and_then(|meta| meta.modified()).unwrap_or(UNIX_EPOCH), entry.path()))
        .collect();
    if logs.len() <= keep {
        return;
    }
    logs.sort();
    for (_, path) in &logs[..logs.len() - keep] {
        let _ = fs::remove_file(path);
    }
}

/// Appends `detail` to this run's crash log, creating it with the published report first.
/// Returns the log's path.
pub fn write(detail: &str) -> Option<PathBuf> {
    let mut context = CONTEXT.try_lock().ok()?;
    if context.log.is_none() {
        let folder = context.folder.clone().unwrap_or_else(std::env::temp_dir);
        let time = now();
        let (path, mut file) = create_log(&folder, time)?;
        let frame = FRAME.load(Ordering::Relaxed);
        let age = frame.saturating_sub(context.report_frame);
        let header = format!(
            "Farming RPG Maker player crash\nPlayer version: {}\nTime: {time}\nFrame: {frame} (report below from frame {}, {age} frames earlier)\n\n{}\n",
            env!("CARGO_PKG_VERSION"),
            context.report_frame,
            context.report
        );
        file.write_all(header.as_bytes()).ok()?;
        prune(&folder, KEEP_LOGS);
        context.log = Some((path, file));
    }
    let (path, file) = context.log.as_mut()?;
    file.write_all(detail.as_bytes()).ok()?;
    let _ = file.flush();
    Some(path.clone())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn folder(name: &str) -> PathBuf {
        let folder = std::env::temp_dir().join(format!("farm-crash-{name}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&folder);
        fs::create_dir_all(&folder).unwrap();
        folder
    }

    #[test]
    fn logs_in_the_same_second_get_their_own_files() {
        let folder = folder("same-second");
        let (first, _) = create_log(&folder, 1000).unwrap();
        let (second, _) = create_log(&folder, 1000).unwrap();
        assert_ne!(first, second);
        let pid = std::process::id();
        assert!(first.ends_with(format!("crash-1000-{pid}.log")), "{first:?}");
        assert!(second.ends_with(format!("crash-1000-{pid}-1.log")), "{second:?}");
        fs::remove_dir_all(folder).unwrap();
    }

    #[cfg(unix)]
    #[test]
    fn a_planted_symlink_is_never_written_through() {
        let folder = folder("symlink");
        let target = folder.join("victim.txt");
        fs::write(&target, b"precious").unwrap();
        let pid = std::process::id();
        std::os::unix::fs::symlink(&target, folder.join(format!("crash-2000-{pid}.log"))).unwrap();
        let (path, mut file) = create_log(&folder, 2000).unwrap();
        file.write_all(b"log").unwrap();
        assert!(path.ends_with(format!("crash-2000-{pid}-1.log")));
        assert_eq!(fs::read(&target).unwrap(), b"precious");
        fs::remove_dir_all(folder).unwrap();
    }

    #[test]
    fn only_the_newest_logs_are_kept() {
        let folder = folder("prune");
        for index in 0..KEEP_LOGS + 3 {
            let path = folder.join(format!("crash-{index:04}-1.log"));
            fs::write(&path, b"x").unwrap();
            let time = UNIX_EPOCH + std::time::Duration::from_secs(1_000_000 + index as u64);
            File::options().write(true).open(&path).unwrap().set_modified(time).unwrap();
        }
        fs::write(folder.join("slot1.sav"), b"save").unwrap();
        prune(&folder, KEEP_LOGS);
        let mut names: Vec<String> =
            fs::read_dir(&folder).unwrap().map(|entry| entry.unwrap().file_name().to_string_lossy().into()).collect();
        names.sort();
        assert_eq!(names.len(), KEEP_LOGS + 1);
        assert_eq!(names[0], "crash-0003-1.log", "the three oldest went");
        assert!(names.contains(&"slot1.sav".to_owned()));
        fs::remove_dir_all(folder).unwrap();
    }
}
