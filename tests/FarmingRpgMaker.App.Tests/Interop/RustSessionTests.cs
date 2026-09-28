using System.Text.Json;
using FarmEngine.Authoring;
using FarmEngine.Cart;
using FarmEngine.Content;
using FarmEngine.Core;
using FarmEngine.Interop;
using FarmEngine.Json;
using FarmEngine.Schemas;
using Google.FlatBuffers;

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

    [Fact]
    public void CompiledCartridgeRunsInRustAndUsesPersistentSaveIdentity()
    {
        var project = Starter() with
        {
            Export = new ExportSettings { GameId = "local.test-farm", Version = "3.1.0", Title = "Test Farm" },
        };
        var bytes = CartridgeCompiler.Compile(project);
        Assert.Equal(bytes, CartridgeCompiler.Compile(project));
        var buffer = new ByteBuffer(bytes);
        Assert.True(Cartridge.CartridgeBufferHasIdentifier(buffer));
        var cart = Cartridge.GetRootAsCartridge(buffer);
        Assert.Equal(2u, cart.CartFormat);
        Assert.NotEmpty(cart.GetStartJsonArray());
        Assert.NotEmpty(cart.GetPresentationJsonArray());
        Assert.Equal("local.test-farm", cart.Info!.Value.GameId);
        Assert.Equal("3.1.0", cart.Info.Value.Version);
        Assert.NotEmpty(cart.GetContentJsonArray());

        if (!FarmFfi.IsAvailable) return;
        using var fromCart = RustSession.CreateCartridge(bytes, "parity");
        using var fromProject = RustSession.Create(project, "parity");
        Assert.Equal(fromProject.StateHash(), fromCart.StateHash());
        using var saved = JsonDocument.Parse(fromCart.Save());
        Assert.Equal("local.test-farm", saved.RootElement.GetProperty("header").GetProperty("gameId").GetString());
        Assert.Equal("3.1.0", saved.RootElement.GetProperty("header").GetProperty("gameVersion").GetString());
        fromCart.Apply(new SleepCommand());
        fromProject.Apply(new SleepCommand());
        Assert.Equal(fromProject.StateHash(), fromCart.StateHash());
        // A cartridge carries no editor project, so there is nothing to write play state back to.
        Assert.Throws<FarmFfiException>(() => fromCart.SyncedProject());
        Assert.Throws<FarmFfiException>(() => RustSession.CreateCartridge([0, 0, 0, 0, (byte)'F', (byte)'G', (byte)'C', (byte)'T']));
    }

    [Fact]
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

    [Theory]
    [InlineData(ProjectTemplates.Starter)]
    [InlineData(ProjectTemplates.Blank)]
    [InlineData(ProjectTemplates.Cozy)]
    [InlineData(ProjectTemplates.Quest)]
    public void FSharpCartridgeContentAndRustPlayMatchEveryTemplate(string template)
    {
        var project = ProjectCatalog.CreateProjectForTemplate(template, 0);
        var bytes = CartridgeCompiler.Compile(project);
        var cart = Cartridge.GetRootAsCartridge(new ByteBuffer(bytes));
        var content = JsonSerializer.Deserialize<GameContent>(cart.GetContentJsonArray(), JsonDefaults.Options)!;
        Assert.Equal(Hash.StableStringify(EngineState.CreateContentFromProject(project)), Hash.StableStringify(content));
        Assert.Equal(Hash.StableStringify(ProjectContent.Compile(project)), Hash.StableStringify(content));
        Assert.Equal(bytes, CartridgeCompiler.Compile(project));
        if (!FarmFfi.IsAvailable) return;

        using var compiled = RustSession.CreateCartridge(bytes, "compiled-template");
        using var reference = RustSession.Create(project, "compiled-template");
        Assert.Equal(reference.StateHash(), compiled.StateHash());
        Command[] commands = [new MoveCommand("down"), new UseToolCommand("hoe"), new InteractCommand(), new CloseDialogueCommand(), new SleepCommand()];
        foreach (var command in commands)
        {
            reference.Apply(command);
            compiled.Apply(command);
            reference.Tick(30);
            compiled.Tick(30);
            Assert.Equal(reference.StateHash(), compiled.StateHash());
        }
    }

    [Fact]
    public void SavesRoundTripThroughTheRustSession()
    {
        if (!FarmFfi.IsAvailable)
        {
            return;
        }

        var project = Starter();
        using var session = RustSession.Create(project, "save");
        var before = session.StateHash();
        var save = session.Save();
        session.Apply(new SleepCommand());
        Assert.NotEqual(before, session.StateHash());

        var report = session.LoadSave(save);
        Assert.Equal(before, session.StateHash());
        Assert.Empty(report.Warnings);
        Assert.Empty(report.Quarantined);
        Assert.False(report.Migrated);

        // A save from another game is refused and the state stays put.
        using var other = RustSession.Create(project with { Id = "another-game" }, "save");
        var ex = Assert.Throws<FarmFfiException>(() => session.LoadSave(other.Save()));
        Assert.Contains("different game", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, session.StateHash());
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
    /// through both engines, hashes compared after every step.
    /// </summary>
    [Fact]
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
