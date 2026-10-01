//! User folders (docs/EXPORT.md "Proper user folders"): saves in `<data dir>/<gameId>/saves/`,
//! settings in `<config dir>/<gameId>/settings.toml`. On Windows the company, when set, comes
//! first (`%APPDATA%\<company>\<gameId>\`). `FARM_PLAYER_USER_DIR` puts both under one folder
//! (tests, portable installs).

use directories::BaseDirs;
use std::path::PathBuf;

/// Where a game keeps its files.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct UserFolders {
    pub saves: PathBuf,
    pub settings: PathBuf,
}

/// Longest folder name, in characters (well inside every file system's 255-unit limit, and
/// short enough that `<data dir>\<company>\<gameId>\saves\slot1.sav` stays under `MAX_PATH`).
const MAX_FOLDER_CHARS: usize = 80;

/// Device names Windows reserves in every folder, with or without an extension (`CON`,
/// `nul.txt`, `COM1`…).
fn is_reserved_on_windows(name: &str) -> bool {
    let stem = name.split('.').next().unwrap_or(name).trim_end().to_ascii_uppercase();
    if matches!(stem.as_str(), "CON" | "PRN" | "AUX" | "NUL" | "CONIN$" | "CONOUT$") {
        return true;
    }
    // COM1…COM9 and LPT1…LPT9, also with the superscript digits Windows treats the same.
    let mut chars = stem.chars();
    let prefix: String = chars.by_ref().take(3).collect();
    let digit: String = chars.collect();
    matches!(prefix.as_str(), "COM" | "LPT")
        && matches!(digit.as_str(), "1" | "2" | "3" | "4" | "5" | "6" | "7" | "8" | "9" | "¹" | "²" | "³")
}

/// A folder name from a game id or company: path separators and reserved characters removed,
/// Windows device names (`CON`, `NUL`, `COM1`…) suffixed, and at most 80 characters. The same
/// rules apply on every platform, so a game keeps its folder name everywhere.
pub fn folder_name(text: &str) -> String {
    let cleaned: String =
        text.chars()
            .map(|c| {
                if c.is_control() || matches!(c, '/' | '\\' | ':' | '*' | '?' | '"' | '<' | '>' | '|') {
                    '_'
                } else {
                    c
                }
            })
            .take(MAX_FOLDER_CHARS)
            .collect();
    // Windows drops trailing dots and spaces, so names never end with them.
    let trimmed = cleaned.trim().trim_matches('.').trim().to_owned();
    if trimmed.is_empty() {
        "farm-game".to_owned()
    } else if is_reserved_on_windows(&trimmed) {
        // The suffix goes on the part before the first dot: `con.game` is as reserved as `con`.
        let end = trimmed.find('.').unwrap_or(trimmed.len());
        format!("{}_{}", &trimmed[..end], &trimmed[end..])
    } else {
        trimmed
    }
}

impl UserFolders {
    pub fn for_game(game_id: &str, company: Option<&str>) -> Option<Self> {
        if let Some(root) = std::env::var_os("FARM_PLAYER_USER_DIR") {
            let root = PathBuf::from(root).join(folder_name(game_id));
            return Some(Self { saves: root.join("saves"), settings: root.join("settings.toml") });
        }
        let base = BaseDirs::new()?;
        let game = folder_name(game_id);
        let scoped = |root: PathBuf| match company.filter(|company| cfg!(windows) && !company.trim().is_empty()) {
            Some(company) => root.join(folder_name(company)).join(&game),
            None => root.join(&game),
        };
        Some(Self {
            saves: scoped(base.data_dir().to_path_buf()).join("saves"),
            settings: scoped(base.config_dir().to_path_buf()).join("settings.toml"),
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn folder_names_are_safe() {
        assert_eq!(folder_name("local.project-1"), "local.project-1");
        assert_eq!(folder_name("a/b\\c:d"), "a_b_c_d");
        assert_eq!(folder_name(" .. "), "farm-game");
    }

    #[test]
    fn windows_device_names_get_a_suffix() {
        assert_eq!(folder_name("Con"), "Con_");
        assert_eq!(folder_name("nul"), "nul_");
        assert_eq!(folder_name("COM1"), "COM1_");
        assert_eq!(folder_name("lpt9.game"), "lpt9_.game");
        assert_eq!(folder_name("aux "), "aux_");
        assert_eq!(folder_name("COM0"), "COM0");
        assert_eq!(folder_name("Console"), "Console");
        assert_eq!(folder_name("local.con"), "local.con");
    }

    #[test]
    fn long_names_are_capped() {
        let long = "a".repeat(300);
        assert_eq!(folder_name(&long).chars().count(), MAX_FOLDER_CHARS);
        // Cut on characters, never inside one.
        let wide = "é".repeat(300);
        assert_eq!(folder_name(&wide), "é".repeat(MAX_FOLDER_CHARS));
        // A cut that ends on a dot or space is trimmed like any other name.
        let dotted = format!("{}.x", "b".repeat(MAX_FOLDER_CHARS - 1));
        assert_eq!(folder_name(&dotted), "b".repeat(MAX_FOLDER_CHARS - 1));
    }
}
