namespace FarmingRpgMaker.App.Tests;

/// <summary>
/// Controllable clock. Its timers (<see cref="TimeProvider.CreateTimer"/>, which
/// <c>Task.Delay</c> and the autosave debounce use) fire only when <see cref="Advance"/> moves
/// the clock past them, on the thread that calls it.
/// </summary>
public sealed class TestTime(DateTimeOffset start) : TimeProvider
{
    private readonly List<FakeTimer> _timers = [];

    public DateTimeOffset Now { get; set; } = start;

    /// <summary>Timers that are armed and have not fired yet.</summary>
    public int PendingTimers => _timers.Count(timer => timer.Due is not null);

    public override DateTimeOffset GetUtcNow() => Now;

    /// <summary>Moves the clock forward, firing every timer that comes due on the way, in order.</summary>
    public void Advance(TimeSpan by)
    {
        var end = Now + by;
        while (_timers.Where(timer => timer.Due <= end).OrderBy(timer => timer.Due).FirstOrDefault() is { } due)
        {
            Now = due.Due!.Value;
            due.Fire();
        }

        Now = end;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        timer.Change(dueTime, period);
        _timers.Add(timer);
        return timer;
    }

    private sealed class FakeTimer(TestTime time, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period;

        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : time.Now + dueTime;
            _period = period;
            return true;
        }

        public void Fire()
        {
            Due = _period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan ? Due + _period : null;
            callback(state);
        }

        public void Dispose()
        {
            Due = null;
            time._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
