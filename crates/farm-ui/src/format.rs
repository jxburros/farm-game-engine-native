//! Text formatting shared by the screens (numbers like JavaScript, money like the web UI).

/// A number as JavaScript's `String(n)` prints it.
pub fn num(value: f64) -> String {
    farm_sim::js::num(value)
}

/// Money like the web UI (`$123`).
pub fn money(amount: f64) -> String {
    format!("${}", num(amount))
}

/// "spring" → "Spring" (CSS `capitalize`).
pub fn capitalize(text: &str) -> String {
    let mut chars = text.chars();
    match chars.next() {
        Some(first) => first.to_uppercase().chain(chars).collect(),
        None => String::new(),
    }
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
/// "3 h ago", "2 days ago", else the UTC date.
pub fn saved_ago(saved_at: i64, now: i64) -> String {
    if saved_at <= 0 {
        return String::new();
    }
    let age = now - saved_at;
    if now <= 0 || age < 0 {
        return utc_date(saved_at);
    }
    match age {
        0..=59 => "just now".to_owned(),
        60..=3599 => format!("{} min ago", age / 60),
        3600..=86_399 => format!("{} h ago", age / 3600),
        86_400..=172_799 => "yesterday".to_owned(),
        172_800..=604_799 => format!("{} days ago", age / 86_400),
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
        assert_eq!(money(1250.0), "$1250");
        assert_eq!(money(0.5), "$0.5");
        assert_eq!(capitalize("spring"), "Spring");
        assert_eq!(capitalize(""), "");
        assert_eq!(play_time(59.0), "0m");
        assert_eq!(play_time(3.0 * 3600.0 + 5.0 * 60.0), "3h 05m");
    }

    #[test]
    fn relative_save_times() {
        let now = 1_790_000_000;
        assert_eq!(saved_ago(now - 30, now), "just now");
        assert_eq!(saved_ago(now - 300, now), "5 min ago");
        assert_eq!(saved_ago(now - 7200, now), "2 h ago");
        assert_eq!(saved_ago(now - 90_000, now), "yesterday");
        assert_eq!(saved_ago(now - 3 * 86_400, now), "3 days ago");
        assert_eq!(saved_ago(0, now), "");
        assert_eq!(utc_date(0), "1970-01-01");
        assert_eq!(utc_date(1_790_000_000), "2026-09-21");
    }
}
