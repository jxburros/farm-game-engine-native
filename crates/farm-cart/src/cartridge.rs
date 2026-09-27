//! Verified FlatBuffers cartridge reader. Format 1 carries compiled content as compatibility
//! JSON so the Rust simulation keeps JavaScript numeric parity while indexed tables are added.

use farm_cart_schema::farm_engine::cart::{cartridge_buffer_has_identifier, root_as_cartridge};

pub const CART_IDENTIFIER: &[u8; 4] = b"FGCT";

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GameInfo<'a> {
    pub title: &'a str,
    pub version: &'a str,
    pub game_id: &'a str,
    pub executable_name: Option<&'a str>,
    pub window_width: u32,
    pub window_height: u32,
    pub fullscreen: bool,
    pub pixel_scale: Option<&'a str>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Cartridge<'a> {
    pub info: GameInfo<'a>,
    pub project_schema_version: u32,
    pub project_json: &'a [u8],
    pub content_json: &'a [u8],
}

pub fn is_cartridge(bytes: &[u8]) -> bool {
    bytes.len() >= 8 && cartridge_buffer_has_identifier(bytes)
}

/// Verify the FlatBuffer before exposing its borrowed fields. A newer cartridge is refused
/// rather than interpreted as the older layout.
pub fn read_cartridge(bytes: &[u8]) -> Result<Cartridge<'_>, String> {
    if !is_cartridge(bytes) {
        return Err("Not a Farm Engine cartridge (FGCT identifier missing).".to_owned());
    }
    let cart = root_as_cartridge(bytes).map_err(|error| format!("Invalid cartridge: {error}"))?;
    if cart.cart_format() != crate::CART_FORMAT {
        return Err(format!(
            "Cartridge format {} is not supported by this player (requires {}).",
            cart.cart_format(),
            crate::CART_FORMAT
        ));
    }
    if cart.project_schema_version() as f64 > farm_sim::schema::CURRENT_PROJECT_SCHEMA_VERSION {
        return Err(format!("Project schema {} is newer than this player supports.", cart.project_schema_version()));
    }
    let info = cart.info();
    if info.game_id().is_empty()
        || info.version().is_empty()
        || cart.project_json().is_empty()
        || cart.content_json().is_empty()
    {
        return Err("Cartridge is missing its game identity or content.".to_owned());
    }
    Ok(Cartridge {
        info: GameInfo {
            title: info.title(),
            version: info.version(),
            game_id: info.game_id(),
            executable_name: info.executable_name(),
            window_width: info.window_width(),
            window_height: info.window_height(),
            fullscreen: info.fullscreen(),
            pixel_scale: info.pixel_scale(),
        },
        project_schema_version: cart.project_schema_version(),
        project_json: cart.project_json().bytes(),
        content_json: cart.content_json().bytes(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use farm_sim::schema::{GameContent, GameProject};

    #[test]
    fn fsharp_compiled_project_matches_rust_content() {
        let bytes = include_bytes!("../../../fixtures/golden/cartridges/project-v8.cart");
        let cart = read_cartridge(bytes).expect("F# cartridge parses in Rust");
        assert_eq!(cart.project_schema_version, 8);
        assert_eq!(cart.info.game_id, "local.project-1");
        assert_eq!(cart.info.title, "Current V7 Farm");
        let project: GameProject = serde_json::from_slice(cart.project_json).unwrap();
        let content: GameContent = serde_json::from_slice(cart.content_json).unwrap();
        assert_eq!(content, farm_sim::state::create_content_from_project(&project));
    }

    #[test]
    fn malformed_cartridges_are_rejected() {
        assert!(read_cartridge(&[]).is_err());
        assert!(read_cartridge(b"1234FGCT").is_err());
        let mut truncated = include_bytes!("../../../fixtures/golden/cartridges/project-v8.cart").to_vec();
        truncated.truncate(20);
        assert!(read_cartridge(&truncated).is_err());
    }
}
