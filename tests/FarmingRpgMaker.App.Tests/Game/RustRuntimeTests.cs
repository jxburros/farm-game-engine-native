using System.Text.Json;
using FarmEngine.Content;
using FarmEngine.Core;
using FarmEngine.Interop;
using FarmEngine.Json;
using FarmEngine.Runtime;
using FarmEngine.Schemas;
using FarmingRpgMaker.App.Game;

namespace FarmingRpgMaker.App.Tests.Game;

public sealed class RustRuntimeTests
{
    private static GameProject Starter() => DefaultContent.CreateInitialProject(0);
    private static void EqualJson<T>(T expected, T actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected, JsonDefaults.Options), JsonSerializer.Serialize(actual, JsonDefaults.Options));

    [Fact]
    public void FramesMatchForHeldKeysQuickTapsOpposingKeysAndModals()
    {
        if (!FarmFfi.IsAvailable) return;
        using var managed = new PlaySession(Starter(), PlayEngineKind.CSharp);
        using var native = new PlaySession(Starter(), PlayEngineKind.Rust);
        Assert.Equal(PlayEngineKind.Rust, native.EngineKind);
        void Frame(double dt, bool modal, string[] down, string[] up)
        {
            foreach (var session in new[] { managed, native })
            {
                foreach (var key in down) session.Input.KeyDown(key);
                foreach (var key in up) session.Input.KeyUp(key);
            }
            EqualJson(managed.Update(dt, modal), native.Update(dt, modal));
            EqualJson(managed.State, native.State);
            Assert.Equal(managed.Alpha, native.Alpha);
        }
        Frame(0.016, false, ["w", "d"], []);
        for (var i = 0; i < 8; i++) Frame(0.016, false, [], []);
        Frame(0.05, false, ["s", "a"], []);
        Frame(0.05, false, [], ["w", "d", "s", "a"]);
        Frame(0.01, false, ["i", "q"], ["i", "q"]);
        Frame(0.5, true, ["w", "e", "escape"], ["e", "escape"]);
        Frame(-1, true, [], ["w"]);
        foreach (var session in new[] { managed, native })
            session.RunCommand(new StartMinigameCommand("fishing"));
        Frame(0.05, false, ["e", "q", "escape"], []);
        Frame(0.05, false, [], ["e", "q", "escape"]);
        managed.ReleaseInput();
        native.ReleaseInput();
        EqualJson(managed.State, native.State);
    }

    [Theory]
    [InlineData("timing-bar")]
    [InlineData("hold-to-catch")]
    [InlineData("simple-battle")]
    [InlineData("unknown-kind")]
    public void MinigameViewsScoresAndResultCommandsMatch(string kind)
    {
        if (!FarmFfi.IsAvailable) return;
        var starter = Starter();
        var definition = starter.Minigames!.First(def => def.Id == "fishing") with { Kind = kind };
        var project = starter with { Minigames = [definition] };
        using var managed = new PlaySession(project, PlayEngineKind.CSharp);
        using var native = new PlaySession(project, PlayEngineKind.Rust);
        Assert.Equal(PlayEngineKind.Rust, native.EngineKind);
        foreach (var session in new[] { managed, native }) session.RunCommand(new StartMinigameCommand("fishing"));
        var registry = Minigames.CreateDefaultMinigameRegistry();
        using var a = managed.MountMinigame(registry, definition, 0.37);
        using var b = native.MountMinigame(registry, definition, 0.37);
        void Step(Action<PlayMinigame> action)
        {
            action(a);
            action(b);
            EqualJson(a.View, b.View);
            EqualJson(managed.State, native.State);
        }
        EqualJson(a.View, b.View);
        if (kind == "simple-battle")
        {
            Step(game => game.Act("guard"));
            Step(game => game.Act("magic"));
            for (var i = 0; i < 20 && !a.IsDone; i++) Step(game => game.Act("attack"));
        }
        else if (kind == "hold-to-catch")
        {
            Step(game => game.Release()); // release without a press cannot score
            Step(game => game.Press());
            Step(game => game.Update(1.2));
            Step(game => game.Release());
        }
        else
        {
            Step(game => game.Update(0.53));
            Step(game => game.Press());
        }
        Assert.True(a.IsDone);
        Assert.NotNull(b.Score);
        Assert.Null(native.State.Minigame);
        var completed = Hash.HashState(native.State);
        Step(game => game.Press());
        Step(game => game.Release());
        Step(game => game.Update(1));
        Assert.Equal(completed, Hash.HashState(native.State));
    }

    [Fact]
    public void PanelVisibilityFlagValuesItemTotalsAndModalBlockingMatch()
    {
        if (!FarmFfi.IsAvailable) return;
        var project = Starter() with
        {
            GamePanels = [new GamePanel
            {
                Id = "test", Title = "Farm", VisibleFlag = "visible", Entries =
                [
                    new() { Kind = "money", Label = "Gold" }, new() { Kind = "energy" },
                    new() { Kind = "day" }, new() { Kind = "flag", Value = "visible" },
                    new() { Kind = "item", Value = "seed-wheat" },
                    new() { Kind = "action", Label = "Forage", Value = "action-forage-snack" },
                ],
            }],
        };
        using var managed = new PlaySession(project, PlayEngineKind.CSharp);
        using var native = new PlaySession(project, PlayEngineKind.Rust);
        EqualJson(managed.PanelViews(false), native.PanelViews(false));
        Assert.True(native.PanelViews(false)[0].Hidden);
        foreach (var session in new[] { managed, native })
            session.DebugMutate((state, _) => state with { Flags = new(state.Flags) { ["visible"] = Js.Value("yes") } });
        EqualJson(managed.PanelViews(false), native.PanelViews(false));
        Assert.False(native.PanelViews(false)[0].Hidden);
        Assert.True(native.PanelViews(false)[0].Entries.Last().Enabled);
        EqualJson(managed.PanelViews(true), native.PanelViews(true));
        Assert.False(native.PanelViews(true)[0].Entries.Last().Enabled);
        foreach (var session in new[] { managed, native }) session.RunCommand(new StartMinigameCommand("fishing"));
        EqualJson(managed.PanelViews(false), native.PanelViews(false));
        Assert.False(native.PanelViews(false)[0].Entries.Last().Enabled);
    }

    [Fact]
    public void ExpiredMountCannotScoreAndItsDisposalCannotDisposeNewMount()
    {
        if (!FarmFfi.IsAvailable) return;
        using var session = RustSession.Create(Starter());
        session.Apply(new StartMinigameCommand("fishing"));
        var first = session.Runtime<JsonElement>(new { type = "mountMinigame", random = 0.5 });
        var firstId = first.GetProperty("generation").GetUInt64();
        var second = session.Runtime<JsonElement>(new { type = "mountMinigame", random = 0.5 });
        var secondId = second.GetProperty("generation").GetUInt64();
        Assert.Throws<FarmFfiException>(() => session.Runtime<JsonElement>(new { type = "minigameInput", generation = firstId, input = new { type = "press" } }));
        session.Runtime<object?>(new { type = "disposeMinigame", generation = firstId });
        var view = session.Runtime<PlayMinigameView>(new { type = "minigameInput", generation = secondId, input = new { type = "press" } });
        Assert.NotNull(view.Score);
        // Runtime input itself never mutates simulation: the host must log resolveMinigame.
        Assert.NotNull(session.State().Minigame);
        session.Apply(new CancelMinigameCommand());
        Assert.Throws<FarmFfiException>(() => session.Runtime<JsonElement>(new { type = "minigameInput", generation = secondId, input = new { type = "press" } }));
        Assert.False(session.IsPoisoned);
    }

    [Fact]
    public void DisposedMinigameCannotResolveAndInvalidFrameCannotPoisonAccumulator()
    {
        foreach (var kind in new[] { PlayEngineKind.CSharp, PlayEngineKind.Rust })
        {
            if (kind == PlayEngineKind.Rust && !FarmFfi.IsAvailable) continue;
            using var session = new PlaySession(Starter(), kind);
            Assert.Throws<ArgumentOutOfRangeException>(() => session.Update(double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => session.Update(double.PositiveInfinity));
            session.Update(0.025);
            Assert.Equal(0.5, session.Alpha);
            session.RunCommand(new StartMinigameCommand("fishing"));
            using var game = session.MountMinigame(Minigames.CreateDefaultMinigameRegistry(), session.Content.Minigames.First(def => def.Id == "fishing"), 0.5);
            game.Dispose();
            game.Press();
            Assert.Null(game.Score);
            Assert.NotNull(session.State.Minigame);
        }
    }

    [Fact]
    public void CalendarAndSoundViewsMatchTheReferenceRuntime()
    {
        if (!FarmFfi.IsAvailable) return;
        var starter = Starter();
        var project = starter with { Settings = starter.Settings! with { Calendar = new CalendarConfig
        {
            Seasons = [new() { Id = "wet", Name = "Rainy season", Days = 3 }, new() { Id = "dry", Name = "Dry season", Days = 5 }],
        } } };
        using var managed = new PlaySession(project, PlayEngineKind.CSharp);
        using var native = new PlaySession(project, PlayEngineKind.Rust);
        foreach (var day in new[] { 1, 3, 4, 8, 9, 17 })
        {
            foreach (var session in new[] { managed, native })
                session.DebugMutate((state, _) => state with { Clock = state.Clock with { Day = day, Season = "wet", TimeMinutes = 1561.7 } });
            EqualJson(managed.CalendarView(), native.CalendarView());
        }
        List<Effect> effects = [new SoundEffect("custom"), new PlayerMovedEffect(0, 0), new MessageEffect("info", "hello"),
            new MessageEffect("error", "error"), new MessageEffect("success", "done"), new CropHarvestedEffect("wheat", 1),
            new QuestCompletedEffect("quest"), new DayStartedEffect(2, "wet", 1), new SceneChangedEffect("farm", 0, 0)];
        EqualJson(managed.Engine.AudioCues(effects), native.Engine.AudioCues(effects));
        Assert.Equal(new string?[] { "custom", null, null, "error", "success", "harvest", "quest", "sleep", "ui" }, native.Engine.AudioCues(effects));
    }

    [Fact]
    public void NativeMinigameFailureUsesThePlaySessionFaultBoundary()
    {
        if (!FarmFfi.IsAvailable) return;
        using var session = new PlaySession(Starter(), PlayEngineKind.Rust);
        session.RunCommand(new StartMinigameCommand("fishing"));
        using var game = session.MountMinigame(Minigames.CreateDefaultMinigameRegistry(), session.Content.Minigames.First(def => def.Id == "fishing"), 0.5);
        Exception? fault = null;
        session.Faulted += (_, error) => fault = error;
        ((RustPlayEngine)session.Engine).Session.Dispose();
        game.Press();
        Assert.NotNull(fault);
        Assert.Same(fault, session.Fault);
        Assert.Null(game.Score);
    }
}
