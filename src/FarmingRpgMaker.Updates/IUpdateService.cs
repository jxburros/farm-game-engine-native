namespace FarmingRpgMaker.Updates;

/// <summary>
/// Low-level update operations. <see cref="VelopackUpdateService"/> is the real
/// implementation; <see cref="Testing.FakeUpdateService"/> is a scriptable fake.
/// UI code should talk to <see cref="UpdateCoordinator"/> instead of this interface.
/// </summary>
public interface IUpdateService
{
    /// <summary>Version of the running app (SemVer, e.g. <c>0.1.0</c> or <c>0.1.0-dev</c>).</summary>
    string CurrentVersion { get; }

    /// <summary>
    /// False when running from a dev build (<c>dotnet run</c>, IDE, unzipped publish folder)
    /// rather than a Velopack install. Updates can only be checked/applied when true.
    /// </summary>
    bool IsInstalled { get; }

    /// <summary>Channel used by the next <see cref="CheckAsync"/>.</summary>
    UpdateChannel Channel { get; set; }

    /// <summary>Version of an update that was downloaded earlier and only needs a restart, if any.</summary>
    string? PendingRestartVersion { get; }

    /// <summary>Looks for a newer release. Never throws (except for cancellation).</summary>
    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the update found by the last successful <see cref="CheckAsync"/>.
    /// <paramref name="progress"/> receives 0–100 and may be called from any thread.
    /// Throws on failure.
    /// </summary>
    Task DownloadAsync(Action<int>? progress, CancellationToken cancellationToken = default);

    /// <summary>Exits the app, applies the downloaded update and relaunches. Does not return on success.</summary>
    void ApplyAndRestart();

    /// <summary>
    /// Schedules the downloaded update to be applied silently after the app exits
    /// (no relaunch). Call from the shutdown path.
    /// </summary>
    void ApplyOnExit();
}
