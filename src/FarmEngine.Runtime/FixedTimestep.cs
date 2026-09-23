using FarmEngine.Core;

namespace FarmEngine.Runtime;

/// <summary>
/// Fixed-timestep accumulator (port of fixed-timestep.ts): converts variable
/// frame deltas into a whole number of simulation ticks (rendering
/// interpolates between them). This decouples simulation determinism from
/// display refresh rate.
/// </summary>
public sealed class FixedTimestep
{
    private readonly double _msPerTick;
    private double _accumulatorMs;

    public FixedTimestep(double msPerTick = Engine.MsPerTick)
    {
        _msPerTick = msPerTick;
    }

    /// <summary>Feed a frame delta (seconds); returns how many whole ticks to simulate.</summary>
    public int Advance(double deltaSeconds)
    {
        // Cap to avoid the spiral of death after a background tab wakes up.
        _accumulatorMs += Math.Min(deltaSeconds, 0.25) * 1000;
        var ticks = Math.Floor(_accumulatorMs / _msPerTick);
        _accumulatorMs -= ticks * _msPerTick;
        return (int)ticks;
    }

    /// <summary>Interpolation alpha within the current tick, for renderers.</summary>
    public double Alpha => _accumulatorMs / _msPerTick;

    public void Reset() => _accumulatorMs = 0;
}
