//! Layout helpers: rectangle cutting (rows, columns, padding, alignment) and a wrapping flow.
//!
//! Screens lay out by cutting a region into pieces: `let header = area.cut_top(48.0)` removes a
//! 48-unit strip from the top of `area` and returns it; `cut_left`, `cut_right` and
//! `cut_bottom` do the same on the other sides. [`Flow`] places items left to right and wraps
//! them onto new lines (HUD statistics, the controls hint row).

use farm_render::Rect;

/// Horizontal alignment inside a rectangle.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum Align {
    #[default]
    Start,
    Center,
    End,
}

/// Rectangle cutting and geometry for [`Rect`].
pub trait RectExt: Sized {
    /// Shrinks every side by `d`.
    fn inset(self, d: f32) -> Rect;
    /// Shrinks the left/right sides by `dx` and the top/bottom by `dy`.
    fn inset_xy(self, dx: f32, dy: f32) -> Rect;
    /// Removes a strip of `height` from the top and returns it.
    fn cut_top(&mut self, height: f32) -> Rect;
    fn cut_bottom(&mut self, height: f32) -> Rect;
    fn cut_left(&mut self, width: f32) -> Rect;
    fn cut_right(&mut self, width: f32) -> Rect;
    fn contains(&self, x: f32, y: f32) -> bool;
    /// The overlap of two rectangles (empty when they don't overlap).
    fn intersect(&self, other: &Rect) -> Rect;
    fn is_empty(&self) -> bool;
    fn center(&self) -> (f32, f32);
    /// A `width`×`height` rectangle centered in this one.
    fn centered(&self, width: f32, height: f32) -> Rect;
    /// A `width`-wide slice aligned inside this one (full height).
    fn aligned(&self, width: f32, align: Align) -> Rect;
    /// The same rectangle moved by (`dx`, `dy`).
    fn offset(&self, dx: f32, dy: f32) -> Rect;
}

impl RectExt for Rect {
    fn inset(self, d: f32) -> Rect {
        self.inset_xy(d, d)
    }

    fn inset_xy(self, dx: f32, dy: f32) -> Rect {
        Rect::new(self.x + dx, self.y + dy, (self.width - 2.0 * dx).max(0.0), (self.height - 2.0 * dy).max(0.0))
    }

    fn cut_top(&mut self, height: f32) -> Rect {
        let height = height.clamp(0.0, self.height);
        let cut = Rect::new(self.x, self.y, self.width, height);
        self.y += height;
        self.height -= height;
        cut
    }

    fn cut_bottom(&mut self, height: f32) -> Rect {
        let height = height.clamp(0.0, self.height);
        self.height -= height;
        Rect::new(self.x, self.y + self.height, self.width, height)
    }

    fn cut_left(&mut self, width: f32) -> Rect {
        let width = width.clamp(0.0, self.width);
        let cut = Rect::new(self.x, self.y, width, self.height);
        self.x += width;
        self.width -= width;
        cut
    }

    fn cut_right(&mut self, width: f32) -> Rect {
        let width = width.clamp(0.0, self.width);
        self.width -= width;
        Rect::new(self.x + self.width, self.y, width, self.height)
    }

    fn contains(&self, x: f32, y: f32) -> bool {
        x >= self.x && y >= self.y && x < self.x + self.width && y < self.y + self.height
    }

    fn intersect(&self, other: &Rect) -> Rect {
        let x0 = self.x.max(other.x);
        let y0 = self.y.max(other.y);
        let x1 = (self.x + self.width).min(other.x + other.width);
        let y1 = (self.y + self.height).min(other.y + other.height);
        Rect::new(x0, y0, (x1 - x0).max(0.0), (y1 - y0).max(0.0))
    }

    fn is_empty(&self) -> bool {
        !(self.width > 0.0 && self.height > 0.0)
    }

    fn center(&self) -> (f32, f32) {
        (self.x + self.width / 2.0, self.y + self.height / 2.0)
    }

    fn centered(&self, width: f32, height: f32) -> Rect {
        let (width, height) = (width.min(self.width), height.min(self.height));
        Rect::new(self.x + (self.width - width) / 2.0, self.y + (self.height - height) / 2.0, width, height)
    }

    fn aligned(&self, width: f32, align: Align) -> Rect {
        let width = width.min(self.width);
        let x = match align {
            Align::Start => self.x,
            Align::Center => self.x + (self.width - width) / 2.0,
            Align::End => self.x + self.width - width,
        };
        Rect::new(x, self.y, width, self.height)
    }

    fn offset(&self, dx: f32, dy: f32) -> Rect {
        Rect::new(self.x + dx, self.y + dy, self.width, self.height)
    }
}

/// Places items of known size left to right, wrapping to a new line when the next item would
/// not fit. Lines are aligned as a whole (the HUD's stats, the centered hint row).
#[derive(Debug, Clone, PartialEq)]
pub struct Flow {
    /// Item rectangles, in input order.
    pub items: Vec<Rect>,
    /// Total height of all lines.
    pub height: f32,
    /// Width of the widest line.
    pub width: f32,
}

impl Flow {
    /// Lays out `sizes` (width, height) in `area.width`, starting at `area`'s top-left, with
    /// `gap_x` between items and `gap_y` between lines. Items on a line are centered vertically.
    pub fn layout(area: Rect, sizes: &[(f32, f32)], gap_x: f32, gap_y: f32, align: Align) -> Flow {
        let mut lines: Vec<(usize, usize, f32, f32)> = Vec::new(); // (start, end, width, height)
        let mut start = 0;
        let mut width = 0.0f32;
        let mut height = 0.0f32;
        for (index, &(w, h)) in sizes.iter().enumerate() {
            let next = if index == start { w } else { width + gap_x + w };
            if index > start && next > area.width {
                lines.push((start, index, width, height));
                start = index;
                width = w;
                height = h;
            } else {
                width = next;
                height = height.max(h);
            }
        }
        if start < sizes.len() {
            lines.push((start, sizes.len(), width, height));
        }
        let mut items = vec![Rect::default(); sizes.len()];
        let mut y = area.y;
        let mut widest = 0.0f32;
        for (line, &(from, to, line_width, line_height)) in lines.iter().enumerate() {
            if line > 0 {
                y += gap_y;
            }
            widest = widest.max(line_width);
            let mut x = match align {
                Align::Start => area.x,
                Align::Center => area.x + (area.width - line_width) / 2.0,
                Align::End => area.x + area.width - line_width,
            };
            for index in from..to {
                let (w, h) = sizes[index];
                items[index] = Rect::new(x, y + (line_height - h) / 2.0, w, h);
                x += w + gap_x;
            }
            y += line_height;
        }
        Flow { items, height: y - area.y, width: widest }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cutting_splits_a_rectangle_into_rows_and_columns() {
        let mut area = Rect::new(0.0, 0.0, 100.0, 50.0);
        let top = area.cut_top(10.0);
        let right = area.cut_right(30.0);
        assert_eq!(top, Rect::new(0.0, 0.0, 100.0, 10.0));
        assert_eq!(right, Rect::new(70.0, 10.0, 30.0, 40.0));
        assert_eq!(area, Rect::new(0.0, 10.0, 70.0, 40.0));
        let bottom = area.cut_bottom(100.0);
        assert_eq!(bottom.height, 40.0);
        assert!(area.is_empty());
        let mut row = Rect::new(0.0, 0.0, 20.0, 5.0);
        assert_eq!(row.cut_left(8.0).width, 8.0);
        assert_eq!(row.x, 8.0);
    }

    #[test]
    fn geometry_helpers() {
        let r = Rect::new(10.0, 10.0, 100.0, 40.0);
        assert_eq!(r.inset(5.0), Rect::new(15.0, 15.0, 90.0, 30.0));
        assert_eq!(r.centered(20.0, 10.0), Rect::new(50.0, 25.0, 20.0, 10.0));
        assert_eq!(r.aligned(20.0, Align::End).x, 90.0);
        assert!(r.contains(10.0, 49.9) && !r.contains(110.0, 20.0));
        assert!(r.intersect(&Rect::new(200.0, 0.0, 5.0, 5.0)).is_empty());
        assert_eq!(r.intersect(&Rect::new(0.0, 0.0, 20.0, 20.0)), Rect::new(10.0, 10.0, 10.0, 10.0));
    }

    #[test]
    fn flow_wraps_and_aligns_lines() {
        let area = Rect::new(0.0, 0.0, 100.0, 0.0);
        let flow = Flow::layout(area, &[(40.0, 10.0), (40.0, 20.0), (40.0, 10.0)], 10.0, 4.0, Align::Center);
        // Two items fit on the first line (40 + 10 + 40 = 90), the third wraps.
        assert_eq!(flow.items[0], Rect::new(5.0, 5.0, 40.0, 10.0));
        assert_eq!(flow.items[1], Rect::new(55.0, 0.0, 40.0, 20.0));
        assert_eq!(flow.items[2], Rect::new(30.0, 24.0, 40.0, 10.0));
        assert_eq!(flow.height, 34.0);
        assert_eq!(flow.width, 90.0);
        // An item wider than the area still gets a line of its own.
        let wide = Flow::layout(area, &[(150.0, 10.0), (10.0, 10.0)], 5.0, 0.0, Align::Start);
        assert_eq!(wide.items[1].y, 10.0);
    }
}
