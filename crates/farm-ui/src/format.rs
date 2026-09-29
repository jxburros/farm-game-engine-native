//! Text formatting shared by the screens (numbers like JavaScript, money like the web UI).

use crate::i18n::Lang;

/// A number the screens print: whole numbers as they are, doubles like JavaScript's
/// `String(n)`.
pub trait DisplayNumber {
    fn display(self) -> String;
}

impl DisplayNumber for f64 {
    fn display(self) -> String {
        farm_sim::units::format_number(self)
    }
}

macro_rules! display_integer {
    ($($ty:ty),*) => {
        $(impl DisplayNumber for $ty {
            fn display(self) -> String {
                self.to_string()
            }
        })*
    };
}

display_integer!(i32, i64, u32, u64, usize);

/// A number as JavaScript's `String(n)` prints it.
pub fn num(value: impl DisplayNumber) -> String {
    value.display()
}

/// Money like the web UI (`$123`).
pub fn money(amount: i64) -> String {
    format!("${amount}")
}

/// Energy points as the HUD prints them (`97.5`).
pub fn energy(value: i32) -> String {
    farm_sim::units::text::<farm_sim::units::Energy>(value)
}

/// "spring" → "Spring" (CSS `capitalize`).
pub fn capitalize(text: &str) -> String {
    let mut chars = text.chars();
    match chars.next() {
        Some(first) => first.to_uppercase().chain(chars).collect(),
        None => String::new(),
    }
}

/// A season id's name when the calendar gives none: the built-in seasons in `lang`
/// ("fall" → "Otoño"), any other id capitalized.
pub fn season_name(id: &str, lang: Lang) -> String {
    let key = format!("season.{}", id.to_lowercase());
    lang.get(&key).map_or_else(|| capitalize(id), str::to_owned)
}

/// Play time as "2h 05m" or "12m".
pub fn play_time(seconds: f64) -> String {
    let minutes = if seconds.is_finite() { (seconds.max(0.0) / 60.0).floor() as u64 } else { 0 };
    if minutes >= 60 {
        format!("{}h {:02}m", minutes / 60, minutes % 60)
    } else {
        format!("{minutes}m")
    }
}

/// When a save was written, relative to `now` (both Unix seconds): "just now", "5 min ago",
/// "3 h ago", "2 days ago", else the UTC date; in `lang`.
pub fn saved_ago(saved_at: i64, now: i64, lang: Lang) -> String {
    if saved_at <= 0 {
        return String::new();
    }
    let age = now - saved_at;
    if now <= 0 || age < 0 {
        return utc_date(saved_at);
    }
    match age {
        0..=59 => lang.tr("time.justNow").to_owned(),
        60..=3599 => lang.format("time.minutesAgo", &[&(age / 60)]),
        3600..=86_399 => lang.format("time.hoursAgo", &[&(age / 3600)]),
        86_400..=172_799 => lang.tr("time.yesterday").to_owned(),
        172_800..=604_799 => lang.format("time.daysAgo", &[&(age / 86_400)]),
        _ => utc_date(saved_at),
    }
}

/// `YYYY-MM-DD` of a Unix time (UTC).
pub fn utc_date(unix: i64) -> String {
    // Howard Hinnant's days-to-civil.
    let days = unix.div_euclid(86_400);
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = doy - (153 * mp + 2) / 5 + 1;
    let month = if mp < 10 { mp + 3 } else { mp - 9 };
    let year = yoe + era * 400 + i64::from(month <= 2);
    format!("{year:04}-{month:02}-{day:02}")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn formats_like_the_web_ui() {
        assert_eq!(money(1250), "$1250");
        assert_eq!(num(0.5), "0.5");
        assert_eq!(num(7_u32), "7");
        assert_eq!(capitalize("spring"), "Spring");
        assert_eq!(capitalize(""), "");
        assert_eq!(season_name("spring", Lang::En), "Spring");
        assert_eq!(season_name("fall", Lang::Es), "Oto\u{f1}o");
        assert_eq!(season_name("monsoon", Lang::Es), "Monsoon");
        assert_eq!(play_time(59.0), "0m");
        assert_eq!(play_time(3.0 * 3600.0 + 5.0 * 60.0), "3h 05m");
    }

    #[test]
    fn relative_save_times() {
        let now = 1_790_000_000;
        assert_eq!(saved_ago(now - 30, now, Lang::En), "just now");
        assert_eq!(saved_ago(now - 300, now, Lang::En), "5 min ago");
        assert_eq!(saved_ago(now - 7200, now, Lang::En), "2 h ago");
        assert_eq!(saved_ago(now - 90_000, now, Lang::En), "yesterday");
        assert_eq!(saved_ago(now - 3 * 86_400, now, Lang::En), "3 days ago");
        assert_eq!(saved_ago(now - 300, now, Lang::Es), "hace 5 min");
        assert_eq!(saved_ago(now - 90_000, now, Lang::Es), "ayer");
        assert_eq!(saved_ago(0, now, Lang::En), "");
        assert_eq!(utc_date(0), "1970-01-01");
        assert_eq!(utc_date(1_790_000_000), "2026-09-21");
    }
}
