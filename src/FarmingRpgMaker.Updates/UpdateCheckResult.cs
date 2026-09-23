namespace FarmingRpgMaker.Updates;

/// <summary>Outcome of <see cref="IUpdateService.CheckAsync"/>.</summary>
public abstract record UpdateCheckResult
{
    private UpdateCheckResult()
    {
    }

    /// <summary>The running version is the newest one on the selected channel.</summary>
    public sealed record UpToDate : UpdateCheckResult;

    /// <summary>A newer version can be downloaded.</summary>
    /// <param name="Version">SemVer of the new release, without a leading <c>v</c>.</param>
    /// <param name="ReleaseNotesMarkdown">Release notes (GitHub release body, falling back to Velopack's notes). May be empty.</param>
    /// <param name="PublishedAt">When the release was published on GitHub, if known.</param>
    /// <param name="SizeBytes">Download size of the full package, if known.</param>
    /// <param name="ReleaseUrl">GitHub page of the release, if known.</param>
    public sealed record UpdateAvailable(
        string Version,
        string ReleaseNotesMarkdown,
        DateTimeOffset? PublishedAt = null,
        long? SizeBytes = null,
        string? ReleaseUrl = null) : UpdateCheckResult;

    /// <summary>The check failed (offline, rate-limited, feed missing…). Never thrown; always returned.</summary>
    public sealed record Error(string Message) : UpdateCheckResult;
}
