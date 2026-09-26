using System.Text.Json;
using FarmEngine.Content;
using FarmEngine.Core;
using FarmEngine.Interop;
using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmingRpgMaker.App.Tests.Interop;

/// <summary>
/// The Rust engine and the C# engine must agree. These run when the Rust library was built
/// (see <see cref="FarmFfiTests.LibraryLoadsWhenTheBuildProducedIt"/>).
/// </summary>
public sealed class RustSessionTests
{
    private static GameProject Starter() => DefaultContent.CreateInitialProject(0);

    [Fact]
    public void CreatedStateHashesLikeTheCSharpEngine()
    {
        if (!FarmFfi.IsAvailable)
        {
            return;
        }

        var project = Starter();
        using var session = RustSession.Create(project, "parity");
        var expected = Hash.HashState(EngineState.CreateGameState(project, "parity"));
        Assert.Equal(expected, session.StateHash());
        Assert.Equal(Hash.StableStringify(EngineState.CreateGameState(project, "parity")), session.StateJson());
        Assert.Equal(expected, Hash.HashState(session.State()));
    }

    [Fact(Skip = "Enable when the farm-sim gameplay port passes the golden replays")]
    public void AutoStartedQuestsMatchTheCSharpEngine()
    {
        if (!FarmFfi.IsAvailable)
        {
            return;
        }

        var project = Starter();
        var ctx = new EngineContext(EngineState.CreateContentFromProject(project));
        var expected = Quests.AutoStartQuests(ctx, EngineState.CreateGameState(project, "parity"));
        using var session = RustSession.Create(project, "parity", autoStartQuests: true);
        Assert.Equal(Hash.HashState(expected), session.StateHash());
    }

    [Fact]
    public void RejectsUnparsableProjects()
    {
        if (!FarmFfi.IsAvailable)
        {
            return;
        }

        // A project whose scenes are not a list cannot be a GameProject on either side.
        var broken = JsonDocument.Parse("""{"scenes": 5}""").RootElement;
        var ex = Assert.Throws<FarmFfiException>(() => RustSession.Create(JsonSerializer.Deserialize<GameProject>("{}", JsonDefaults.Options)! with { Extra = new Dictionary<string, JsonElement> { ["scenes"] = broken.GetProperty("scenes") } }));
        Assert.Contains("fe_session_new failed", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Differential test (docs/LANGUAGES.md, phase 2): the same seeded random command stream
    /// through both engines, hashes compared after every step. Enabled once the Rust gameplay
    /// modules pass the golden replays.
    /// </summary>
    [Fact(Skip = "Enable when the farm-sim gameplay port passes the golden replays")]
    public void RandomCommandStreamsHashIdentically()
    {
        if (!FarmFfi.IsAvailable)
        {
            return;
        }

        var project = Starter();
        var ctx = new EngineContext(EngineState.CreateContentFromProject(project));
        var state = Quests.AutoStartQuests(ctx, EngineState.CreateGameState(project, "fuzz"));
        using var session = RustSession.Create(project, "fuzz", autoStartQuests: true);
        var random = new Random(12345);
        string[] dirs = ["up", "down", "left", "right"];
        string[] tools = ["hoe", "watering-can", "axe", "pickaxe", "scythe"];
        for (var step = 0; step < 400; step++)
        {
            Command command = random.Next(8) switch
            {
                0 => new SetMoveIntentCommand(random.Next(-1, 2), random.Next(-1, 2)),
                1 => new MoveCommand(dirs[random.Next(dirs.Length)]),
                2 => new UseToolCommand(tools[random.Next(tools.Length)]),
                3 => new InteractCommand(),
                4 => new SleepCommand(),
                5 => new ChooseDialogueOptionCommand(random.Next(3)),
                _ => new CloseDialogueCommand(),
            };
            var ticks = random.Next(0, 40);
            state = Engine.ApplyCommand(ctx, state, command).State;
            state = Engine.AdvanceTick(ctx, state, ticks).State;
            session.Apply(command);
            session.Tick((uint)ticks);
            Assert.True(Hash.HashState(state) == session.StateHash(), $"diverged at step {step} after {command.Type}");
        }
    }
}
