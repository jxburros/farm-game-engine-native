//! Draw lists: *what* to draw, as an ordered list of commands in logical pixels, independent of
//! how it is rasterized (the CPU [`crate::raster`] backend here; a GPU or Skia executor later).
//!
//! Commands follow the HTML canvas / Skia model: a current transform and clip that
//! [`DrawCmd::Save`] / [`DrawCmd::Restore`] push and pop, painter's order, source-over blending
//! (except [`DrawCmd::ModulateRect`]). [`crate::build_world`] wraps the world in one save/restore
//! pair, so a UI layer can append its own screen-space commands (panels, buttons, text) after it.
//! Transforms are expected to be axis-aligned (translate and scale).
//!
//! Draw lists serialize to JSON (`{"op":"fillRect",…}`) for debugging, tests and other executors.
//! Images are referred to by [`ImageId`] of the [`crate::ImageStore`] that built the list.

use crate::css_color::Color;
use crate::images::ImageId;
use serde::{Deserialize, Serialize};

/// An axis-aligned rectangle in logical pixels (`SKRect.Create(x, y, width, height)`).
#[derive(Debug, Clone, Copy, PartialEq, Default, Serialize, Deserialize)]
pub struct Rect {
    pub x: f32,
    pub y: f32,
    pub width: f32,
    pub height: f32,
}

impl Rect {
    pub const fn new(x: f32, y: f32, width: f32, height: f32) -> Self {
        Self { x, y, width, height }
    }

    pub fn right(&self) -> f32 {
        self.x + self.width
    }

    pub fn bottom(&self) -> f32 {
        self.y + self.height
    }
}

/// How an image is sampled when scaled.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum Sampling {
    /// Nearest neighbour: crisp pixel art (canvas `imageSmoothingEnabled = false`).
    #[default]
    Nearest,
    /// Bilinear filtering.
    Smooth,
}

/// One of the embedded fonts (see [`crate::text`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum FontId {
    /// Inter Regular.
    #[default]
    Regular,
    /// Inter Bold (in-world pops, like the C# host).
    Bold,
}

/// Horizontal anchor of a text run relative to its `x`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum TextAlign {
    #[default]
    Left,
    Center,
    Right,
}

/// An outline drawn under the text fill (round joins).
#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
pub struct TextStroke {
    pub color: Color,
    pub width: f32,
}

/// One drawing operation. Shapes other than [`DrawCmd::FillRect`] are anti-aliased; strokes use
/// butt caps and miter joins like Skia's defaults.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "op", rename_all = "camelCase", rename_all_fields = "camelCase")]
pub enum DrawCmd {
    /// Pushes the current transform and clip.
    Save,
    /// Pops the transform and clip pushed by the matching [`DrawCmd::Save`].
    Restore,
    /// Moves the origin (pre-concatenated, like `canvas.translate`).
    Translate {
        dx: f32,
        dy: f32,
    },
    /// Scales the coordinate system (pre-concatenated).
    Scale {
        sx: f32,
        sy: f32,
    },
    /// Intersects the clip with a rectangle (hard edges, rounded to whole device pixels).
    ClipRect {
        rect: Rect,
    },
    /// Fills a rectangle; `anti_alias: false` covers exactly the pixels whose centers are inside.
    FillRect {
        rect: Rect,
        color: Color,
        anti_alias: bool,
    },
    /// Outlines a rectangle (centered on its edges).
    StrokeRect {
        rect: Rect,
        color: Color,
        width: f32,
    },
    FillRoundRect {
        rect: Rect,
        radius: f32,
        color: Color,
    },
    StrokeRoundRect {
        rect: Rect,
        radius: f32,
        color: Color,
        width: f32,
    },
    FillCircle {
        cx: f32,
        cy: f32,
        radius: f32,
        color: Color,
    },
    StrokeCircle {
        cx: f32,
        cy: f32,
        radius: f32,
        color: Color,
        width: f32,
    },
    /// Fills the ellipse inscribed in `rect`.
    FillOval {
        rect: Rect,
        color: Color,
    },
    /// A Gaussian-blurred ellipse (soft shadows). `sigma` is in logical pixels.
    BlurOval {
        rect: Rect,
        color: Color,
        sigma: f32,
    },
    /// A Gaussian-blurred rectangle (glows). `sigma` is in logical pixels.
    BlurRect {
        rect: Rect,
        color: Color,
        sigma: f32,
    },
    Line {
        x0: f32,
        y0: f32,
        x1: f32,
        y1: f32,
        color: Color,
        width: f32,
    },
    /// Draws the `src` region of an image into `dst`. `tint` multiplies every channel (seasonal
    /// foliage); `opacity` multiplies alpha.
    Image {
        image: ImageId,
        src: Rect,
        dst: Rect,
        opacity: f32,
        sampling: Sampling,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        tint: Option<Color>,
    },
    /// Multiplies what is already drawn by `color` (`BlendMode.Modulate`; day/night tint).
    ModulateRect {
        rect: Rect,
        color: Color,
    },
    /// A single line of text with its baseline at `y`, anchored at `x` by `align`. The stroke,
    /// when present, is drawn first.
    Text {
        text: String,
        x: f32,
        y: f32,
        font: FontId,
        size: f32,
        color: Color,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        stroke: Option<TextStroke>,
        align: TextAlign,
    },
}

/// An ordered list of [`DrawCmd`]s. Push commands directly or through the helpers.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
pub struct DrawList {
    pub commands: Vec<DrawCmd>,
}

impl DrawList {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn len(&self) -> usize {
        self.commands.len()
    }

    pub fn is_empty(&self) -> bool {
        self.commands.is_empty()
    }

    pub fn push(&mut self, command: DrawCmd) {
        self.commands.push(command);
    }

    /// Appends every command of `other` (for example a UI layer after the world).
    pub fn append(&mut self, other: &mut DrawList) {
        self.commands.append(&mut other.commands);
    }

    pub fn save(&mut self) {
        self.push(DrawCmd::Save);
    }

    pub fn restore(&mut self) {
        self.push(DrawCmd::Restore);
    }

    pub fn translate(&mut self, dx: f32, dy: f32) {
        self.push(DrawCmd::Translate { dx, dy });
    }

    pub fn scale(&mut self, sx: f32, sy: f32) {
        self.push(DrawCmd::Scale { sx, sy });
    }

    pub fn clip_rect(&mut self, rect: Rect) {
        self.push(DrawCmd::ClipRect { rect });
    }

    /// An anti-aliased filled rectangle (UI panels).
    pub fn fill_rect(&mut self, rect: Rect, color: Color) {
        self.push(DrawCmd::FillRect { rect, color, anti_alias: true });
    }

    pub fn stroke_rect(&mut self, rect: Rect, color: Color, width: f32) {
        self.push(DrawCmd::StrokeRect { rect, color, width });
    }

    pub fn fill_round_rect(&mut self, rect: Rect, radius: f32, color: Color) {
        self.push(DrawCmd::FillRoundRect { rect, radius, color });
    }

    pub fn stroke_round_rect(&mut self, rect: Rect, radius: f32, color: Color, width: f32) {
        self.push(DrawCmd::StrokeRoundRect { rect, radius, color, width });
    }

    pub fn fill_circle(&mut self, cx: f32, cy: f32, radius: f32, color: Color) {
        self.push(DrawCmd::FillCircle { cx, cy, radius, color });
    }

    pub fn line(&mut self, from: (f32, f32), to: (f32, f32), color: Color, width: f32) {
        self.push(DrawCmd::Line { x0: from.0, y0: from.1, x1: to.0, y1: to.1, color, width });
    }

    /// Draws a whole image into `dst` with nearest-neighbour sampling.
    pub fn image(&mut self, image: ImageId, src: Rect, dst: Rect) {
        self.push(DrawCmd::Image { image, src, dst, opacity: 1.0, sampling: Sampling::Nearest, tint: None });
    }

    /// Left-aligned text with its baseline at `y`.
    pub fn text(&mut self, text: impl Into<String>, x: f32, y: f32, font: FontId, size: f32, color: Color) {
        self.push(DrawCmd::Text { text: text.into(), x, y, font, size, color, stroke: None, align: TextAlign::Left });
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn draw_lists_round_trip_through_json() {
        let mut list = DrawList::new();
        list.save();
        list.translate(-3.5, 2.0);
        list.clip_rect(Rect::new(0.0, 0.0, 10.0, 10.0));
        list.push(DrawCmd::FillRect {
            rect: Rect::new(1.0, 2.0, 3.0, 4.0),
            color: Color::rgb(1, 2, 3),
            anti_alias: false,
        });
        list.push(DrawCmd::Image {
            image: ImageId(7),
            src: Rect::new(0.0, 0.0, 32.0, 32.0),
            dst: Rect::new(0.0, 0.0, 28.0, 28.0),
            opacity: 0.5,
            sampling: Sampling::Nearest,
            tint: Some(Color::rgb(240, 184, 112)),
        });
        list.push(DrawCmd::Text {
            text: "+1".into(),
            x: 5.0,
            y: 6.0,
            font: FontId::Bold,
            size: 13.0,
            color: Color::rgb(255, 217, 74),
            stroke: Some(TextStroke { color: Color::rgba(0, 0, 0, 0x90), width: 3.0 }),
            align: TextAlign::Center,
        });
        list.restore();
        let json = serde_json::to_string(&list).unwrap();
        assert!(
            json.contains(
                r##"{"op":"fillRect","rect":{"x":1.0,"y":2.0,"width":3.0,"height":4.0},"color":"#010203ff","antiAlias":false}"##
            ),
            "{json}"
        );
        assert!(json.contains(r#""op":"image","image":7"#), "{json}");
        assert!(json.contains(r#""font":"bold""#) && json.contains(r#""align":"center""#), "{json}");
        let back: DrawList = serde_json::from_str(&json).unwrap();
        assert_eq!(back, list);
    }
}
