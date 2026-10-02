using System.Globalization;
using FarmEngine.Export;

namespace FarmingRpgMaker.App;

/// <summary>
/// How the editor writes sizes, times and numbers, shared by its windows and views so the
/// same value always reads the same way.
/// </summary>
public static class DisplayFormat
{
    /// <summary>
    /// A file's size in binary units: "12 B", "3.4 KB", "5.6 MB". The same text Export Game and
    /// <c>farmc</c> print (F# <see cref="GameExporter.FormatSize"/>).
    /// </summary>
    public static string FileSize(long bytes) => GameExporter.FormatSize(bytes);

    /// <summary>A download's size in decimal units, as release pages state it: "850 bytes", "12 KB", "48.7 MB".</summary>
    public static string DownloadSize(long bytes) => bytes switch
    {
        >= 1_000_000_000 => (bytes / 1_000_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1_000_000 => (bytes / 1_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        >= 1_000 => (bytes / 1_000d).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " bytes",
    };

    /// <summary>
    /// When something happened, from the creator's point of view at <paramref name="now"/>:
    /// "just now", "5 minutes ago", "today at 3:04 PM", "yesterday at 9:15 AM" or
    /// "Sep 30, 2026 at 9:15 AM" (local time).
    /// </summary>
    public static string RelativeTime(DateTimeOffset when, DateTimeOffset now)
    {
        var local = when.ToLocalTime();
        var localNow = now.ToLocalTime();
        var ago = localNow - local;
        if (ago < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (ago < TimeSpan.FromHours(1))
        {
            var minutes = (int)ago.TotalMinutes;
            return minutes == 1 ? "1 minute ago" : $"{minutes} minutes ago";
        }

        var time = local.ToString("h:mm tt", CultureInfo.InvariantCulture);
        if (local.Date == localNow.Date)
        {
            return $"today at {time}";
        }

        if (local.Date == localNow.Date.AddDays(-1))
        {
            return $"yesterday at {time}";
        }

        return local.ToString("MMM d, yyyy", CultureInfo.InvariantCulture) + " at " + time;
    }

    /// <summary>A number as the editor's text fields show it: invariant culture, general format ("16", "0.5").</summary>
    public static string Number(double value) => value.ToString("G", CultureInfo.InvariantCulture);
}
