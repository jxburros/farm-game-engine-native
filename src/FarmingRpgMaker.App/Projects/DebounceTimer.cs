using Avalonia.Threading;

namespace FarmingRpgMaker.App.Projects;

/// <summary>
/// A restartable one-shot timer whose action runs on the UI thread: <see cref="Start"/> (re)arms
/// it for the full delay, <see cref="Stop"/> disarms it. It runs on a <see cref="TimeProvider"/>
/// rather than a DispatcherTimer, so tests can drive the autosave debounce with a fake clock.
/// UI-thread only, like <see cref="ProjectWorkspace"/>.
/// </summary>
internal sealed class DebounceTimer(TimeProvider time, TimeSpan delay, Action action)
{
    private ITimer? _timer;

    /// <summary>Counts every arm and disarm: a tick from an earlier arm finds it changed and does nothing.</summary>
    private int _generation;

    /// <summary>True while armed and not yet fired.</summary>
    public bool IsRunning => _timer is not null;

    /// <summary>Arms the timer to run the action once, <c>delay</c> from now, replacing an earlier arm.</summary>
    public void Start()
    {
        Stop();
        var generation = _generation;
        // The provider's timers tick on a worker thread (or on the caller's, for a fake clock);
        // the action always runs on the UI thread, and only if nothing re-armed or stopped it since.
        _timer = time.CreateTimer(_ => Dispatcher.UIThread.Post(() => Fire(generation)), null, delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Disarms the timer; a tick already on its way to the UI thread is dropped.</summary>
    public void Stop()
    {
        _generation++;
        _timer?.Dispose();
        _timer = null;
    }

    private void Fire(int generation)
    {
        if (generation != _generation)
        {
            return;
        }

        Stop();
        action();
    }
}
