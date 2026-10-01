//! Embedded fonts and text measurement.
//!
//! The fonts are Inter 3.019 Regular and Bold (SIL Open Font License 1.1, see
//! `assets/fonts/OFL.txt`), the same files the desktop app loads from `Avalonia.Fonts.Inter`, so
//! in-world text matches the Skia host; and Atkinson Hyperlegible Regular and Bold (Braille
//! Institute, SIL Open Font License 1.1, see `assets/fonts/OFL-AtkinsonHyperlegible.txt`) for the
//! player's "Readable font" setting ([`FontId::readable`]). Measurement is what a UI layout needs: glyph advances,
//! line metrics, greedy word wrap and ellipsis truncation. Like SkiaSharp's `DrawText`, runs are
//! laid out from glyph advances without kerning or shaping.

use crate::draw::FontId;
use std::sync::OnceLock;
use ttf_parser::Face;
/// A glyph of an embedded font ([`Font::glyph`], [`Font::layout`]).
pub use ttf_parser::GlyphId;

const INTER_REGULAR: &[u8] = include_bytes!("../../../assets/fonts/Inter-Regular.ttf");
const INTER_BOLD: &[u8] = include_bytes!("../../../assets/fonts/Inter-Bold.ttf");
const ATKINSON_REGULAR: &[u8] = include_bytes!("../../../assets/fonts/AtkinsonHyperlegible-Regular.ttf");
const ATKINSON_BOLD: &[u8] = include_bytes!("../../../assets/fonts/AtkinsonHyperlegible-Bold.ttf");

/// The ellipsis appended by [`ellipsize`].
pub const ELLIPSIS: &str = "\u{2026}";

/// A parsed embedded font.
pub struct Font {
    id: FontId,
    face: Face<'static>,
    units_per_em: f32,
}

impl std::fmt::Debug for Font {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Font").field("id", &self.id).field("units_per_em", &self.units_per_em).finish_non_exhaustive()
    }
}

/// The embedded font for `id` (parsed on first use).
pub fn font(id: FontId) -> &'static Font {
    static REGULAR: OnceLock<Font> = OnceLock::new();
    static BOLD: OnceLock<Font> = OnceLock::new();
    static READABLE_REGULAR: OnceLock<Font> = OnceLock::new();
    static READABLE_BOLD: OnceLock<Font> = OnceLock::new();
    let (cell, bytes) = match id {
        FontId::Regular => (&REGULAR, INTER_REGULAR),
        FontId::Bold => (&BOLD, INTER_BOLD),
        FontId::ReadableRegular => (&READABLE_REGULAR, ATKINSON_REGULAR),
        FontId::ReadableBold => (&READABLE_BOLD, ATKINSON_BOLD),
    };
    cell.get_or_init(|| {
        let face = Face::parse(bytes, 0).expect("the embedded fonts are valid TrueType");
        let units_per_em = f32::from(face.units_per_em());
        Font { id, face, units_per_em }
    })
}

impl Font {
    pub fn id(&self) -> FontId {
        self.id
    }

    /// The parsed font, for renderers that outline glyphs themselves.
    pub fn face(&self) -> &Face<'static> {
        &self.face
    }

    pub fn units_per_em(&self) -> f32 {
        self.units_per_em
    }

    /// Pixels per font unit at `size`.
    pub fn scale(&self, size: f32) -> f32 {
        size / self.units_per_em
    }

    /// Distance from the baseline to the top of the line box (positive).
    pub fn ascent(&self, size: f32) -> f32 {
        f32::from(self.face.ascender()) * self.scale(size)
    }

    /// Distance from the baseline to the bottom of the line box (positive).
    pub fn descent(&self, size: f32) -> f32 {
        -f32::from(self.face.descender()) * self.scale(size)
    }

    pub fn line_gap(&self, size: f32) -> f32 {
        f32::from(self.face.line_gap()) * self.scale(size)
    }

    /// Baseline-to-baseline distance of consecutive lines.
    pub fn line_height(&self, size: f32) -> f32 {
        self.ascent(size) + self.descent(size) + self.line_gap(size)
    }

    /// The glyph for a character (`.notdef` when the font lacks it).
    pub fn glyph(&self, ch: char) -> GlyphId {
        self.face.glyph_index(ch).unwrap_or(GlyphId(0))
    }

    pub fn glyph_advance(&self, glyph: GlyphId, size: f32) -> f32 {
        f32::from(self.face.glyph_hor_advance(glyph).unwrap_or(0)) * self.scale(size)
    }

    /// Whether the font has a glyph for every character of `text` (whitespace and control
    /// characters aside).
    pub fn covers(&self, text: &str) -> bool {
        self.first_missing(text).is_none()
    }

    /// The first character of `text` the font has no glyph for (whitespace and control
    /// characters aside): it would draw as a box.
    pub fn first_missing(&self, text: &str) -> Option<char> {
        text.chars().find(|ch| !(ch.is_whitespace() || ch.is_control() || self.face.glyph_index(*ch).is_some()))
    }

    pub fn char_advance(&self, ch: char, size: f32) -> f32 {
        self.glyph_advance(self.glyph(ch), size)
    }

    /// Width of a single line of text: the sum of its glyph advances.
    pub fn measure(&self, text: &str, size: f32) -> f32 {
        text.chars().map(|ch| self.char_advance(ch, size)).sum()
    }

    /// Each glyph of a run with its pen position relative to the run's start.
    pub fn layout<'t>(&'t self, text: &'t str, size: f32) -> impl Iterator<Item = (GlyphId, f32)> + 't {
        let mut pen = 0.0;
        text.chars().map(move |ch| {
            let glyph = self.glyph(ch);
            let x = pen;
            pen += self.glyph_advance(glyph, size);
            (glyph, x)
        })
    }
}

/// Width of a single line of `text` in `font` at `size` pixels.
pub fn measure(font_id: FontId, size: f32, text: &str) -> f32 {
    font(font_id).measure(text, size)
}

/// Greedy word wrap: splits `text` into lines no wider than `max_width` where possible. Explicit
/// `\n` start new lines, runs of whitespace between words collapse to one space, and a word wider
/// than the whole line is broken between characters.
pub fn wrap(font_id: FontId, size: f32, text: &str, max_width: f32) -> Vec<String> {
    let font = font(font_id);
    let space = font.char_advance(' ', size);
    let mut lines = Vec::new();
    for paragraph in text.split('\n') {
        let mut line = String::new();
        let mut width = 0.0;
        for word in paragraph.split_whitespace() {
            let word_width = font.measure(word, size);
            if !line.is_empty() && width + space + word_width <= max_width {
                line.push(' ');
                line.push_str(word);
                width += space + word_width;
                continue;
            }
            if !line.is_empty() {
                lines.push(std::mem::take(&mut line));
            }
            if word_width <= max_width {
                line.push_str(word);
                width = word_width;
                continue;
            }
            // Break an over-long word into pieces that fit (at least one character each).
            width = 0.0;
            for ch in word.chars() {
                let advance = font.char_advance(ch, size);
                if !line.is_empty() && width + advance > max_width {
                    lines.push(std::mem::take(&mut line));
                    width = 0.0;
                }
                line.push(ch);
                width += advance;
            }
        }
        lines.push(line);
    }
    lines
}

/// `text` when it fits in `max_width`, else its longest prefix that fits with [`ELLIPSIS`]
/// appended (trailing spaces trimmed). Empty when not even the ellipsis fits.
pub fn ellipsize(font_id: FontId, size: f32, text: &str, max_width: f32) -> String {
    let font = font(font_id);
    if font.measure(text, size) <= max_width {
        return text.to_owned();
    }
    let budget = max_width - font.measure(ELLIPSIS, size);
    if budget < 0.0 {
        return String::new();
    }
    let mut width = 0.0;
    let mut end = 0;
    for (index, ch) in text.char_indices() {
        let advance = font.char_advance(ch, size);
        if width + advance > budget {
            break;
        }
        width += advance;
        end = index + ch.len_utf8();
    }
    let mut out = text[..end].trim_end().to_owned();
    out.push_str(ELLIPSIS);
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fonts_load_with_sane_metrics() {
        for id in [FontId::Regular, FontId::Bold] {
            let font = font(id);
            assert_eq!(font.id(), id);
            assert!(font.units_per_em() > 0.0);
            assert!(font.ascent(16.0) > 10.0 && font.ascent(16.0) < 20.0, "{}", font.ascent(16.0));
            assert!(font.descent(16.0) > 2.0 && font.descent(16.0) < 8.0);
            assert!(font.line_height(16.0) >= font.ascent(16.0) + font.descent(16.0));
            assert_ne!(font.glyph('A'), GlyphId(0));
            assert_ne!(font.glyph('\u{2026}'), GlyphId(0), "Inter has an ellipsis");
        }
        // Bold is wider than regular.
        assert!(measure(FontId::Bold, 20.0, "Harvest") > measure(FontId::Regular, 20.0, "Harvest"));
    }

    #[test]
    fn readable_fonts_load_and_cover_the_interface() {
        for (id, inter) in [(FontId::ReadableRegular, FontId::Regular), (FontId::ReadableBold, FontId::Bold)] {
            assert_eq!(inter.readable(), id);
            assert_eq!(id.readable(), id);
            let font = font(id);
            assert_eq!(font.id(), id);
            assert!(font.ascent(16.0) > 10.0 && font.ascent(16.0) < 20.0, "{}", font.ascent(16.0));
            assert!(font.descent(16.0) > 2.0 && font.descent(16.0) < 8.0);
            // Another face: its advances differ from Inter's.
            assert_ne!(measure(id, 20.0, "Harvest"), measure(inter, 20.0, "Harvest"));
            // English and Spanish interface text, money and the separators the UI uses.
            assert!(font.covers("Harvest $1,250 \u{00b7} \u{00d7}5 \u{2026} \u{2014} \u{2022}"));
            assert!(
                font.covers("\u{bf}Volver al t\u{ed}tulo? \u{a1}A\u{f1}o! Estaci\u{f3}n, M\u{fa}sica, cr\u{e9}ditos")
            );
        }
        // Coverage is per character: neither face has CJK.
        assert!(!font(FontId::ReadableRegular).covers("\u{7530}"));
        assert!(font(FontId::ReadableRegular).covers(" \n\t"));
        assert_eq!(font(FontId::Regular).first_missing("Farm \u{7530} \u{0628}"), Some('\u{7530}'));
        assert_eq!(font(FontId::Regular).first_missing("Ferme \u{0444}\u{0435}\u{0440}\u{043c}\u{0430}"), None);
    }

    #[test]
    fn measurement_is_additive_and_scales_with_size() {
        let a = measure(FontId::Regular, 10.0, "farm");
        let b = measure(FontId::Regular, 10.0, "ing");
        assert!((measure(FontId::Regular, 10.0, "farming") - (a + b)).abs() < 1e-4);
        assert!((measure(FontId::Regular, 20.0, "farm") - 2.0 * a).abs() < 1e-4);
        assert_eq!(measure(FontId::Regular, 10.0, ""), 0.0);
        let positions: Vec<f32> = font(FontId::Regular).layout("ab", 10.0).map(|(_, x)| x).collect();
        assert_eq!(positions[0], 0.0);
        assert!((positions[1] - measure(FontId::Regular, 10.0, "a")).abs() < 1e-4);
    }

    #[test]
    fn wraps_greedily_at_word_boundaries() {
        let size = 12.0;
        let width = measure(FontId::Regular, size, "the quick brown") + 0.01;
        let lines = wrap(FontId::Regular, size, "the quick brown fox jumps over", width);
        assert_eq!(lines[0], "the quick brown");
        assert!(lines.iter().all(|line| measure(FontId::Regular, size, line) <= width));
        assert_eq!(lines.join(" "), "the quick brown fox jumps over");
        // Explicit newlines and empty paragraphs are kept; whitespace runs collapse.
        assert_eq!(wrap(FontId::Regular, size, "a  b\n\nc", 1000.0), vec!["a b", "", "c"]);
        // Words longer than the line are broken between characters.
        let narrow = measure(FontId::Regular, size, "abc") + 0.01;
        let broken = wrap(FontId::Regular, size, "abcdefgh", narrow);
        assert!(broken.len() >= 3, "{broken:?}");
        assert_eq!(broken.concat(), "abcdefgh");
        assert!(broken.iter().all(|line| !line.is_empty() && measure(FontId::Regular, size, line) <= narrow));
    }

    #[test]
    fn ellipsizes_to_the_available_width() {
        let size = 14.0;
        assert_eq!(ellipsize(FontId::Bold, size, "Wheat", 1000.0), "Wheat");
        let width = measure(FontId::Bold, size, "Golden Wh") + measure(FontId::Bold, size, ELLIPSIS) + 0.01;
        let cut = ellipsize(FontId::Bold, size, "Golden Wheat Seeds", width);
        assert_eq!(cut, "Golden Wh\u{2026}");
        assert!(measure(FontId::Bold, size, &cut) <= width + 1e-3);
        // Trailing spaces before the ellipsis are trimmed.
        let width = measure(FontId::Bold, size, "Golden ") + measure(FontId::Bold, size, ELLIPSIS) + 0.01;
        assert_eq!(ellipsize(FontId::Bold, size, "Golden Wheat", width), "Golden\u{2026}");
        assert_eq!(ellipsize(FontId::Bold, size, "Golden Wheat", 1.0), "");
    }
}
