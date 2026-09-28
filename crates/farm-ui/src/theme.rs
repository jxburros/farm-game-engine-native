//! Colors and metrics of the in-game UI.
//!
//! [`Theme::cozy`] is the default: warm dark panels with gold accents (the exported web shell's
//! palette), laid out like the editor's Play Mode (`GameStyles.axaml`). [`Theme::light`] is the
//! editor's cream palette, for hosts that want the game to match the editor chrome. Screens only
//! read colors and sizes from here.

use farm_render::Color;

/// Every color the UI draws with.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Palette {
    /// Dims the world behind a modal.
    pub backdrop: Color,
    /// Title and shell backgrounds without a world behind them.
    pub background: Color,
    /// Modal cards, the dialogue box.
    pub panel: Color,
    pub panel_border: Color,
    /// Modal header and footer strips.
    pub panel_header: Color,
    /// The HUD bar and hint row over the world.
    pub hud: Color,
    pub hud_border: Color,
    /// List rows inside panels.
    pub row: Color,
    pub row_border: Color,
    pub text: Color,
    pub muted: Color,
    pub faint: Color,
    /// Gold: money chip, primary buttons, focus ring.
    pub accent: Color,
    /// Text on an accent background.
    pub on_accent: Color,
    /// Gold text (titles, the season value).
    pub accent_text: Color,
    /// Green: energy, success, completed objectives.
    pub primary: Color,
    /// Orange: the day value, the dialogue portrait.
    pub secondary: Color,
    pub error: Color,
    pub success_tint: Color,
    pub error_tint: Color,
    pub button: Color,
    pub button_hover: Color,
    pub button_pressed: Color,
    pub button_border: Color,
    pub focus: Color,
    pub track: Color,
    pub shadow: Color,
}

/// Font sizes in logical pixels at text size 100 %.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct FontSizes {
    pub body: f32,
    pub small: f32,
    pub tiny: f32,
    pub h3: f32,
    pub h2: f32,
    pub h1: f32,
    pub title: f32,
    pub dialogue: f32,
}

/// A complete UI theme.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Theme {
    pub colors: Palette,
    pub fonts: FontSizes,
    /// Corner radius of cards.
    pub radius: f32,
    /// Corner radius of buttons and rows.
    pub small_radius: f32,
}

impl Default for Theme {
    fn default() -> Self {
        Self::cozy()
    }
}

const fn hex(rgb: u32) -> Color {
    Color::rgb((rgb >> 16) as u8, (rgb >> 8) as u8, rgb as u8)
}

const fn hexa(rgb: u32, alpha: u8) -> Color {
    Color::rgba((rgb >> 16) as u8, (rgb >> 8) as u8, rgb as u8, alpha)
}

const FONTS: FontSizes =
    FontSizes { body: 14.0, small: 12.5, tiny: 11.5, h3: 14.0, h2: 16.5, h1: 22.0, title: 44.0, dialogue: 15.0 };

impl Theme {
    /// Warm dark panels, gold accents (the default).
    pub const fn cozy() -> Self {
        Self {
            colors: Palette {
                backdrop: hexa(0x0a0c09, 0xa8),
                background: hex(0x171a15),
                panel: hex(0x23271f),
                panel_border: hex(0x4a523f),
                panel_header: hex(0x1d211a),
                hud: hexa(0x171a15, 0xe0),
                hud_border: hexa(0x4a523f, 0xd0),
                row: hex(0x2b3026),
                row_border: hex(0x3a4033),
                text: hex(0xe8e6da),
                muted: hex(0xa3a996),
                faint: hex(0x7c8371),
                accent: hex(0xf9c718),
                on_accent: hex(0x272833),
                accent_text: hex(0xffd97a),
                primary: hex(0x8fd06c),
                secondary: hex(0xf0a45a),
                error: hex(0xef8a78),
                success_tint: hex(0x263a22),
                error_tint: hex(0x44261f),
                button: hex(0x333a2d),
                button_hover: hex(0x414a39),
                button_pressed: hex(0x2a3025),
                button_border: hex(0x55604a),
                focus: hex(0xffd97a),
                track: hex(0x3a4033),
                shadow: hexa(0x000000, 0x60),
            },
            fonts: FONTS,
            radius: 12.0,
            small_radius: 7.0,
        }
    }

    /// The editor's cream palette (`App.axaml` Farm* brushes).
    pub const fn light() -> Self {
        Self {
            colors: Palette {
                backdrop: hexa(0xf8f5ee, 0x99),
                background: hex(0xf8f5ee),
                panel: hex(0xfffdf8),
                panel_border: hex(0xd4cdbf),
                panel_header: hex(0xf3eee4),
                hud: hexa(0xf0ebe0, 0xf0),
                hud_border: hex(0xd4cdbf),
                row: hexa(0xf0ebe0, 0xc0),
                row_border: hexa(0xd4cdbf, 0xc0),
                text: hex(0x272833),
                muted: hex(0x5f6275),
                faint: hex(0x8a8c99),
                accent: hex(0xf9c718),
                on_accent: hex(0x272833),
                accent_text: hex(0x8a6a1f),
                primary: hex(0x095c34),
                secondary: hex(0xb45f12),
                error: hex(0xc62828),
                success_tint: hex(0xe3f1e7),
                error_tint: hex(0xfbe3e1),
                button: hex(0xfffdf8),
                button_hover: hex(0xfcefb8),
                button_pressed: hex(0xf0ebe0),
                button_border: hex(0xd4cdbf),
                focus: hex(0xe1791b),
                track: hex(0xe4ddcf),
                shadow: hexa(0x000000, 0x33),
            },
            fonts: FONTS,
            radius: 12.0,
            small_radius: 7.0,
        }
    }
}

/// `color` with its alpha multiplied by `opacity` (0..1).
pub fn fade(color: Color, opacity: f32) -> Color {
    let alpha = (f32::from(color.a) * opacity.clamp(0.0, 1.0) + 0.5).floor();
    Color::rgba(color.r, color.g, color.b, alpha as u8)
}

/// Linear blend of two colors (`t` = 0 → `a`, 1 → `b`).
pub fn mix(a: Color, b: Color, t: f32) -> Color {
    let t = t.clamp(0.0, 1.0);
    let lerp = |x: u8, y: u8| (f32::from(x) + (f32::from(y) - f32::from(x)) * t + 0.5).floor() as u8;
    Color::rgba(lerp(a.r, b.r), lerp(a.g, b.g), lerp(a.b, b.b), lerp(a.a, b.a))
}

#[cfg(test)]
mod tests {
    use super::*;

    /// WCAG relative luminance contrast of two opaque colors.
    fn contrast(a: Color, b: Color) -> f64 {
        fn channel(v: u8) -> f64 {
            let c = f64::from(v) / 255.0;
            if c <= 0.03928 {
                c / 12.92
            } else {
                ((c + 0.055) / 1.055).powf(2.4)
            }
        }
        let lum = |c: Color| 0.2126 * channel(c.r) + 0.7152 * channel(c.g) + 0.0722 * channel(c.b);
        let (x, y) = (lum(a), lum(b));
        (x.max(y) + 0.05) / (x.min(y) + 0.05)
    }

    #[test]
    fn text_is_readable_on_every_surface() {
        for theme in [Theme::cozy(), Theme::light()] {
            let c = theme.colors;
            for surface in [c.panel, c.row, c.button, c.background] {
                assert!(contrast(c.text, surface) >= 7.0, "{:?} on {:?}", c.text, surface);
                assert!(contrast(c.muted, surface) >= 3.5, "{:?} on {:?}", c.muted, surface);
            }
            assert!(contrast(c.on_accent, c.accent) >= 7.0);
        }
    }

    #[test]
    fn fade_and_mix() {
        assert_eq!(fade(Color::rgb(1, 2, 3), 0.5).a, 128);
        assert_eq!(mix(Color::rgb(0, 0, 0), Color::rgb(255, 255, 255), 0.5), Color::rgb(128, 128, 128));
    }
}
