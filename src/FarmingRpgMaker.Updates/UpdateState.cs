namespace FarmingRpgMaker.Updates;

/// <summary>States of <see cref="UpdateCoordinator"/>.</summary>
public enum UpdateState
{
    /// <summary>Nothing checked yet this session.</summary>
    Idle,

    /// <summary>Running from a dev build; updates are unavailable.</summary>
    NotInstalled,

    Checking,
    UpToDate,

    /// <summary><see cref="UpdateCoordinator.AvailableUpdate"/> can be downloaded.</summary>
    Available,

    /// <summary>Download in progress; see <see cref="UpdateCoordinator.DownloadProgress"/>.</summary>
    Downloading,

    /// <summary>Downloaded; restart to install (or it is applied on exit).</summary>
    ReadyToInstall,

    /// <summary>See <see cref="UpdateCoordinator.ErrorMessage"/>.</summary>
    Error,
}
