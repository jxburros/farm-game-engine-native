using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FarmingRpgMaker.App.Controls;
using FarmingRpgMaker.App.ViewModels;
using FarmingRpgMaker.App.Views;
using FarmingRpgMaker.Updates;
using FarmingRpgMaker.Updates.Testing;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Ui;

public sealed class UpdateCenterWindowTests
{
    private readonly FakeUpdateService _fake = new();
    private readonly RecordingUrlLauncher _launcher = new();
    private readonly TestTime _time = new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
    private InMemorySettingsStore _store = new();

    private (UpdateCenterWindow Window, UpdateCoordinator Coordinator) Open(UpdateSettings? settings = null)
    {
        _store = new InMemorySettingsStore(settings);
        var coordinator = new UpdateCoordinator(_fake, _store, _time);
        var window = new UpdateCenterWindow { DataContext = new UpdateCenterViewModel(coordinator, _launcher, _time) };
        window.Show();
        Pump();
        return (window, coordinator);
    }

    [AvaloniaFact]
    public void Idle_ShowsVersionChannelAndCheckButton()
    {
        _fake.CurrentVersion = "0.1.0";
        var (window, _) = Open();

        Assert.Equal("Farming RPG Maker 0.1.0", Find<TextBlock>(window, "CurrentVersionText").Text);
        Assert.Equal("Stable channel", Find<TextBlock>(window, "ChannelText").Text);
        Assert.Equal("Check for updates", Find<TextBlock>(window, "StatusTitle").Text);
        Assert.Equal("Not checked yet", Find<TextBlock>(window, "LastCheckedText").Text);
        Assert.True(Find<Button>(window, "CheckButton").IsEffectivelyEnabled);
        Assert.False(Find<Button>(window, "InstallerButton").IsVisible);
        Assert.False(Find<Button>(window, "DownloadButton").IsVisible);
        Assert.False(Find<Button>(window, "RestartButton").IsVisible);
        Assert.False(Find<Border>(window, "ReleaseNotesCard").IsVisible);
        Assert.True(Find<CheckBox>(window, "CheckOnStartupOption").IsChecked);
        Assert.False(Find<CheckBox>(window, "AutoDownloadOption").IsChecked);
        Assert.True(Find<RadioButton>(window, "StableChannelOption").IsChecked);
        Assert.Equal("View all releases on GitHub", Find<HyperlinkButton>(window, "AllReleasesLink").Content);
    }

    [AvaloniaFact]
    public void NotInstalled_ShowsInlineExplanation_AndDisablesCheck()
    {
        _fake.IsInstalled = false;
        _fake.CurrentVersion = "0.1.0-dev";
        var (window, _) = Open();

        Assert.Equal("Updates are available only in the installed app", Find<TextBlock>(window, "StatusTitle").Text);
        Assert.Contains("development build (0.1.0-dev)", Find<TextBlock>(window, "StatusDetail").Text, StringComparison.Ordinal);
        Assert.Contains("warning", Find<Border>(window, "StatusCard").Classes);
        Assert.False(Find<Button>(window, "CheckButton").IsVisible);
        Assert.False(Find<Button>(window, "DownloadButton").IsVisible);
        Assert.False(Find<TextBlock>(window, "LastCheckedText").IsVisible);

        Click(window, Find<Button>(window, "InstallerButton"));
        Assert.Equal([UpdateSource.ReleasesPageUrl], _launcher.Opened);
        Assert.Equal(0, _fake.CheckCount);
    }

    [AvaloniaFact]
    public void CheckButton_UpToDate()
    {
        _fake.CurrentVersion = "0.2.0";
        var (window, coordinator) = Open();

        Click(window, Find<Button>(window, "CheckButton"));
        PumpUntil(() => coordinator.State == UpdateState.UpToDate, "up to date");

        Assert.Equal("You're up to date", Find<TextBlock>(window, "StatusTitle").Text);
        Assert.Contains("0.2.0 is the newest stable version", Find<TextBlock>(window, "StatusDetail").Text, StringComparison.Ordinal);
        Assert.Equal("Last checked just now", Find<TextBlock>(window, "LastCheckedText").Text);
        Assert.Contains("success", Find<Border>(window, "StatusCard").Classes);
        Assert.False(Find<Button>(window, "DownloadButton").IsVisible);
    }

    [AvaloniaFact]
    public void Error_ShowsMessage_AndAllowsRetry()
    {
        _fake.NextResult = new UpdateCheckResult.Error("Couldn't reach GitHub. Are you offline?");
        var (window, coordinator) = Open();

        Click(window, Find<Button>(window, "CheckButton"));
        PumpUntil(() => coordinator.State == UpdateState.Error, "error");

        Assert.Equal("Something went wrong", Find<TextBlock>(window, "StatusTitle").Text);
        Assert.Equal("Couldn't reach GitHub. Are you offline?", Find<TextBlock>(window, "StatusDetail").Text);
        Assert.Contains("error", Find<Border>(window, "StatusCard").Classes);
        Assert.True(Find<Button>(window, "CheckButton").IsEffectivelyEnabled);

        _fake.NextResult = new UpdateCheckResult.UpToDate();
        Click(window, Find<Button>(window, "CheckButton"));
        PumpUntil(() => coordinator.State == UpdateState.UpToDate, "retry");
    }

    [AvaloniaFact]
    public void Available_ShowsVersionNotesAndActions()
    {
        _fake.NextResult = FakeUpdateService.SampleUpdate("0.2.0");
        var (window, coordinator) = Open();

        Click(window, Find<Button>(window, "CheckButton"));
        PumpUntil(() => coordinator.State == UpdateState.Available, "available");

        Assert.Equal("Version 0.2.0 is available", Find<TextBlock>(window, "StatusTitle").Text);
        Assert.True(Find<Button>(window, "DownloadButton").IsVisible);
        Assert.True(Find<Button>(window, "SkipButton").IsVisible);
        Assert.True(Find<Border>(window, "ReleaseNotesCard").IsVisible);
        Assert.Equal("Release notes · 0.2.0", Find<TextBlock>(window, "ReleaseNotesTitle").Text);

        var notes = AllVisibleText(Find<MarkdownView>(window, "ReleaseNotesView"));
        Assert.Contains("What's new in 0.2.0", notes, StringComparison.Ordinal);
        Assert.Contains("Fishing mini-game with 12 new fish", notes, StringComparison.Ordinal);
        Assert.Contains("Fixes", notes, StringComparison.Ordinal);
        Assert.Contains("Download 0.2.0", AllVisibleText(window), StringComparison.Ordinal);
        Assert.Contains("Released September 20, 2026", AllVisibleText(window), StringComparison.Ordinal);
        Assert.Contains("48.7 MB", AllVisibleText(window), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void ClickingDownload_DrivesProgress_ThenRestartInstalls()
    {
        _fake.NextResult = FakeUpdateService.SampleUpdate("0.2.0");
        _fake.DownloadSteps = [0, 40, 100];
        _fake.HoldDownload = true;
        var (window, coordinator) = Open();
        Click(window, Find<Button>(window, "CheckButton"));
        PumpUntil(() => coordinator.State == UpdateState.Available, "available");

        Click(window, Find<Button>(window, "DownloadButton"));
        PumpUntil(() => coordinator.State == UpdateState.Downloading, "downloading");

        var bar = Find<ProgressBar>(window, "DownloadProgressBar");
        Assert.True(bar.IsEffectivelyVisible);
        Assert.Equal(0, bar.Value);
        Assert.Equal("Downloading version 0.2.0…", Find<TextBlock>(window, "StatusTitle").Text);
        Assert.True(Find<Button>(window, "CancelButton").IsVisible);
        Assert.False(Find<Button>(window, "CheckButton").IsVisible);
        Assert.False(Find<Button>(window, "DownloadButton").IsVisible);

        _fake.ReleaseDownload();
        PumpUntil(() => coordinator.State == UpdateState.ReadyToInstall, "ready");

        Assert.Equal(100, bar.Value);
        Assert.False(bar.IsEffectivelyVisible);
        Assert.Equal("Version 0.2.0 is ready to install", Find<TextBlock>(window, "StatusTitle").Text);
        var restart = Find<Button>(window, "RestartButton");
        Assert.True(restart.IsVisible);
        Assert.Equal("Restart & install", restart.Content);

        Click(window, restart);
        Assert.Equal(1, _fake.ApplyAndRestartCount);
    }

    [AvaloniaFact]
    public void DownloadProgress_UpdatesProgressBarWhileRunning()
    {
        _fake.NextResult = FakeUpdateService.SampleUpdate("0.2.0");
        _fake.DownloadSteps = [35, 70, 100];
        _fake.HoldDownload = true;
        var (window, coordinator) = Open();
        Click(window, Find<Button>(window, "CheckButton"));
        PumpUntil(() => coordinator.State == UpdateState.Available, "available");

        Click(window, Find<Button>(window, "DownloadButton"));
        PumpUntil(() => Find<ProgressBar>(window, "DownloadProgressBar").Value == 35, "35%");
        Assert.Equal("35% downloaded", Find<TextBlock>(window, "ProgressText").Text);

        _fake.ReleaseDownload();
        PumpUntil(() => coordinator.State == UpdateState.ReadyToInstall, "ready");
    }

    [AvaloniaFact]
    public void SkipThisVersion_HidesSkipAndExplains()
    {
        _fake.NextResult = FakeUpdateService.SampleUpdate("0.2.0");
        var (window, coordinator) = Open();
        Click(window, Find<Button>(window, "CheckButton"));
        PumpUntil(() => coordinator.State == UpdateState.Available, "available");

        Click(window, Find<Button>(window, "SkipButton"));

        Assert.Equal("0.2.0", _store.Current.SkippedVersion);
        Assert.False(Find<Button>(window, "SkipButton").IsVisible);
        Assert.Contains("chose to skip", Find<TextBlock>(window, "StatusDetail").Text, StringComparison.Ordinal);
        Assert.True(Find<Button>(window, "DownloadButton").IsVisible);
        Assert.False(coordinator.IsBadgeVisible);
    }

    [AvaloniaFact]
    public void ChoosingPrereleaseChannel_SavesAndRechecks()
    {
        _fake.PrereleaseResult = FakeUpdateService.SampleUpdate("0.3.0-beta.1");
        var (window, coordinator) = Open();

        Click(window, Find<RadioButton>(window, "PrereleaseChannelOption"));
        PumpUntil(() => coordinator.State == UpdateState.Available, "pre-release found");

        Assert.Equal(UpdateChannel.Prerelease, _store.Current.Channel);
        Assert.Equal([UpdateChannel.Prerelease], _fake.CheckedChannels);
        Assert.Equal("Pre-release channel", Find<TextBlock>(window, "ChannelText").Text);
        Assert.Equal("Version 0.3.0-beta.1 is available", Find<TextBlock>(window, "StatusTitle").Text);
    }

    [AvaloniaFact]
    public void PreferenceCheckboxes_ArePersisted()
    {
        var (window, _) = Open();

        Click(window, Find<CheckBox>(window, "CheckOnStartupOption"));
        Click(window, Find<CheckBox>(window, "AutoDownloadOption"));

        Assert.False(_store.Current.CheckOnStartup);
        Assert.True(_store.Current.AutoDownload);
    }

    [AvaloniaFact]
    public void ReleasesLink_OpensGitHub()
    {
        var (window, _) = Open();

        Click(window, Find<HyperlinkButton>(window, "AllReleasesLink"));

        Assert.Equal([UpdateSource.ReleasesPageUrl], _launcher.Opened);
    }
}
