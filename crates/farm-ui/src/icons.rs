//! Small vector icons built from draw-list primitives and the few symbol glyphs Inter has
//! (★ ✓ ♥ ▶). Each icon fills a square; `background` is the surface it sits on (the moon's
//! crescent is cut with it).

use farm_render::{Color, DrawCmd, DrawList, FontId, Rect, TextAlign};

/// The icons the game UI uses.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Icon {
    Close,
    Check,
    Star,
    Package,
    Hammer,
    Moon,
    Menu,
    Store,
    Info,
    Alert,
    Person,
    Coin,
    Gear,
    Save,
    Folder,
    Play,
    Exit,
    Heart,
    Gamepad,
    Keyboard,
    Speaker,
    Monitor,
    Eye,
    Sun,
    Clock,
    Book,
}

/// Draws `icon` into the square `rect` in `color`.
pub fn draw(list: &mut DrawList, icon: Icon, rect: Rect, color: Color, background: Color) {
    let s = rect.width.min(rect.height);
    let (ox, oy) = (rect.x + (rect.width - s) / 2.0, rect.y + (rect.height - s) / 2.0);
    let p = |x: f32, y: f32| (ox + x * s, oy + y * s);
    let r = |x: f32, y: f32, w: f32, h: f32| Rect::new(ox + x * s, oy + y * s, w * s, h * s);
    let stroke = (s * 0.1).max(1.2);
    let line = |list: &mut DrawList, a: (f32, f32), b: (f32, f32)| list.line(p(a.0, a.1), p(b.0, b.1), color, stroke);
    let glyph = |list: &mut DrawList, text: &str, scale: f32| {
        let size = s * scale;
        list.push(DrawCmd::Text {
            text: text.to_owned(),
            x: ox + s / 2.0,
            y: oy + s / 2.0 + size * 0.727 / 2.0,
            font: FontId::Bold,
            size,
            color,
            stroke: None,
            align: TextAlign::Center,
        });
    };
    let ring = |list: &mut DrawList| {
        list.push(DrawCmd::StrokeCircle { cx: ox + s / 2.0, cy: oy + s / 2.0, radius: s * 0.4, color, width: stroke })
    };
    match icon {
        Icon::Close => {
            line(list, (0.22, 0.22), (0.78, 0.78));
            line(list, (0.78, 0.22), (0.22, 0.78));
        }
        Icon::Check => {
            line(list, (0.16, 0.54), (0.4, 0.76));
            line(list, (0.4, 0.76), (0.84, 0.26));
        }
        Icon::Star => glyph(list, "\u{2605}", 1.05),
        Icon::Heart => glyph(list, "\u{2665}", 1.0),
        Icon::Play => glyph(list, "\u{25B6}", 0.8),
        Icon::Package => {
            list.stroke_round_rect(r(0.14, 0.22, 0.72, 0.62), s * 0.08, color, stroke);
            line(list, (0.14, 0.42), (0.86, 0.42));
            list.fill_rect(r(0.42, 0.42, 0.16, 0.16), color);
        }
        Icon::Hammer => {
            list.fill_round_rect(r(0.14, 0.16, 0.6, 0.24), s * 0.05, color);
            list.fill_rect(r(0.4, 0.38, 0.14, 0.5), color);
        }
        Icon::Moon => {
            list.fill_circle(ox + 0.47 * s, oy + 0.52 * s, 0.36 * s, color);
            list.fill_circle(ox + 0.66 * s, oy + 0.38 * s, 0.3 * s, background);
        }
        Icon::Menu => {
            for y in [0.28, 0.5, 0.72] {
                line(list, (0.18, y), (0.82, y));
            }
        }
        Icon::Store => {
            list.fill_round_rect(r(0.1, 0.18, 0.8, 0.2), s * 0.05, color);
            list.stroke_rect(r(0.18, 0.42, 0.64, 0.4), color, stroke);
            list.fill_rect(r(0.42, 0.58, 0.16, 0.24), color);
        }
        Icon::Info => {
            ring(list);
            glyph(list, "i", 0.55);
        }
        Icon::Alert => {
            ring(list);
            glyph(list, "!", 0.55);
        }
        Icon::Coin => {
            ring(list);
            glyph(list, "$", 0.5);
        }
        Icon::Person => {
            list.fill_circle(ox + 0.5 * s, oy + 0.34 * s, 0.17 * s, color);
            list.fill_round_rect(r(0.2, 0.58, 0.6, 0.3), s * 0.15, color);
        }
        Icon::Gear => {
            let (cx, cy) = (ox + s / 2.0, oy + s / 2.0);
            for step in 0..8 {
                let angle = step as f32 * std::f32::consts::FRAC_PI_4;
                let (dx, dy) = (angle.cos(), angle.sin());
                list.line(
                    (cx + dx * s * 0.2, cy + dy * s * 0.2),
                    (cx + dx * s * 0.44, cy + dy * s * 0.44),
                    color,
                    stroke * 1.4,
                );
            }
            list.fill_circle(cx, cy, s * 0.3, color);
            list.fill_circle(cx, cy, s * 0.12, background);
        }
        Icon::Save => {
            list.stroke_round_rect(r(0.16, 0.16, 0.68, 0.68), s * 0.06, color, stroke);
            list.fill_rect(r(0.3, 0.16, 0.4, 0.22), color);
            list.fill_rect(r(0.3, 0.56, 0.4, 0.28), color);
        }
        Icon::Folder => {
            list.fill_round_rect(r(0.12, 0.22, 0.34, 0.16), s * 0.04, color);
            list.fill_round_rect(r(0.12, 0.32, 0.76, 0.48), s * 0.06, color);
        }
        Icon::Exit => {
            list.stroke_rect(r(0.16, 0.16, 0.44, 0.68), color, stroke);
            line(list, (0.42, 0.5), (0.88, 0.5));
            line(list, (0.72, 0.34), (0.88, 0.5));
            line(list, (0.72, 0.66), (0.88, 0.5));
        }
        Icon::Gamepad => {
            list.fill_round_rect(r(0.08, 0.28, 0.84, 0.46), s * 0.2, color);
            list.fill_rect(r(0.2, 0.47, 0.2, 0.07), background);
            list.fill_rect(r(0.265, 0.4, 0.07, 0.21), background);
            list.fill_circle(ox + 0.66 * s, oy + 0.45 * s, 0.05 * s, background);
            list.fill_circle(ox + 0.76 * s, oy + 0.56 * s, 0.05 * s, background);
        }
        Icon::Keyboard => {
            list.stroke_round_rect(r(0.08, 0.26, 0.84, 0.48), s * 0.08, color, stroke);
            for row in 0..2 {
                for col in 0..4 {
                    list.fill_rect(r(0.2 + col as f32 * 0.17, 0.36 + row as f32 * 0.14, 0.08, 0.07), color);
                }
            }
            list.fill_rect(r(0.3, 0.62, 0.4, 0.05), color);
        }
        Icon::Speaker => {
            list.fill_rect(r(0.14, 0.38, 0.18, 0.24), color);
            line(list, (0.32, 0.38), (0.54, 0.2));
            line(list, (0.54, 0.2), (0.54, 0.8));
            line(list, (0.54, 0.8), (0.32, 0.62));
            line(list, (0.68, 0.36), (0.74, 0.5));
            line(list, (0.74, 0.5), (0.68, 0.64));
            line(list, (0.8, 0.26), (0.88, 0.5));
            line(list, (0.88, 0.5), (0.8, 0.74));
        }
        Icon::Monitor => {
            list.stroke_round_rect(r(0.1, 0.16, 0.8, 0.54), s * 0.06, color, stroke);
            line(list, (0.5, 0.7), (0.5, 0.84));
            line(list, (0.3, 0.86), (0.7, 0.86));
        }
        Icon::Eye => {
            list.push(DrawCmd::FillOval { rect: r(0.06, 0.26, 0.88, 0.48), color });
            list.push(DrawCmd::FillOval { rect: r(0.14, 0.33, 0.72, 0.34), color: background });
            list.fill_circle(ox + 0.5 * s, oy + 0.5 * s, 0.13 * s, color);
        }
        Icon::Sun => {
            let (cx, cy) = (ox + s / 2.0, oy + s / 2.0);
            for step in 0..8 {
                let angle = step as f32 * std::f32::consts::FRAC_PI_4;
                let (dx, dy) = (angle.cos(), angle.sin());
                list.line(
                    (cx + dx * s * 0.3, cy + dy * s * 0.3),
                    (cx + dx * s * 0.46, cy + dy * s * 0.46),
                    color,
                    stroke,
                );
            }
            list.fill_circle(cx, cy, s * 0.2, color);
        }
        Icon::Clock => {
            ring(list);
            line(list, (0.5, 0.5), (0.5, 0.26));
            line(list, (0.5, 0.5), (0.68, 0.58));
        }
        Icon::Book => {
            list.stroke_round_rect(r(0.18, 0.14, 0.64, 0.72), s * 0.06, color, stroke);
            line(list, (0.34, 0.14), (0.34, 0.86));
            line(list, (0.44, 0.34), (0.72, 0.34));
            line(list, (0.44, 0.48), (0.72, 0.48));
        }
    }
}
