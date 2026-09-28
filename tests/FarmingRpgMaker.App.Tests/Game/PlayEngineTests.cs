using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FarmEngine.Content;
using FarmEngine.Core;
using FarmEngine.Interop;
using FarmEngine.Json;
using FarmEngine.Runtime;
using FarmEngine.Schemas;
using FarmingRpgMaker.App.Game;
using FarmingRpgMaker.App.Hosting;
using Xunit.Abstractions;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

/// <summary>
/// Play Mode runs the Rust engine when its library loaded (roadmap phase 4) and the C# engine
/// otherwise. The player must not see a difference: these drive the same scripted play through
/// <see cref="PlaySession"/> on both and compare everything the views read.
/// </summary>
public sealed class PlayEngineTests(ITestOutputHelper output)
{
    private const double Frame = 1.0 / 60;

    /// <summary>
    /// A plugin that answers every hook it may see with a mutation that records the payload, so
    /// hook order and payload JSON (key order included) end up in the compared state.
    /// </summary>
    private const string EchoPlugin = """
        api.on('onCommand', function (p) { if (p.commandType === 'pluginMutation') return []; return [{ type: 'setFlag', flag: 'last-command', value: JSON.stringify(p) }]; });
        api.on('onEffect', function (p) { return p.effectType === 'message' ? [{ type: 'giveMoney', amount: 1 }] : []; });
        api.on('onDayStart', function (p) { return [{ type: 'message', text: 'echo ' + JSON.stringify(p) }]; });
        api.on('onNPCInteract', function (p) { return [{ type: 'setFlag', flag: 'talked', value: Object.keys(p).join(',') + '=' + p.npcId }]; });
        api.on('onWeatherRoll', function (p) { return [{ type: 'setFlag', flag: 'weather', value: JSON.stringify(p) }]; });
        """;

    private static GameProject Starter() => DefaultContent.CreateInitialProject(0);

    private static GameProject StarterWithPlugins()
    {
        var demo = JsonDefaults.Deserialize<ContentPack>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "demo-mod.json")))!;
        string[] hooks = [HookNames.OnDayStart, HookNames.OnCommand, HookNames.OnEffect, HookNames.OnNPCInteract, HookNames.OnWeatherRoll];
        var pack = demo with
        {
            Manifest = demo.Manifest with { Permissions = demo.Manifest.Permissions with { Hooks = [.. hooks] } },
            Plugins = [.. demo.Plugins, new PackPlugin { Id = "echo", Name = "Echo", Hooks = [.. hooks], Source = EchoPlugin }],
        };
        return Starter() with { ContentPacks = [new PackInstallation { Pack = pack, Enabled = true }] };
    }

    /// <summary>A session plus everything it told its host.</summary>
    private sealed class Run : IDisposable
    {
        public Run(GameProject project, PlayEngineKind kind)
        {
            // Wall-clock plugin budgets would make a slow moment on a busy test machine drop a
            // mutation in one run and not the other.
            Session = new PlaySession(project, kind, pluginOptions: new JintPluginHostOptions { TimeoutMs = 10_000, InitTimeoutMs = 10_000 });
            Assert.Equal(kind, Session.EngineKind);
            Session.Toast += (_, toast) => Log.Add($"toast {toast.Kind}: {toast.Text}");
            Session.SceneChanged += (_, scene) => Log.Add($"scene {scene.SceneId} {scene.X},{scene.Y}");
            Session.StateChanged += (_, _) => StateChanges++;
        }

        public PlaySession Session { get; }

        public List<string> Log { get; } = [];

        public int StateChanges { get; private set; }

        public void Frames(int count, params string[] held)
        {
            foreach (var key in held)
            {
                Session.Input.KeyDown(key);
            }

            for (var i = 0; i < count; i++)
            {
                Session.Update(Frame);
            }

            foreach (var key in held)
            {
                Session.Input.KeyUp(key);
            }
        }

        public void Dispose() => Session.Dispose();
    }

    private static void TeleportFacingUp(PlaySession session, string npcId)
    {
        var npc = session.Content.Npcs.First(n => n.Id == npcId);
        session.DebugMutate((s, _) => s with { Player = s.Player with { X = npc.X + 0.5, Y = npc.Y + 1.5, Direction = "up" } });
    }

    /// <summary>The same inputs on both engines, compared after every step.</summary>
    private static void Script(Action<Action<Run>> each, Action<string> check)
    {
        void Step(string label, Action<Run> action)
        {
            each(action);
            check(label);
        }

        Step("idle", run => run.Frames(5));
        Step("walk right", run => run.Frames(40, "d"));
        Step("walk down", run => run.Frames(20, "s"));
        Step("stop", run => run.Frames(5));
        Step("talk to the farmer", run =>
        {
            TeleportFacingUp(run.Session, "npc-farmer");
            run.Session.RunCommand(new InteractCommand());
            run.Frames(2);
        });
        Step("ask about crops", run => run.Session.RunCommand(new ChooseDialogueOptionCommand(1)));
        Step("say goodbye", run =>
        {
            run.Session.RunCommand(new ChooseDialogueOptionCommand(0));
            run.Frames(2);
        });
        Step("open the shop", run =>
        {
            TeleportFacingUp(run.Session, "npc-merchant");
            run.Session.RunCommand(new InteractCommand());
            run.Session.RunCommand(new ChooseDialogueOptionCommand(0));
            run.Frames(3);
        });
        Step("buy and sell", run =>
        {
            run.Session.RunCommand(new BuyItemCommand("seed-wheat", 2));
            run.Session.RunCommand(new SellItemCommand("seed-wheat", 1));
            run.Session.RunCommand(new BuyItemCommand("no-such-item", 1));
            run.Frames(2);
        });
        Step("leave the shop", run =>
        {
            run.Session.RunCommand(new CloseShopCommand());
            run.Frames(2);
        });
        Step("work the soil", run =>
        {
            run.Session.RunCommand(new UseToolCommand("hoe"));
            run.Session.RunCommand(new UseToolCommand("watering-can"));
            run.Frames(3, "e");
        });
        Step("sleep (plugins answer on the next frame)", run =>
        {
            run.Session.RunCommand(new SleepCommand());
            run.Frames(1);
            run.Frames(30, "a");
        });
        Step("debug: skip a day, money, season", run =>
        {
            run.Session.DebugSkipDay();
            run.Session.DebugMutate((s, _) => s with { Player = s.Player with { Money = s.Player.Money + 500 } });
            run.Frames(2);
        });
        Step("minigame", run =>
        {
            run.Session.RunCommand(new StartMinigameCommand("fishing"));
            run.Frames(3);
            run.Session.RunCommand(new ResolveMinigameCommand(0.8));
            run.Frames(3);
        });
        Step("a long walk", run => run.Frames(400, "w", "a"));
        Step("a few hours", run => run.Frames(600));
    }

    [Fact]
    public void PlaytestsRunOnRustWhenItsLibraryLoaded()
    {
        if (Environment.GetEnvironmentVariable(PlayEngines.EnvironmentVariable) is { Length: > 0 })
        {
            return;
        }

        using var session = new PlaySession(Starter());
        Assert.Equal(FarmFfi.IsAvailable ? PlayEngineKind.Rust : PlayEngineKind.CSharp, session.EngineKind);
    }

    [Fact]
    public void ScriptedPlayIsIdenticalOnBothEngines()
    {
        if (!FarmFfi.IsAvailable)
        {
            return;
        }

        var project = StarterWithPlugins();
        using var csharp = new Run(project, PlayEngineKind.CSharp);
        using var rust = new Run(project, PlayEngineKind.Rust);
        Assert.True(rust.Session.HasPlugins);
        var rustSession = ((RustPlayEngine)rust.Session.Engine).Session;

        Script(
            step =>
            {
                step(csharp);
                step(rust);
            },
            label =>
            {
                // Everything a view reads: the state in C# order (maps list their entries as the
                // C# engine inserted them), the host notifications, and the refresh cadence.
                var expected = JsonDefaults.Serialize(csharp.Session.State);
                var actual = JsonDefaults.Serialize(rust.Session.State);
                Assert.True(expected == actual, $"state differs after \"{label}\": {FirstDifference(expected, actual)}; plugin errors: {string.Join(" | ", csharp.Session.PluginErrors.Concat(rust.Session.PluginErrors).Select(e => $"{e.PluginId} {e.Kind} {e.Message}"))}");
                Assert.Equal(csharp.Log, rust.Log);
                Assert.Equal(csharp.StateChanges, rust.StateChanges);
                var csharpOverlay = Hash.StableStringify(csharp.Session.OverlayView());
                var rustOverlay = Hash.StableStringify(rust.Session.OverlayView());
                Assert.True(csharpOverlay == rustOverlay, $"overlay queries differ after \"{label}\": {FirstDifference(csharpOverlay, rustOverlay)}");
                // The mirror is exactly the Rust state.
                Assert.Equal(rustSession.StateHash(), Hash.HashState(rust.Session.State));
                Assert.Equal(Hash.StableStringify(csharp.Session.BuildSnapshot(640, 416)),
                    Hash.StableStringify(rust.Session.BuildSnapshot(640, 416)));
            });

        Assert.Equal(Hash.StableStringify(csharp.Session.SyncedProject()), Hash.StableStringify(rust.Session.SyncedProject()));
        Assert.Empty(csharp.Session.PluginErrors);
        Assert.Empty(rust.Session.PluginErrors);

        // The script reached what it meant to test.
        var flags = rust.Session.State.Flags;
        Assert.Equal("npcId=npc-merchant", flags["talked"].GetString());
        Assert.StartsWith("{\"weatherId\":", flags["weather"].GetString(), StringComparison.Ordinal);
        Assert.Contains(rust.Log, line => line.StartsWith("toast Info: echo {\"day\":2,\"season\":", StringComparison.Ordinal));
        Assert.Contains(rust.Log, line => line.Contains("glowshrooms hum softly on day 2", StringComparison.Ordinal));
        Assert.Contains(rust.Session.State.Player.Inventory, slot => slot.Item.Id == "seed-wheat");
        Assert.True(rust.Session.State.Clock.Day >= 3);
    }

    /// <summary>
    /// A failed engine call from a button (a Rust panic poisons the session; a freed session
    /// fails every call the same way) ends the playtest through the fault path: back to Edit
    /// Mode, nothing kept, the app still running.
    /// </summary>
    [AvaloniaFact]
    public void AnEngineFailureEndsThePlaytestWithoutKeepingChanges()
    {
        using var host = new GameTestHost();
        host.EnterPlay();
        if (host.Play.Session.Engine is not RustPlayEngine engine)
        {
            return;
        }

        var before = host.Workspace.Current;
        host.Play.KeepChanges = true;
        host.Frames(5);
        engine.Session.Dispose();

        Click(host.Window, FindByName<Button>(host.Window, "SleepButton"));
        Pump();

        Assert.Equal(EditorMode.Edit, host.ViewModel.Mode);
        Assert.Null(host.Surface.PlayView);
        Assert.Same(before, host.Workspace.Current);
    }

    private static string FirstDifference(string expected, string actual)
    {
        var at = 0;
        while (at < expected.Length && at < actual.Length && expected[at] == actual[at])
        {
            at++;
        }

        var from = Math.Max(0, at - 80);
        string Around(string text) => text[from..Math.Min(text.Length, at + 80)];
        return $"at {at}: C# …{Around(expected)}… Rust …{Around(actual)}…";
    }

    [Fact]
    public void TheMirrorKeepsUnchangedSectionsByReference()
    {
        if (!FarmFfi.IsAvailable)
        {
            return;
        }

        using var session = new PlaySession(Starter(), PlayEngineKind.Rust);
        var before = session.State;

        // A frame without ticks or commands leaves the state object alone.
        session.Update(0);
        Assert.Same(before, session.State);

        // Ticks move the clock only.
        session.Update(0.25);
        var ticked = session.State;
        Assert.NotSame(before, ticked);
        Assert.NotEqual(before.Clock, ticked.Clock);
        Assert.Same(before.World, ticked.World);
        Assert.Same(before.Player, ticked.Player);
        Assert.Same(before.Quests, ticked.Quests);
        Assert.Same(before.Flags, ticked.Flags);
        Assert.Same(before.Meta, ticked.Meta);

        // Walking changes the player's position; the rest of the player is shared.
        session.Input.KeyDown("d");
        for (var i = 0; i < 20; i++)
        {
            session.Update(Frame);
        }

        session.Input.KeyUp("d");
        var walked = session.State;
        Assert.NotEqual(ticked.Player.X, walked.Player.X);
        Assert.Same(ticked.Player.Inventory, walked.Player.Inventory);
        Assert.Same(ticked.Player.Skills, walked.Player.Skills);
        Assert.Same(ticked.Player.ActiveQuests, walked.Player.ActiveQuests);

        // An open dialogue stays the same object while time passes (the overlay is not rebuilt).
        TeleportFacingUp(session, "npc-farmer");
        session.RunCommand(new InteractCommand());
        var dialogue = session.State.Dialogue;
        Assert.NotNull(dialogue);
        session.Update(0.25);
        Assert.Same(dialogue, session.State.Dialogue);
    }

    [Fact]
    public void GameStateMirrorSharesSectionsWhoseJsonDidNotChange()
    {
        var state = EngineState.CreateGameState(Starter(), "mirror");
        var mirror = new GameStateMirror();
        Assert.True(mirror.Apply(JsonSerializerUtf8(state)));
        var first = mirror.State;
        Assert.Equal(Hash.HashState(state), Hash.HashState(first));

        // Nothing listed, or only identical JSON: the same object.
        Assert.False(mirror.Apply("{}"u8));
        Assert.False(mirror.Apply(System.Text.Encoding.UTF8.GetBytes($"{{\"clock\":{JsonDefaults.Serialize(state.Clock)}}}")));
        Assert.Same(first, mirror.State);

        // One changed section: a new state sharing every other section.
        var clock = state.Clock with { TimeMinutes = state.Clock.TimeMinutes + 10 };
        var player = state.Player with { Money = 999 };
        Assert.True(mirror.Apply(System.Text.Encoding.UTF8.GetBytes($"{{\"clock\":{JsonDefaults.Serialize(clock)},\"player\":{JsonDefaults.Serialize(player)}}}")));
        var second = mirror.State;
        Assert.Equal(clock, second.Clock);
        Assert.Equal(999, second.Player.Money);
        Assert.Same(first.World, second.World);
        Assert.Same(first.Quests, second.Quests);
        Assert.Same(first.Player.Inventory, second.Player.Inventory);
        Assert.Equal(Hash.HashState(state with { Clock = clock, Player = player }), Hash.HashState(second));

        // Sections that become null (a closed dialogue) and come back.
        var talking = state with { Dialogue = new DialogueState { NpcId = "npc-farmer", DialogueId = "hello" } };
        Assert.True(mirror.Apply(System.Text.Encoding.UTF8.GetBytes($"{{\"dialogue\":{JsonDefaults.Serialize(talking.Dialogue)}}}")));
        Assert.Equal("hello", mirror.State.Dialogue?.DialogueId);
        Assert.True(mirror.Apply("{\"dialogue\":null}"u8));
        Assert.Null(mirror.State.Dialogue);
    }

    private static byte[] JsonSerializerUtf8(GameState state) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(state, JsonDefaults.Options);

    [Fact]
    public void DebugMutateWritesThroughToTheRustEngine()
    {
        if (!FarmFfi.IsAvailable)
        {
            return;
        }

        using var session = new PlaySession(Starter(), PlayEngineKind.Rust);
        var rust = ((RustPlayEngine)session.Engine).Session;
        var changes = 0;
        session.StateChanged += (_, _) => changes++;
        var money = session.State.Player.Money;
        var inventory = session.State.Player.Inventory;

        session.DebugMutate((s, _) => s with { Player = s.Player with { Money = s.Player.Money + 500 } });
        Assert.Equal(1, changes);
        Assert.Equal(money + 500, session.State.Player.Money);
        Assert.Same(inventory, session.State.Player.Inventory);
        Assert.Equal(rust.StateHash(), Hash.HashState(session.State));

        // The engine carries on from the edited state.
        session.RunCommand(new SleepCommand());
        Assert.Equal(money + 500, session.State.Player.Money);
        var day = session.State.Clock.Day;
        session.DebugSkipDay();
        Assert.Equal(day + 1, session.State.Clock.Day);
        Assert.Equal(rust.StateHash(), Hash.HashState(session.State));
    }

    /// <summary>
    /// Per-frame cost of Play Mode's engine loop on the starter farm (walking, 60 frames/s,
    /// 20 ticks/s). Reported, not asserted: timings depend on the machine.
    /// </summary>
    [Fact]
    public void ReportsTheFrameCostOfBothEngines()
    {
        foreach (var kind in new[] { PlayEngineKind.CSharp, PlayEngineKind.Rust })
        {
            if (kind == PlayEngineKind.Rust && !FarmFfi.IsAvailable)
            {
                continue;
            }

            using var session = new PlaySession(Starter(), kind);
            var clock = new Stopwatch();
            var frames = new List<double>();
            string[] keys = ["d", "s", "a", "w"];
            for (var i = 0; i < 1200; i++)
            {
                // Change direction every second so intents and positions keep changing.
                if (i % 60 == 0)
                {
                    session.Input.KeyUp(keys[(i / 60 + 3) % 4]);
                    session.Input.KeyDown(keys[i / 60 % 4]);
                }

                clock.Restart();
                session.Update(Frame);
                clock.Stop();
                if (i >= 120)
                {
                    frames.Add(clock.Elapsed.TotalMilliseconds);
                }
            }

            frames.Sort();
            output.WriteLine($"{kind}: mean {frames.Average():0.000} ms, median {frames[frames.Count / 2]:0.000} ms, p99 {frames[(int)(frames.Count * 0.99)]:0.000} ms, max {frames[^1]:0.000} ms per frame over {frames.Count} frames");

            // Commands that change the world (tilling, watering) resend the world section.
            var commands = new List<double>();
            session.Input.Clear();
            session.Update(Frame);
            for (var i = 0; i < 200; i++)
            {
                clock.Restart();
                session.RunCommand(new UseToolCommand(i % 2 == 0 ? "hoe" : "watering-can"));
                clock.Stop();
                commands.Add(clock.Elapsed.TotalMilliseconds);
                session.RunCommand(new MoveCommand(i / 10 % 2 == 0 ? "left" : "right"));
            }

            output.WriteLine($"{kind}: mean {commands.Skip(20).Average():0.000} ms per tool command");

            // A night: the whole farm grows.
            clock.Restart();
            session.RunCommand(new SleepCommand());
            clock.Stop();
            output.WriteLine($"{kind}: {clock.Elapsed.TotalMilliseconds:0.000} ms for a sleep command");
        }
    }
}
