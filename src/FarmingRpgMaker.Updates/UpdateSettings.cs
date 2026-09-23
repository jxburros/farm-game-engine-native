namespace FarmingRpgMaker.Updates;

/// <summary>User preferences for the Update Center, persisted by <see cref="ISettingsStore"/>.</summary>
public sealed record UpdateSettings
{
    /// <summary>Check GitHub for updates in the background when the app starts.</summary>
    public bool CheckOnStartup { get; init; } = true;

    /// <summary>Start downloading as soon as an update is found.</summary>
    public bool AutoDownload { get; init; }

    public UpdateChannel Channel { get; init; } = UpdateChannel.Stable;

    /// <summary>Last time a check completed (successfully or not).</summary>
    public DateTimeOffset? LastChecked { get; init; }

    /// <summary>A version the user chose to skip; automatic checks won't nag about it.</summary>
    public string? SkippedVersion { get; init; }
}
