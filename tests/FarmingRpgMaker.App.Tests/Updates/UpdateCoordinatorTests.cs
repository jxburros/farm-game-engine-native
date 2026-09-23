using FarmingRpgMaker.Updates;
using FarmingRpgMaker.Updates.Testing;

namespace FarmingRpgMaker.App.Tests.Updates;

public sealed class UpdateCoordinatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeUpdateService _service = new();
    private readonly TestTime _time = new(Start);

    private UpdateCoordinator Create(UpdateSettings? settings = null, InMemorySettingsStore? store = null) =>
        new(_service, store ?? new InMemorySettingsStore(settings), _time, dispatch: action => action());

    [Fact]
    public void StartsIdleWhenInstalled()
    {
        var coordinator = Create();

        Assert.Equal(UpdateState.Idle, coordinator.State);
        Assert.True(coordinator.CanCheck);
        Assert.False(coordinator.IsBadgeVisible);
        Assert.Equal("0.1.0", coordinator.CurrentVersion);
    }

    [Fact]
    public async Task NotInstalled_NeverCallsTheService()
    {
        _service.IsInstalled = false;
        var coordinator = Create();

        Assert.Equal(UpdateState.NotInstalled, coordinator.State);
        Assert.False(coordinator.CanCheck);

        await coordinator.CheckAsync();
        await coordinator.RunStartupCheckAsync();

        Assert.Equal(UpdateState.NotInstalled, coordinator.State);
        Assert.Equal(0, _service.CheckCount);
    }

    [Fact]
    public async Task Check_UpToDate_RecordsLastChecked()
    {
        var store = new InMemorySettingsStore();
        var coordinator = Create(store: store);
        var states = new List<UpdateState>();
        coordinator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UpdateCoordinator.State))
            {
                states.Add(coordinator.State);
            }
        };

        await coordinator.CheckAsync();

        Assert.Equal([UpdateState.Checking, UpdateState.UpToDate], states);
        Assert.Null(coordinator.AvailableUpdate);
        Assert.Equal(Start, coordinator.LastChecked);
        Assert.Equal(Start, store.Current.LastChecked);
    }

    [Fact]
    public async Task Check_Available_ThenDownloadReportsProgress_ThenReadyToInstall()
    {
        _service.NextResult = FakeUpdateService.SampleUpdate("0.2.0");
        _service.DownloadSteps = [0, 10, 55, 90, 100];
        var coordinator = Create();

        await coordinator.CheckAsync();

        Assert.Equal(UpdateState.Available, coordinator.State);
        Assert.Equal("0.2.0", coordinator.AvailableUpdate?.Version);
        Assert.True(coordinator.IsBadgeVisible);
        Assert.Equal("Update available", coordinator.BadgeText);
        Assert.True(coordinator.CanDownload);

        var progress = new List<int>();
        var sawDownloading = false;
        coordinator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UpdateCoordinator.DownloadProgress))
            {
                progress.Add(coordinator.DownloadProgress);
            }

            sawDownloading |= coordinator.State == UpdateState.Downloading;
        };

        await coordinator.DownloadAsync();

        Assert.True(sawDownloading);
        Assert.Equal([10, 55, 90, 100], progress);
        Assert.Equal(UpdateState.ReadyToInstall, coordinator.State);
        Assert.True(coordinator.CanApply);
        Assert.Equal("Restart to update", coordinator.BadgeText);
        Assert.Equal(1, _service.DownloadCount);

        coordinator.ApplyAndRestart();
        Assert.Equal(1, _service.ApplyAndRestartCount);
    }

    [Fact]
    public async Task Check_Error_SurfacesMessage()
    {
        _service.NextResult = new UpdateCheckResult.Error("GitHub's hourly request limit was reached.");
        var coordinator = Create();

        await coordinator.CheckAsync();

        Assert.Equal(UpdateState.Error, coordinator.State);
        Assert.Equal("GitHub's hourly request limit was reached.", coordinator.ErrorMessage);
        Assert.False(coordinator.CanDownload);
        Assert.True(coordinator.CanCheck);
        Assert.False(coordinator.IsBadgeVisible);
    }

    [Fact]
    public async Task Check_ServiceThrows_BecomesError()
    {
        var coordinator = new UpdateCoordinator(new ThrowingService(), new InMemorySettingsStore(), _time, a => a());

        await coordinator.CheckAsync();

        Assert.Equal(UpdateState.Error, coordinator.State);
        Assert.Equal("boom", coordinator.ErrorMessage);
    }

    [Fact]
    public async Task DownloadFailure_BecomesError_AndCanRetry()
    {
        _service.NextResult = FakeUpdateService.SampleUpdate();
        _service.DownloadFailure = new IOException("disk full");
        var coordinator = Create();

        await coordinator.CheckAsync();
        await coordinator.DownloadAsync();

        Assert.Equal(UpdateState.Error, coordinator.State);
        Assert.Contains("disk full", coordinator.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, coordinator.DownloadProgress);
        Assert.True(coordinator.CanDownload);

        _service.DownloadFailure = null;
        await coordinator.DownloadAsync();
        Assert.Equal(UpdateState.ReadyToInstall, coordinator.State);
    }

    [Fact]
    public async Task CancelDownload_ReturnsToAvailable()
    {
        _service.NextResult = FakeUpdateService.SampleUpdate();
        _service.HoldDownload = true;
        var coordinator = Create();
        await coordinator.CheckAsync();

        var download = coordinator.DownloadAsync();
        Assert.Equal(UpdateState.Downloading, coordinator.State);
        coordinator.Cancel();
        await download;

        Assert.Equal(UpdateState.Available, coordinator.State);
        Assert.Equal(0, coordinator.DownloadProgress);
    }

    [Fact]
    public async Task SkipVersion_HidesBadge_PersistsAndSuppressesAutoDownload()
    {
        _service.NextResult = FakeUpdateService.SampleUpdate("0.2.0");
        var store = new InMemorySettingsStore(new UpdateSettings { AutoDownload = false });
        var coordinator = Create(store: store);
        await coordinator.CheckAsync();
        Assert.True(coordinator.IsBadgeVisible);

        coordinator.SkipAvailableVersion();

        Assert.Equal("0.2.0", store.Current.SkippedVersion);
        Assert.True(coordinator.IsAvailableUpdateSkipped);
        Assert.False(coordinator.IsBadgeVisible);

        // A later session with auto-download on still ignores the skipped version…
        var next = new UpdateCoordinator(_service, new InMemorySettingsStore(store.Current with { AutoDownload = true }), _time, a => a());
        await next.CheckAsync();
        Assert.Equal(UpdateState.Available, next.State);
        Assert.False(next.IsBadgeVisible);
        Assert.Equal(0, _service.DownloadCount);

        // …but a newer version shows up again and auto-downloads.
        _service.NextResult = FakeUpdateService.SampleUpdate("0.2.1");
        await next.CheckAsync();
        Assert.False(next.IsAvailableUpdateSkipped);
        Assert.Equal(UpdateState.ReadyToInstall, next.State);
        Assert.True(next.IsBadgeVisible);
        Assert.Equal(1, _service.DownloadCount);
    }

    [Fact]
    public async Task AutoDownload_DownloadsRightAfterCheck()
    {
        _service.NextResult = FakeUpdateService.SampleUpdate();
        var coordinator = Create(new UpdateSettings { AutoDownload = true });

        await coordinator.CheckAsync();

        Assert.Equal(1, _service.DownloadCount);
        Assert.Equal(UpdateState.ReadyToInstall, coordinator.State);
        Assert.Equal(100, coordinator.DownloadProgress);
    }

    [Fact]
    public async Task ChannelChange_PersistsAndRechecks()
    {
        _service.NextResult = new UpdateCheckResult.UpToDate();
        _service.PrereleaseResult = FakeUpdateService.SampleUpdate("0.3.0-beta.1");
        var store = new InMemorySettingsStore();
        var coordinator = Create(store: store);
        await coordinator.CheckAsync();
        Assert.Equal(UpdateState.UpToDate, coordinator.State);

        await coordinator.SetChannelAsync(UpdateChannel.Prerelease);

        Assert.Equal(UpdateChannel.Prerelease, store.Current.Channel);
        Assert.Equal(UpdateChannel.Prerelease, _service.Channel);
        Assert.Equal([UpdateChannel.Stable, UpdateChannel.Prerelease], _service.CheckedChannels);
        Assert.Equal(UpdateState.Available, coordinator.State);
        Assert.Equal("0.3.0-beta.1", coordinator.AvailableUpdate?.Version);

        await coordinator.SetChannelAsync(UpdateChannel.Stable);
        Assert.Equal(UpdateState.UpToDate, coordinator.State);
        Assert.Null(coordinator.AvailableUpdate);
    }

    [Fact]
    public async Task ChannelChange_DuringCheck_SupersedesTheOldCheck()
    {
        _service.CheckDelay = TimeSpan.FromMilliseconds(200);
        _service.NextResult = new UpdateCheckResult.UpToDate();
        _service.PrereleaseResult = FakeUpdateService.SampleUpdate("0.3.0-beta.1");
        var coordinator = Create();

        var first = coordinator.CheckAsync();
        Assert.Equal(UpdateState.Checking, coordinator.State);
        await coordinator.SetChannelAsync(UpdateChannel.Prerelease);
        await first;

        Assert.Equal([UpdateChannel.Stable, UpdateChannel.Prerelease], _service.CheckedChannels);
        Assert.Equal(UpdateState.Available, coordinator.State);
        Assert.Equal("0.3.0-beta.1", coordinator.AvailableUpdate?.Version);
    }

    [Fact]
    public void Constructor_AppliesSavedChannelToService()
    {
        Create(new UpdateSettings { Channel = UpdateChannel.Prerelease });
        Assert.Equal(UpdateChannel.Prerelease, _service.Channel);
    }

    [Fact]
    public async Task StartupCheck_Disabled_DoesNothing()
    {
        var coordinator = Create(new UpdateSettings { CheckOnStartup = false });

        await coordinator.RunStartupCheckAsync();

        Assert.Equal(0, _service.CheckCount);
        Assert.Equal(UpdateState.Idle, coordinator.State);
    }

    [Fact]
    public async Task StartupCheck_ThrottledByInterval()
    {
        var recent = Create(new UpdateSettings { LastChecked = Start - TimeSpan.FromHours(1) });
        recent.StartupCheckInterval = TimeSpan.FromHours(6);
        await recent.RunStartupCheckAsync();
        Assert.Equal(0, _service.CheckCount);

        var stale = Create(new UpdateSettings { LastChecked = Start - TimeSpan.FromHours(7) });
        stale.StartupCheckInterval = TimeSpan.FromHours(6);
        await stale.RunStartupCheckAsync();
        Assert.Equal(1, _service.CheckCount);
        Assert.Equal(UpdateState.UpToDate, stale.State);

        var never = Create();
        await never.RunStartupCheckAsync();
        Assert.Equal(2, _service.CheckCount);
    }

    [Fact]
    public async Task StartupCheck_PendingDownloadedUpdate_IsReadyToInstall()
    {
        _service.PendingRestartVersion = "0.2.0";
        var coordinator = Create();

        await coordinator.RunStartupCheckAsync();

        Assert.Equal(UpdateState.ReadyToInstall, coordinator.State);
        Assert.Equal("0.2.0", coordinator.AvailableUpdate?.Version);
        Assert.Equal(0, _service.CheckCount);

        coordinator.ApplyOnExitIfReady();
        Assert.Equal(1, _service.ApplyOnExitCount);
    }

    [Fact]
    public async Task ApplyOnlyWhenReady()
    {
        var coordinator = Create();
        coordinator.ApplyAndRestart();
        coordinator.ApplyOnExitIfReady();
        Assert.Equal(0, _service.ApplyAndRestartCount);
        Assert.Equal(0, _service.ApplyOnExitCount);

        _service.NextResult = FakeUpdateService.SampleUpdate();
        await coordinator.CheckAsync();
        coordinator.ApplyAndRestart();
        Assert.Equal(0, _service.ApplyAndRestartCount);
    }

    [Fact]
    public void Preferences_ArePersisted()
    {
        var store = new InMemorySettingsStore();
        var coordinator = Create(store: store);

        coordinator.SetCheckOnStartup(false);
        coordinator.SetAutoDownload(true);

        Assert.False(store.Current.CheckOnStartup);
        Assert.True(store.Current.AutoDownload);
        Assert.Equal(2, store.SaveCount);
    }

    [Fact]
    public async Task SettingsStoreFailure_DoesNotBreakUpdating()
    {
        var coordinator = new UpdateCoordinator(_service, new BrokenStore(), _time, a => a());

        await coordinator.CheckAsync();
        coordinator.SetAutoDownload(true);

        Assert.Equal(UpdateState.UpToDate, coordinator.State);
        Assert.True(coordinator.Settings.AutoDownload);
    }

    private sealed class ThrowingService : IUpdateService
    {
        public string CurrentVersion => "1.0.0";

        public bool IsInstalled => true;

        public UpdateChannel Channel { get; set; }

        public string? PendingRestartVersion => null;

        public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");

        public Task DownloadAsync(Action<int>? progress, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");

        public void ApplyAndRestart() => throw new InvalidOperationException("boom");

        public void ApplyOnExit() => throw new InvalidOperationException("boom");
    }

    private sealed class BrokenStore : ISettingsStore
    {
        public UpdateSettings Load() => new();

        public void Save(UpdateSettings settings) => throw new UnauthorizedAccessException("read-only");
    }
}
