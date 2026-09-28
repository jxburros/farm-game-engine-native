//! winit keys → farm-runtime key names (`KeyboardEvent.key.toLowerCase()` values).

use winit::keyboard::{Key, NamedKey};

/// The farm-runtime name of a key, or `None` for keys the game ignores.
pub fn key_name(key: &Key) -> Option<String> {
    match key {
        Key::Character(text) => {
            let lower = text.to_lowercase();
            (!lower.is_empty()).then_some(lower)
        }
        Key::Named(named) => named_key(*named).map(str::to_owned),
        _ => None,
    }
}

fn named_key(key: NamedKey) -> Option<&'static str> {
    Some(match key {
        NamedKey::ArrowUp => "arrowup",
        NamedKey::ArrowDown => "arrowdown",
        NamedKey::ArrowLeft => "arrowleft",
        NamedKey::ArrowRight => "arrowright",
        NamedKey::Space => " ",
        NamedKey::Enter => "enter",
        NamedKey::Escape => "escape",
        NamedKey::Tab => "tab",
        NamedKey::Backspace => "backspace",
        NamedKey::Delete => "delete",
        NamedKey::Insert => "insert",
        NamedKey::Home => "home",
        NamedKey::End => "end",
        NamedKey::PageUp => "pageup",
        NamedKey::PageDown => "pagedown",
        NamedKey::Shift => "shift",
        NamedKey::Control => "control",
        NamedKey::Alt => "alt",
        NamedKey::Super | NamedKey::Meta => "meta",
        NamedKey::F1 => "f1",
        NamedKey::F2 => "f2",
        NamedKey::F3 => "f3",
        NamedKey::F4 => "f4",
        NamedKey::F5 => "f5",
        NamedKey::F6 => "f6",
        NamedKey::F7 => "f7",
        NamedKey::F8 => "f8",
        NamedKey::F9 => "f9",
        NamedKey::F10 => "f10",
        NamedKey::F11 => "f11",
        NamedKey::F12 => "f12",
        _ => return None,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn maps_named_and_character_keys() {
        assert_eq!(key_name(&Key::Named(NamedKey::ArrowUp)).as_deref(), Some("arrowup"));
        assert_eq!(key_name(&Key::Named(NamedKey::Space)).as_deref(), Some(" "));
        assert_eq!(key_name(&Key::Named(NamedKey::F11)).as_deref(), Some("f11"));
        assert_eq!(key_name(&Key::Character("W".into())).as_deref(), Some("w"));
        assert_eq!(key_name(&Key::Character("1".into())).as_deref(), Some("1"));
        assert_eq!(key_name(&Key::Named(NamedKey::MediaPlay)), None);
    }
}
