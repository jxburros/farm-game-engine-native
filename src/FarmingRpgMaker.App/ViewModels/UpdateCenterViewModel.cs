using System.ComponentModel;
using System.Globalization;
using FarmingRpgMaker.App.Mvvm;
using FarmingRpgMaker.App.Services;
using FarmingRpgMaker.Updates;

namespace FarmingRpgMaker.App.ViewModels;

/// <summary>Visual tone of the Update Center status card.</summary>
public enum UpdateStatusTone
{
    Neutral,
    Success,
    Update,
    Warning,
    Error,
}

/// <summary>Presentation of <see cref="UpdateCoordinator"/> for the Update Center window.</summary>
public sealed class UpdateCenterViewModel : ObservableObject, IDisposable
{
    private readonly IUrlLauncher _launcher;
    private readonly TimeProvider _time;

    public UpdateCenterViewModel(UpdateCoordinator coordinator, IUrlLauncher launcher, TimeProvider? timeProvider = null)
    {
        Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _time = timeProvider ?? TimeProvider.System;

        CheckCommand = new AsyncRelayCommand(() => Coordinator.CheckAsync(), () => Coordinator.CanCheck);
        DownloadCommand = new AsyncRelayCommand(() => Coordinator.DownloadAsync(), () => Coordinator.CanDownload);
        RestartCommand = new RelayCommand(Coordinator.ApplyAndRestart, () => Coordinator.CanApply);
        SkipCommand = new RelayCommand(Coordinator.SkipAvailableVersion, () => IsSkipVisible);
        CancelCommand = new RelayCommand(Coordinator.Cancel, () => Coordinator.IsBusy);
        OpenReleasesCommand = new RelayCommand(() => _launcher.Open(UpdateSource.ReleasesPageUrl));
        OpenReleasePageCommand = new RelayCommand(
            () => _launcher.Open(Coordinator.AvailableUpdate?.ReleaseUrl ?? UpdateSource.ReleasesPageUrl));
        OpenLinkCommand = new RelayCommand<string>(url => _launcher.Open(url));

        Coordinator.PropertyChanged += OnCoordinatorChanged;
    }

    public UpdateCoordinator Coordinator { get; }

    public string CurrentVersionText => $"Farming RPG Maker {Coordinator.CurrentVersion}";

    public UpdateState State => Coordinator.State;

    // ---- Channel & preferences -------------------------------------------------------

    public bool IsStableChannel
    {
        get => Coordinator.Channel == UpdateChannel.Stable;
        set
        {
            if (value)
            {
                _ = Coordinator.SetChannelAsync(UpdateChannel.Stable);
            }
        }
    }

    public bool IsPrereleaseChannel
    {
        get => Coordinator.Channel == UpdateChannel.Prerelease;
        set
        {
            if (value)
            {
                _ = Coordinator.SetChannelAsync(UpdateChannel.Prerelease);
            }
        }
    }

    public string ChannelText => Coordinator.Channel == UpdateChannel.Prerelease ? "Pre-release channel" : "Stable channel";

    public bool CheckOnStartup
    {
        get => Coordinator.Settings.CheckOnStartup;
        set => Coordinator.SetCheckOnStartup(value);
    }

    public bool AutoDownload
    {
        get => Coordinator.Settings.AutoDownload;
        set => Coordinator.SetAutoDownload(value);
    }

    public string LastCheckedText => Coordinator.LastChecked is { } last
        ? $"Last checked {FormatWhen(last)}"
        : "Not checked yet";

    // ---- Status card -----------------------------------------------------------------

    public UpdateStatusTone StatusTone => Coordinator.State switch
    {
        UpdateState.UpToDate => UpdateStatusTone.Success,
        UpdateState.Available or UpdateState.Downloading or UpdateState.ReadyToInstall => UpdateStatusTone.Update,
        UpdateState.NotInstalled => UpdateStatusTone.Warning,
        UpdateState.Error => UpdateStatusTone.Error,
        _ => UpdateStatusTone.Neutral,
    };

    public bool IsToneNeutral => StatusTone == UpdateStatusTone.Neutral;

    public bool IsToneSuccess => StatusTone == UpdateStatusTone.Success;

    public bool IsToneUpdate => StatusTone == UpdateStatusTone.Update;

    public bool IsToneWarning => StatusTone == UpdateStatusTone.Warning;

    public bool IsToneError => StatusTone == UpdateStatusTone.Error;

    public string StatusTitle => Coordinator.State switch
    {
        UpdateState.NotInstalled => "Updates are available only in the installed app",
        UpdateState.Checking => "Checking for updates…",
        UpdateState.UpToDate => "You're up to date",
        UpdateState.Available => $"Version {AvailableVersion} is available",
        UpdateState.Downloading => $"Downloading version {AvailableVersion}…",
        UpdateState.ReadyToInstall => $"Version {AvailableVersion} is ready to install",
        UpdateState.Error => "Something went wrong",
        _ => "Check for updates",
    };

    public string StatusDetail => Coordinator.State switch
    {
        UpdateState.NotInstalled =>
            $"You're running a development build ({Coordinator.CurrentVersion}). Install Farming RPG Maker with Setup.exe from GitHub Releases to get automatic updates.",
        UpdateState.Checking => $"Looking at GitHub Releases on the {ChannelName} channel.",
        UpdateState.UpToDate => $"Farming RPG Maker {Coordinator.CurrentVersion} is the newest {ChannelName} version.",
        UpdateState.Available when Coordinator.IsAvailableUpdateSkipped =>
            $"You chose to skip this version. You're on {Coordinator.CurrentVersion}; you can still download it any time.",
        UpdateState.Available => $"You have {Coordinator.CurrentVersion}. You can keep working while the update downloads.",
        UpdateState.Downloading => "You can keep working while the update downloads.",
        UpdateState.ReadyToInstall =>
            "Restart Farming RPG Maker to finish updating. If you don't, the update is installed when you close the app.",
        UpdateState.Error => Coordinator.ErrorMessage ?? "The update check failed.",
        _ => "Updates are published on GitHub Releases. Check now to see whether a newer version is out.",
    };

    // ---- Update details --------------------------------------------------------------

    public bool HasUpdate => Coordinator.AvailableUpdate is not null
        && Coordinator.State is UpdateState.Available or UpdateState.Downloading or UpdateState.ReadyToInstall;

    public string AvailableVersion => Coordinator.AvailableUpdate?.Version ?? "";

    public string ReleaseNotesTitle => $"Release notes · {AvailableVersion}";

    public string ReleaseNotesMarkdown => Coordinator.AvailableUpdate?.ReleaseNotesMarkdown ?? "";

    public bool HasReleaseNotes => HasUpdate && !string.IsNullOrWhiteSpace(ReleaseNotesMarkdown);

    public bool HasNoReleaseNotes => HasUpdate && string.IsNullOrWhiteSpace(ReleaseNotesMarkdown);

    public string ReleaseMetaText
    {
        get
        {
            var update = Coordinator.AvailableUpdate;
            if (update is null)
            {
                return "";
            }

            var parts = new List<string>();
            if (update.PublishedAt is { } published)
            {
                parts.Add("Released " + published.ToLocalTime().ToString("MMMM d, yyyy", CultureInfo.InvariantCulture));
            }

            if (update.SizeBytes is { } size)
            {
                parts.Add(FormatSize(size));
            }

            return string.Join("  ·  ", parts);
        }
    }

    // ---- Download --------------------------------------------------------------------

    public int DownloadProgress => Coordinator.DownloadProgress;

    public bool IsDownloading => Coordinator.State == UpdateState.Downloading;

    public string ProgressText => $"{Coordinator.DownloadProgress}% downloaded";

    // ---- Buttons ---------------------------------------------------------------------

    public bool IsNotInstalled => Coordinator.State == UpdateState.NotInstalled;

    public bool IsCheckVisible => Coordinator.State is not (UpdateState.NotInstalled or UpdateState.Downloading or UpdateState.ReadyToInstall);

    public bool IsCheckEnabled => Coordinator.CanCheck;

    public bool IsDownloadVisible => Coordinator.CanDownload;

    public bool IsRestartVisible => Coordinator.CanApply;

    public bool IsSkipVisible => Coordinator.State == UpdateState.Available && !Coordinator.IsAvailableUpdateSkipped;

    public bool IsCancelVisible => Coordinator.State == UpdateState.Downloading;

    public AsyncRelayCommand CheckCommand { get; }

    public AsyncRelayCommand DownloadCommand { get; }

    public RelayCommand RestartCommand { get; }

    public RelayCommand SkipCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand OpenReleasesCommand { get; }

    public RelayCommand OpenReleasePageCommand { get; }

    /// <summary>Opens a link from the release notes (the launcher allows http/https only).</summary>
    public RelayCommand<string> OpenLinkCommand { get; }

    public void Dispose() => Coordinator.PropertyChanged -= OnCoordinatorChanged;

    private string ChannelName => Coordinator.Channel == UpdateChannel.Prerelease ? "pre-release" : "stable";

    private void OnCoordinatorChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(UpdateCoordinator.DownloadProgress):
                OnPropertiesChanged(nameof(DownloadProgress), nameof(ProgressText));
                return;
            case nameof(UpdateCoordinator.Settings):
            case nameof(UpdateCoordinator.Channel):
            case nameof(UpdateCoordinator.LastChecked):
                OnPropertiesChanged(
                    nameof(IsStableChannel),
                    nameof(IsPrereleaseChannel),
                    nameof(ChannelText),
                    nameof(CheckOnStartup),
                    nameof(AutoDownload),
                    nameof(LastCheckedText));
                break;
        }

        OnPropertiesChanged(
            nameof(State),
            nameof(StatusTone),
            nameof(IsToneNeutral),
            nameof(IsToneSuccess),
            nameof(IsToneUpdate),
            nameof(IsToneWarning),
            nameof(IsToneError),
            nameof(StatusTitle),
            nameof(StatusDetail),
            nameof(HasUpdate),
            nameof(AvailableVersion),
            nameof(ReleaseNotesTitle),
            nameof(ReleaseNotesMarkdown),
            nameof(HasReleaseNotes),
            nameof(HasNoReleaseNotes),
            nameof(ReleaseMetaText),
            nameof(IsDownloading),
            nameof(IsNotInstalled),
            nameof(IsCheckVisible),
            nameof(IsCheckEnabled),
            nameof(IsDownloadVisible),
            nameof(IsRestartVisible),
            nameof(IsSkipVisible),
            nameof(IsCancelVisible));
        CheckCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
        SkipCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private string FormatWhen(DateTimeOffset when)
    {
        var local = when.ToLocalTime();
        var now = _time.GetUtcNow().ToLocalTime();
        var ago = now - local;
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
        if (local.Date == now.Date)
        {
            return $"today at {time}";
        }

        if (local.Date == now.Date.AddDays(-1))
        {
            return $"yesterday at {time}";
        }

        return local.ToString("MMM d, yyyy", CultureInfo.InvariantCulture) + " at " + time;
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1_000_000_000 => (bytes / 1_000_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1_000_000 => (bytes / 1_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        >= 1_000 => (bytes / 1_000d).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " bytes",
    };
}
