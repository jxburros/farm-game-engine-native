namespace FarmingRpgMaker.App.Tests;

/// <summary>Controllable clock.</summary>
public sealed class TestTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}
