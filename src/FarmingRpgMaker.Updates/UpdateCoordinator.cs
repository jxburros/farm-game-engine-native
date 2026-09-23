using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FarmingRpgMaker.Updates;

/// <summary>
/// UI-independent update state machine shared by the header badge and the Update Center:
/// <c>Idle/NotInstalled → Checking → UpToDate | Available | Error</c>,
/// <c>Available → Downloading(progress) → ReadyToInstall | Error</c>.
/// Construct it on the UI thread: property-change notifications (including download
/// progress coming from background threads) are raised through the
/// <see cref="SynchronizationContext"/> captured at construction. Public async methods
/// never throw; failures become <see cref="UpdateState.Error"/>.
/// </summary>
public sealed class UpdateCoordinator : INotifyPropertyChanged
{
    /// <summary>Default minimum time between automatic startup checks.</summary>
    public static readonly TimeSpan DefaultStartupCheckInterval = TimeSpan.FromHours(6);

    private readonly IUpdateService _service;
    private readonly ISettingsStore _store;
    private readonly TimeProvider _time;
    private readonly Action<Action> _dispatch;
    private CancellationTokenSource? _operation;

    private UpdateState _state;
    private int _downloadProgress;
    private UpdateCheckResult.UpdateAvailable? _availableUpdate;
    private string? _errorMessage;
    private UpdateSettings _settings;

    public UpdateCoordinator(
        IUpdateService service,
        ISettingsStore store,
        TimeProvider? timeProvider = null,
        Action<Action>? dispatch = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _time = timeProvider ?? TimeProvider.System;
        _dispatch = dispatch ?? CreateDefaultDispatcher();
        _settings = _store.Load();
        _service.Channel = _settings.Channel;
        _state = _service.IsInstalled ? UpdateState.Idle : UpdateState.NotInstalled;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public UpdateState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                RaiseDerived();
            }
        }
    }

    /// <summary>0–100 while <see cref="State"/> is <see cref="UpdateState.Downloading"/>.</summary>
    public int DownloadProgress
    {
        get => _downloadProgress;
        private set
        {
            if (Set(ref _downloadProgress, value))
            {
                OnPropertyChanged(nameof(BadgeText));
            }
        }
    }

    /// <summary>The newest release found by the last check, if any.</summary>
    public UpdateCheckResult.UpdateAvailable? AvailableUpdate
    {
        get => _availableUpdate;
        private set
        {
            if (Set(ref _availableUpdate, value))
            {
                RaiseDerived();
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => Set(ref _errorMessage, value);
    }

    public UpdateSettings Settings
    {
        get => _settings;
        private set
        {
            if (Set(ref _settings, value))
            {
                OnPropertyChanged(nameof(Channel));
                OnPropertyChanged(nameof(LastChecked));
                RaiseDerived();
            }
        }
    }

    public string CurrentVersion => _service.CurrentVersion;

    public bool IsInstalled => _service.IsInstalled;

    public UpdateChannel Channel => _settings.Channel;

    public DateTimeOffset? LastChecked => _settings.LastChecked;

    /// <summary>Minimum time between automatic checks at startup.</summary>
    public TimeSpan StartupCheckInterval { get; set; } = DefaultStartupCheckInterval;

    /// <summary>True while checking or downloading.</summary>
    public bool IsBusy => _state is UpdateState.Checking or UpdateState.Downloading;

    /// <summary>The available update is the one the user chose to skip.</summary>
    public bool IsAvailableUpdateSkipped =>
        _availableUpdate is not null
        && string.Equals(_settings.SkippedVersion, _availableUpdate.Version, StringComparison.OrdinalIgnoreCase);

    public bool CanCheck => IsInstalled && !IsBusy;

    public bool CanDownload => _availableUpdate is not null && _state is UpdateState.Available or UpdateState.Error;

    public bool CanApply => _state == UpdateState.ReadyToInstall;

    /// <summary>Show the "Update available" badge in the main window header.</summary>
    public bool IsBadgeVisible => _state switch
    {
        UpdateState.Available => !IsAvailableUpdateSkipped,
        UpdateState.Downloading or UpdateState.ReadyToInstall => true,
        _ => false,
    };

    public string BadgeText => _state switch
    {
        UpdateState.Downloading => $"Downloading update… {_downloadProgress}%",
        UpdateState.ReadyToInstall => "Restart to update",
        _ => "Update available",
    };

    /// <summary>
    /// Startup hook: restores a pending downloaded update, then checks in the background when
    /// <see cref="UpdateSettings.CheckOnStartup"/> is on and the last check is older than
    /// <see cref="StartupCheckInterval"/>. Never throws.
    /// </summary>
    public async Task RunStartupCheckAsync(CancellationToken cancellationToken = default)
    {
        if (!IsInstalled)
        {
            State = UpdateState.NotInstalled;
            return;
        }

        string? pending;
        try
        {
            pending = _service.PendingRestartVersion;
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            pending = null;
        }

        if (pending is not null)
        {
            AvailableUpdate = new UpdateCheckResult.UpdateAvailable(pending, "");
            DownloadProgress = 100;
            State = UpdateState.ReadyToInstall;
            return;
        }

        if (!_settings.CheckOnStartup)
        {
            return;
        }

        if (_settings.LastChecked is { } last && _time.GetUtcNow() - last < StartupCheckInterval)
        {
            return;
        }

        await CheckAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Checks for updates on the current channel, then auto-downloads when
    /// <see cref="UpdateSettings.AutoDownload"/> is on and the version isn't skipped. Never throws.
    /// </summary>
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!IsInstalled)
        {
            State = UpdateState.NotInstalled;
            return;
        }

        if (IsBusy)
        {
            return;
        }

        using var operation = BeginOperation(cancellationToken);
        ErrorMessage = null;
        State = UpdateState.Checking;
        UpdateCheckResult? result;
        try
        {
            result = await _service.CheckAsync(operation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            result = null;
        }
#pragma warning disable CA1031 // The contract is "never throws"; surface everything as an error state.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            result = new UpdateCheckResult.Error(ex.Message);
        }

        if (!EndOperation(operation))
        {
            return; // Superseded (e.g. the channel changed mid-check); the newer operation owns the state.
        }

        if (result is null)
        {
            State = _availableUpdate is null ? UpdateState.Idle : UpdateState.Available;
            return;
        }

        UpdateSettings(_settings with { LastChecked = _time.GetUtcNow() });

        switch (result)
        {
            case UpdateCheckResult.UpToDate:
                AvailableUpdate = null;
                State = UpdateState.UpToDate;
                break;

            case UpdateCheckResult.UpdateAvailable available:
                AvailableUpdate = available;
                State = UpdateState.Available;
                if (_settings.AutoDownload && !IsAvailableUpdateSkipped)
                {
                    await DownloadAsync(cancellationToken).ConfigureAwait(true);
                }

                break;

            case UpdateCheckResult.Error error:
                AvailableUpdate = null;
                ErrorMessage = error.Message;
                State = UpdateState.Error;
                break;
        }
    }

    /// <summary>Downloads <see cref="AvailableUpdate"/>, reporting <see cref="DownloadProgress"/>. Never throws.</summary>
    public async Task DownloadAsync(CancellationToken cancellationToken = default)
    {
        if (!CanDownload)
        {
            return;
        }

        using var operation = BeginOperation(cancellationToken);
        ErrorMessage = null;
        DownloadProgress = 0;
        State = UpdateState.Downloading;
        Exception? failure = null;
        var cancelled = false;
        try
        {
            await _service.DownloadAsync(
                p => _dispatch(() =>
                {
                    if (_state == UpdateState.Downloading && ReferenceEquals(_operation, operation))
                    {
                        DownloadProgress = Math.Clamp(p, 0, 100);
                    }
                }),
                operation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = ex;
        }

        if (!EndOperation(operation))
        {
            return;
        }

        if (cancelled)
        {
            DownloadProgress = 0;
            State = UpdateState.Available;
        }
        else if (failure is not null)
        {
            DownloadProgress = 0;
            ErrorMessage = $"The download failed: {failure.Message}";
            State = UpdateState.Error;
        }
        else
        {
            DownloadProgress = 100;
            State = UpdateState.ReadyToInstall;
        }
    }

    /// <summary>Cancels a running check or download.</summary>
    public void Cancel() => _operation?.Cancel();

    /// <summary>Remembers <see cref="AvailableUpdate"/> as skipped: no badge or auto-download for it.</summary>
    public void SkipAvailableVersion()
    {
        if (_availableUpdate is null)
        {
            return;
        }

        UpdateSettings(_settings with { SkippedVersion = _availableUpdate.Version });
    }

    /// <summary>Exits, installs the downloaded update and relaunches.</summary>
    public void ApplyAndRestart()
    {
        if (!CanApply)
        {
            return;
        }

        try
        {
            _service.ApplyAndRestart();
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = $"The update couldn't be installed: {ex.Message}";
            State = UpdateState.Error;
        }
    }

    /// <summary>Call on app shutdown: installs a downloaded update silently after exit.</summary>
    public void ApplyOnExitIfReady()
    {
        if (!CanApply)
        {
            return;
        }

        try
        {
            _service.ApplyOnExit();
        }
#pragma warning disable CA1031 // Shutdown must not fail because of the updater.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>Switches channel, saves it and re-checks (when installed). Never throws.</summary>
    public async Task SetChannelAsync(UpdateChannel channel, CancellationToken cancellationToken = default)
    {
        if (channel == _settings.Channel)
        {
            return;
        }

        Cancel();
        UpdateSettings(_settings with { Channel = channel });
        _service.Channel = channel;
        AvailableUpdate = null;
        DownloadProgress = 0;
        if (!IsInstalled)
        {
            State = UpdateState.NotInstalled;
            return;
        }

        State = UpdateState.Idle;
        await CheckAsync(cancellationToken).ConfigureAwait(true);
    }

    public void SetCheckOnStartup(bool enabled) => UpdateSettings(_settings with { CheckOnStartup = enabled });

    public void SetAutoDownload(bool enabled) => UpdateSettings(_settings with { AutoDownload = enabled });

    private void UpdateSettings(UpdateSettings settings)
    {
        Settings = settings;
        try
        {
            _store.Save(settings);
        }
#pragma warning disable CA1031 // A read-only profile folder should not break updating.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private CancellationTokenSource BeginOperation(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _operation = cts;
        return cts;
    }

    /// <summary>Clears <paramref name="cts"/> as the current operation; false if a newer one replaced it.</summary>
    private bool EndOperation(CancellationTokenSource cts)
    {
        if (!ReferenceEquals(_operation, cts))
        {
            return false;
        }

        _operation = null;
        return true;
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(IsAvailableUpdateSkipped));
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(IsBadgeVisible));
        OnPropertyChanged(nameof(BadgeText));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static Action<Action> CreateDefaultDispatcher()
    {
        var context = SynchronizationContext.Current;
        if (context is null)
        {
            return action => action();
        }

        return action =>
        {
            if (SynchronizationContext.Current == context)
            {
                action();
            }
            else
            {
                context.Post(_ => action(), null);
            }
        };
    }
}
