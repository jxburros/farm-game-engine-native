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

/// A folder name from a game id or company: path separators and reserved characters removed.
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
            .collect();
    let trimmed = cleaned.trim().trim_matches('.').to_owned();
    if trimmed.is_empty() {
        "farm-game".to_owned()
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
}
