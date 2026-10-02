using System.Text.Json;
using FarmEngine.Authoring;
using FarmEngine.Authoring.Net;
using FarmEngine.Interop;
using FarmEngine.Schemas;

namespace FarmingRpgMaker.App.Tests.Interop;

/// <summary>
/// The headless Rust session (<c>fe_session_*</c>) and the F# cartridge compiler. The Rust tests
/// check the engine against the TypeScript goldens; these check the .NET side of the boundary.
/// They need the Rust library: a missing one fails them (see <see cref="NativeTests"/>).
/// </summary>
public sealed class RustSessionTests
{
    private static GameProject Starter() => ProjectCatalog.CreateInitialProject(0);

    [NativeFact]
    public void CreatedStateHashesLikeTheTypeScriptGolden()
    {
        // The content golden records the state hash of createGameState(project, "content:starter-farm")
        // (xxh3 over the canonical binary state since v9, docs/NUMERICS.md).
        using var golden = JsonDocument.Parse(File.ReadAllText(RepoFile("fixtures", "golden", "content", "starter-farm.json")));
        var project = RecordJson.Parse<GameProject>(golden.RootElement.GetProperty("project").GetRawText());
        using var session = RustSession.Create(project, "content:starter-farm");
        Assert.Equal(golden.RootElement.GetProperty("stateHash").GetString(), session.StateHash());
        Assert.Equal(project.Player.SceneId, (string?)session.State()["player"]!["sceneId"]);
    }

    [Fact]
    public void CompiledCartridgeRunsInRustAndUsesPersistentSaveIdentity()
    {
        var project = Starter().WithExport(ExportSettings.Default.WithGameId("local.test-farm").WithVersion("3.1.0").WithTitle("Test Farm"));
        var bytes = CartridgeCompiler.Compile(project);
        Assert.Equal(bytes, CartridgeCompiler.Compile(project));
        Assert.True(CartridgeReader.hasIdentifier(bytes));
        var cart = CartridgeReader.read(bytes).ResultValue;
        Assert.Equal(2u, cart.CartFormat);
        Assert.NotEmpty(cart.StartJson);
        Assert.NotEmpty(cart.PresentationJson);
        Assert.Equal("local.test-farm", cart.Info.GameId);
        Assert.Equal("3.1.0", cart.Info.Version);
        Assert.NotEmpty(cart.ContentJson);

        if (!NativeTests.Available()) return;
        using var fromCart = RustSession.CreateCartridge(bytes, "parity");
        using var fromProject = RustSession.Create(project, "parity");
        Assert.Equal(fromProject.StateHash(), fromCart.StateHash());
        using var saved = JsonDocument.Parse(fromCart.Save());
        Assert.Equal("local.test-farm", saved.RootElement.GetProperty("header").GetProperty("gameId").GetString());
        Assert.Equal("3.1.0", saved.RootElement.GetProperty("header").GetProperty("gameVersion").GetString());
        Assert.NotEmpty(fromCart.Apply("""[{"type":"sleep"}]"""));
        fromProject.Apply(new { type = "sleep" });
        Assert.Equal(fromProject.StateHash(), fromCart.StateHash());
        // A cartridge carries no editor project, so there is nothing to write play state back to.
        Assert.Throws<FarmFfiException>(() => fromCart.SyncedProject());
        Assert.Equal(2, fromProject.SyncedProject().CurrentDay);
        Assert.Throws<FarmFfiException>(() => RustSession.CreateCartridge([0, 0, 0, 0, (byte)'F', (byte)'G', (byte)'C', (byte)'T']));
    }

    [Theory]
    [InlineData(ProjectTemplates.Starter)]
    [InlineData(ProjectTemplates.Blank)]
    [InlineData(ProjectTemplates.Cozy)]
    [InlineData(ProjectTemplates.Quest)]
    public void CartridgesPlayLikeTheirProjectsForEveryTemplate(string template)
    {
        var project = ProjectCatalog.CreateProjectForTemplate(template, 0);
        var bytes = CartridgeCompiler.Compile(project);
        var cart = CartridgeReader.read(bytes).ResultValue;
        var content = RecordJson.Parse<GameContent>(cart.ContentJson);
        Assert.Equal(RecordJson.ToStableText(ProjectContent.Compile(project)), RecordJson.ToStableText(content));
        Assert.Equal(bytes, CartridgeCompiler.Compile(project));
        if (!NativeTests.Available()) return;

        using var compiled = RustSession.CreateCartridge(bytes, "compiled-template");
        using var reference = RustSession.Create(project, "compiled-template");
        Assert.Equal(reference.StateHash(), compiled.StateHash());
        string[] commands =
        [
            """{"type":"move","dir":"down"}""",
            """{"type":"useTool","tool":"hoe"}""",
            """{"type":"interact"}""",
            """{"type":"closeDialogue"}""",
            """{"type":"sleep"}""",
        ];
        foreach (var command in commands)
        {
            reference.Apply($"[{command}]");
            compiled.Apply($"[{command}]");
            reference.Tick(30);
            compiled.Tick(30);
            Assert.Equal(reference.StateHash(), compiled.StateHash());
        }
    }

    /// <summary>Keep changes: F# writing the engine's state back gives the project Rust's own
    /// <c>applyStateToProject</c> gives (Play Mode runs cartridges, so F# does it there).</summary>
    [Theory]
    [InlineData(ProjectTemplates.Starter)]
    [InlineData(ProjectTemplates.Blank)]
    [InlineData(ProjectTemplates.Cozy)]
    [InlineData(ProjectTemplates.Quest)]
    public void KeepChangesInFSharpMatchesTheRustWriteBack(string template)
    {
        if (!NativeTests.Available())
        {
            return;
        }

        var project = ProjectCatalog.CreateProjectForTemplate(template, 0);
        using var session = RustSession.Create(project, "keep");
        string[] commands =
        [
            """{"type":"move","dir":"down"}""",
            """{"type":"useTool","tool":"hoe"}""",
            """{"type":"interact"}""",
            """{"type":"sleep"}""",
            """{"type":"move","dir":"right"}""",
        ];
        foreach (var command in commands)
        {
            session.Apply($"[{command}]");
            session.Tick(30);
            Assert.Equal(
                RecordJson.ToStableText(session.SyncedProject()),
                RecordJson.ToStableText(Playtests.ApplyState(project, session.StateJson())));
        }

        Assert.Throws<FormatException>(() => Playtests.ApplyState(project, "[]"));
    }

    [NativeFact]
    public void SavesRoundTripThroughTheRustSession()
    {
        var project = Starter();
        using var session = RustSession.Create(project, "save");
        var before = session.StateHash();
        var save = session.Save();
        session.Apply(new { type = "sleep" });
        Assert.NotEqual(before, session.StateHash());

        var report = session.LoadSave(save);
        Assert.Equal(before, session.StateHash());
        Assert.Empty(report.Warnings);
        Assert.Empty(report.Quarantined);
        Assert.False(report.Migrated);

        // A save from another game is refused and the state stays put.
        using var other = RustSession.Create(project.WithId("another-game"), "save");
        var ex = Assert.Throws<FarmFfiException>(() => session.LoadSave(other.Save()));
        Assert.Contains("different game", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, session.StateHash());

        // Creator tools replace the state as it is.
        var state = session.State();
        state["player"]!["money"] = 4321;
        session.SetState(state.ToJsonString());
        Assert.Equal(4321, (double)session.State()["player"]!["money"]!);
        Assert.Throws<FarmFfiException>(() => session.SetState("""{"player": 5}"""));
        Assert.Equal(4321, (double)session.State()["player"]!["money"]!);
    }

    [NativeFact]
    public void RejectsUnparsableProjects()
    {
        // A project whose scenes are not a list cannot be a GameProject.
        var broken = System.Text.Encoding.UTF8.GetBytes("""{"scenes": 5}""");
        var ex = Assert.Throws<FarmFfiException>(() => RustSession.CreateCartridge(broken));
        Assert.Contains("fe_session_new failed", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A seeded random command stream replays to the same hash (the session is deterministic).</summary>
    [NativeFact]
    public void RandomCommandStreamsReplayIdentically()
    {
        string Run()
        {
            using var session = RustSession.Create(Starter(), "fuzz", autoStartQuests: true);
            var random = new Random(12345);
            string[] dirs = ["up", "down", "left", "right"];
            string[] tools = ["hoe", "watering-can", "axe", "pickaxe", "scythe"];
            for (var step = 0; step < 200; step++)
            {
                object command = random.Next(8) switch
                {
                    0 => new { type = "setMoveIntent", dx = random.Next(-1, 2), dy = random.Next(-1, 2) },
                    1 => new { type = "move", dir = dirs[random.Next(dirs.Length)] },
                    2 => new { type = "useTool", tool = tools[random.Next(tools.Length)] },
                    3 => new { type = "interact" },
                    4 => new { type = "sleep" },
                    5 => new { type = "chooseDialogueOption", index = random.Next(3) },
                    _ => new { type = "closeDialogue" },
                };
                session.Apply(command);
                session.Tick((uint)random.Next(0, 40));
            }

            return session.StateHash();
        }

        Assert.Equal(Run(), Run());
    }

    internal static string RepoFile(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FarmingRpgMaker.sln")))
            {
                return Path.Combine([dir.FullName, .. parts]);
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
