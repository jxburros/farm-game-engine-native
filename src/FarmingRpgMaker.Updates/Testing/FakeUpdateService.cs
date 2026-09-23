namespace FarmingRpgMaker.Updates.Testing;

/// <summary>
/// Scriptable <see cref="IUpdateService"/> for tests, screenshots and UI demos
/// (run the app with <c>FARMING_RPG_MAKER_FAKE_UPDATES=1</c>).
/// </summary>
public sealed class FakeUpdateService : IUpdateService
{
    private TaskCompletionSource? _downloadGate;

    public string CurrentVersion { get; set; } = "0.1.0";

    public bool IsInstalled { get; set; } = true;

    public UpdateChannel Channel { get; set; } = UpdateChannel.Stable;

    public string? PendingRestartVersion { get; set; }

    /// <summary>Result returned by <see cref="CheckAsync"/> for the stable channel.</summary>
    public UpdateCheckResult NextResult { get; set; } = new UpdateCheckResult.UpToDate();

    /// <summary>Optional different result for the pre-release channel (defaults to <see cref="NextResult"/>).</summary>
    public UpdateCheckResult? PrereleaseResult { get; set; }

    /// <summary>Progress values reported by <see cref="DownloadAsync"/>.</summary>
    public IReadOnlyList<int> DownloadSteps { get; set; } = [0, 25, 50, 75, 100];

    /// <summary>When set, <see cref="DownloadAsync"/> throws this after reporting progress.</summary>
    public Exception? DownloadFailure { get; set; }

    /// <summary>When true, <see cref="DownloadAsync"/> pauses after the first progress step until <see cref="ReleaseDownload"/>.</summary>
    public bool HoldDownload { get; set; }

    /// <summary>Artificial latency for <see cref="CheckAsync"/>.</summary>
    public TimeSpan CheckDelay { get; set; } = TimeSpan.Zero;

    public int CheckCount { get; private set; }

    public int DownloadCount { get; private set; }

    public int ApplyAndRestartCount { get; private set; }

    public int ApplyOnExitCount { get; private set; }

    public List<UpdateChannel> CheckedChannels { get; } = [];

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        CheckCount++;
        CheckedChannels.Add(Channel);
        if (CheckDelay > TimeSpan.Zero)
        {
            await Task.Delay(CheckDelay, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Channel == UpdateChannel.Prerelease && PrereleaseResult is not null ? PrereleaseResult : NextResult;
    }

    public async Task DownloadAsync(Action<int>? progress, CancellationToken cancellationToken = default)
    {
        DownloadCount++;
        for (var i = 0; i < DownloadSteps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke(DownloadSteps[i]);
            if (i == 0 && HoldDownload)
            {
                _downloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await _downloadGate.Task.WaitAsync(cancellationToken).ConfigureAwait(true);
            }
            else
            {
                await Task.Yield();
            }
        }

        if (DownloadFailure is not null)
        {
            throw DownloadFailure;
        }
    }

    /// <summary>Lets a download paused by <see cref="HoldDownload"/> continue.</summary>
    public void ReleaseDownload() => _downloadGate?.TrySetResult();

    public void ApplyAndRestart() => ApplyAndRestartCount++;

    public void ApplyOnExit() => ApplyOnExitCount++;

    /// <summary>A sample "update available" result with realistic release notes.</summary>
    public static UpdateCheckResult.UpdateAvailable SampleUpdate(string version = "0.2.0") => new(
        version,
        $"""
        ## What's new in {version}

        - **Fishing** mini-game with 12 new fish and a seasonal leaderboard
        - Crops now show a *growth stage* tooltip in the editor
        - Faster project loading for large maps

        ### Fixes

        1. Fixed NPCs occasionally walking through fences
        2. `Export Project JSON` keeps custom tile art

        See the [full changelog]({UpdateSource.ReleasesPageUrl}) for details.
        """,
        new DateTimeOffset(2026, 9, 20, 16, 30, 0, TimeSpan.Zero),
        48_700_000,
        $"{UpdateSource.ReleasesPageUrl}/tag/v{version}");
}
