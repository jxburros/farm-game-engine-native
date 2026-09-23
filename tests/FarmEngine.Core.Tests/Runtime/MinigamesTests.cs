using System.Text.Json;
using FarmEngine.Json;
using FarmEngine.Runtime;
using FarmEngine.Schemas;

namespace FarmEngine.Core.Tests.Runtime;

/// <summary>Port of minigames.test.ts (+ the timing-bar model, which TS drives through the DOM).</summary>
public class MinigamesTests
{
    private static MinigameDef Def(string kind) => new() { Id = "x", Name = "X", Kind = kind, Config = [], ResultTiers = [] };

    [Fact]
    public void ShipsTimingBarAndResolvesRegisteredKinds()
    {
        var registry = Minigames.CreateDefaultMinigameRegistry();
        Assert.Same(Minigames.TimingBar, registry.Get("timing-bar"));
        Assert.Same(Minigames.TimingBar, Minigames.MinigameImplFor(registry, Def("timing-bar")));
    }

    private sealed class Custom : IMinigameImpl
    {
        public IMinigameSession Mount(MinigameMountOptions options) => throw new NotSupportedException();
    }

    [Fact]
    public void LetsGameCodeRegisterCustomKinds()
    {
        var registry = new MinigameRegistry();
        var custom = new Custom();
        registry.Register("rhythm", custom);
        Assert.Same(custom, Minigames.MinigameImplFor(registry, Def("rhythm")));
    }

    [Fact]
    public void FallsBackToTheNeutralImplementationForUnknownKindsAndMissingDefs()
    {
        var registry = Minigames.CreateDefaultMinigameRegistry();
        Assert.Same(Minigames.Fallback, Minigames.MinigameImplFor(registry, Def("unregistered")));
        Assert.Same(Minigames.Fallback, Minigames.MinigameImplFor(registry, null));
    }

    // --- host-agnostic session models ---

    private static (IMinigameSession Session, List<double> Scores) Mount(IMinigameImpl impl, Dictionary<string, JsonElement>? config = null, double random = 0.5)
    {
        var scores = new List<double>();
        var session = impl.Mount(new MinigameMountOptions(config ?? [], scores.Add, () => { }, () => random));
        return (session, scores);
    }

    [Fact]
    public void TimingBarScoresOneInsideTheZoneAndFallsOffLinearlyOutside()
    {
        // random 0.5 → target center 0.5; default speed 0.9 sweeps/s.
        var (session, scores) = Mount(Minigames.TimingBar);
        var bar = Assert.IsType<TimingBarSession>(session);
        Assert.Equal(0.5, bar.TargetCenter);
        Assert.Equal(0.18, bar.TargetSize);
        Assert.Equal("Stop the marker in the zone!", bar.Prompt);

        bar.Update(0.5 / 0.9); // marker at 0.5
        Assert.Equal(0.5, bar.Position, 9);
        bar.Press();
        bar.Press(); // at most once
        Assert.Equal([1.0], scores);
        Assert.True(bar.IsDone);
    }

    [Fact]
    public void TimingBarMissScoresByDistanceAndSweepsBack()
    {
        var (session, scores) = Mount(Minigames.TimingBar, new() { ["speed"] = Js.Value(1), ["targetSize"] = Js.Value(0.1), ["prompt"] = Js.Value("Go") });
        var bar = (TimingBarSession)session;
        Assert.Equal("Go", bar.Prompt);
        bar.Update(1.8); // triangle wave: 1.8 → 0.2
        Assert.Equal(0.2, bar.Position, 9);
        bar.Stop();
        Assert.Single(scores);
        Assert.Equal(0.7, scores[0], 9);
    }

    [Fact]
    public void TimingBarClampsConfig()
    {
        var bar = (TimingBarSession)Mount(Minigames.TimingBar, new() { ["speed"] = Js.Value(0), ["targetSize"] = Js.Value(5), ["prompt"] = Js.Value(3) }).Session;
        Assert.Equal(0.1, bar.Speed);
        Assert.Equal(0.9, bar.TargetSize);
        Assert.Equal("Stop the marker in the zone!", bar.Prompt);
    }

    [Fact]
    public void FallbackScoresANeutralHalfOnce()
    {
        var (session, scores) = Mount(Minigames.Fallback);
        Assert.Equal("Ready?", session.Prompt);
        Assert.Equal("Go!", session.ButtonText);
        session.Press();
        session.Press();
        Assert.Equal([0.5], scores);
    }

    [Fact]
    public void DisposedSessionsNeverComplete()
    {
        var (session, scores) = Mount(Minigames.Fallback);
        session.Dispose();
        session.Press();
        Assert.Empty(scores);
    }

    [Fact]
    public void CancelReportsOnceWithoutAScore()
    {
        var cancelled = 0;
        var scores = new List<double>();
        var session = (MinigameSessionBase)Minigames.TimingBar.Mount(new MinigameMountOptions(new Dictionary<string, JsonElement>(), scores.Add, () => cancelled++));
        session.Cancel();
        session.Cancel();
        session.Press();
        Assert.Equal(1, cancelled);
        Assert.Empty(scores);
    }
}
