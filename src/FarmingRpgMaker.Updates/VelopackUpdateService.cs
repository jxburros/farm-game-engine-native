using Velopack;
using Velopack.Sources;

namespace FarmingRpgMaker.Updates;

/// <summary>
/// <see cref="IUpdateService"/> backed by Velopack and GitHub Releases of
/// <see cref="UpdateSource.RepositoryUrl"/>. Release notes come from the GitHub API
/// (the release body) and fall back to the notes embedded in the Velopack package.
/// </summary>
public sealed class VelopackUpdateService : IUpdateService
{
    private readonly GitHubReleaseNotesClient _notesClient;
    private readonly string _repositoryUrl;
    private UpdateManager? _manager;
    private UpdateChannel _channel;
    private UpdateInfo? _lastUpdate;
    private bool _downloaded;

    public VelopackUpdateService(
        UpdateChannel channel = UpdateChannel.Stable,
        GitHubReleaseNotesClient? notesClient = null,
        string repositoryUrl = UpdateSource.RepositoryUrl)
    {
        _channel = channel;
        _notesClient = notesClient ?? new GitHubReleaseNotesClient();
        _repositoryUrl = repositoryUrl;
    }

    public UpdateChannel Channel
    {
        get => _channel;
        set
        {
            if (_channel == value)
            {
                return;
            }

            _channel = value;
            _manager = null; // GithubSource bakes the prerelease flag in; rebuild on next use.
            _lastUpdate = null;
            _downloaded = false;
        }
    }

    public bool IsInstalled => TryGetManager()?.IsInstalled ?? false;

    public string CurrentVersion
    {
        get
        {
            var manager = TryGetManager();
            return manager is { IsInstalled: true, CurrentVersion: { } v } ? v.ToString() : AppVersion.Current;
        }
    }

    public string? PendingRestartVersion
    {
        get
        {
            try
            {
                return TryGetManager() is { IsInstalled: true } m ? m.UpdatePendingRestart?.Version?.ToString() : null;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var manager = TryGetManager();
        if (manager is not { IsInstalled: true })
        {
            return new UpdateCheckResult.Error("Updates are available only in the installed app.");
        }

        UpdateInfo? info;
        try
        {
            info = await manager.CheckForUpdatesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Velopack surfaces network/feed problems as assorted exception types.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new UpdateCheckResult.Error(DescribeCheckFailure(ex));
        }

        _lastUpdate = info;
        _downloaded = false;
        if (info is null)
        {
            return new UpdateCheckResult.UpToDate();
        }

        var target = info.TargetFullRelease;
        var version = target.Version.ToString();
        GitHubReleaseInfo? release = null;
        try
        {
            release = await _notesClient.FindReleaseAsync(version, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Notes are optional.
        }

        var notes = !string.IsNullOrWhiteSpace(release?.Body) ? release!.Body! : target.NotesMarkdown ?? "";
        long? size = target.Size > 0 ? target.Size : null;
        return new UpdateCheckResult.UpdateAvailable(version, notes, release?.PublishedAt, size, release?.HtmlUrl);
    }

    public async Task DownloadAsync(Action<int>? progress, CancellationToken cancellationToken = default)
    {
        var manager = TryGetManager() ?? throw new InvalidOperationException("Updates are available only in the installed app.");
        var update = _lastUpdate ?? throw new InvalidOperationException("Check for updates before downloading.");
        await manager.DownloadUpdatesAsync(update, p => progress?.Invoke(Math.Clamp(p, 0, 100)), cancellationToken).ConfigureAwait(false);
        _downloaded = true;
    }

    public void ApplyAndRestart()
    {
        var manager = TryGetManager() ?? throw new InvalidOperationException("Updates are available only in the installed app.");
        var toApply = _downloaded && _lastUpdate is not null ? _lastUpdate.TargetFullRelease : manager.UpdatePendingRestart;
        manager.ApplyUpdatesAndRestart(toApply);
    }

    public void ApplyOnExit()
    {
        var manager = TryGetManager();
        if (manager is not { IsInstalled: true })
        {
            return;
        }

        var toApply = _downloaded && _lastUpdate is not null ? _lastUpdate.TargetFullRelease : manager.UpdatePendingRestart;
        if (toApply is not null)
        {
            manager.WaitExitThenApplyUpdates(toApply, silent: true, restart: false);
        }
    }

    private UpdateManager? TryGetManager()
    {
        if (_manager is not null)
        {
            return _manager;
        }

        try
        {
            var source = new GithubSource(_repositoryUrl, accessToken: null, prerelease: _channel == UpdateChannel.Prerelease);
            _manager = new UpdateManager(source);
        }
#pragma warning disable CA1031 // A dev build without a Velopack install must never crash here.
        catch (Exception)
#pragma warning restore CA1031
        {
            _manager = null;
        }

        return _manager;
    }

    private static string DescribeCheckFailure(Exception ex)
    {
        var message = ex switch
        {
            HttpRequestException => "Couldn't reach GitHub. Check your internet connection and try again.",
            TimeoutException => "GitHub did not respond in time. Try again in a moment.",
            _ when ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                => "GitHub's hourly request limit was reached. Try again later.",
            _ => $"The update check failed: {ex.Message}",
        };
        return message;
    }
}
