using FarmEngine.Core.Tests.Core;
using FarmEngine.Runtime;
using FarmEngine.Schemas;

namespace FarmEngine.Core.Tests.Runtime;

/// <summary>Port of input.test.ts (DOM events → the host-agnostic KeyDown/KeyUp feed).</summary>
public class InputTests
{
    [Fact]
    public void TracksHeldAndJustPressedKeysFromWindowEvents()
    {
        var input = new InputManager();
        input.KeyDown("w");
        Assert.True(input.Pressed("w"));
        Assert.True(input.JustPressed("w"));
        Assert.Equal("up", input.Direction());

        input.EndFrame();
        Assert.False(input.JustPressed("w"));
        Assert.True(input.Pressed("w")); // still held

        input.KeyUp("w");
        Assert.False(input.Pressed("w"));
    }

    [Fact]
    public void PreventsDefaultForGameplayKeysOnNonEditableTargets()
    {
        var input = new InputManager();
        Assert.True(input.KeyDown("e"));
    }

    [Fact]
    public void IgnoresKeystrokesAimedAtTextInputsEditorTypingRegression()
    {
        var input = new InputManager();
        // Every gameplay binding must pass through untouched so users can type
        // words like "sara ward" into editor fields.
        foreach (var key in new[] { "w", "a", "s", "d", "e", "q", "t", "x", "z", "i", "j", " ", "Enter" })
        {
            Assert.False(input.KeyDown(key, editableTarget: true));
            Assert.False(input.Pressed(key.ToLowerInvariant()));
        }
    }

    [Fact]
    public void IgnoresKeystrokesAimedAtTextareasAndContenteditableElements()
    {
        var input = new InputManager();
        Assert.False(input.KeyDown("w", editableTarget: true));
        Assert.False(input.Pressed("w"));
        Assert.False(input.KeyDown("a", editableTarget: true));
        Assert.False(input.Pressed("a"));
    }

    [Fact]
    public void LeavesModifiedShortcutsCtrlCmdAltForTheBrowserAndApp()
    {
        var input = new InputManager();
        Assert.False(input.KeyDown("z", ctrl: true));
        Assert.False(input.KeyDown("s", meta: true));
        Assert.False(input.KeyDown("d", alt: true));
    }

    // --- C# additions: key-name translation, movement vector, play bindings ---

    [Theory]
    [InlineData("W", "w")]
    [InlineData("Up", "arrowup")]
    [InlineData("Down", "arrowdown")]
    [InlineData("Left", "arrowleft")]
    [InlineData("Right", "arrowright")]
    [InlineData("Space", " ")]
    [InlineData("Enter", "enter")]
    [InlineData("Return", "enter")]
    [InlineData("Escape", "escape")]
    [InlineData("E", "e")]
    [InlineData("D1", "1")]
    [InlineData("NumPad7", "7")]
    [InlineData("F5", "f5")]
    [InlineData("OemComma", ",")]
    public void MapsAvaloniaKeyNames(string avalonia, string expected) => Assert.Equal(expected, KeyNames.FromAvalonia(avalonia));

    [Theory]
    [InlineData("KeyW", "w")]
    [InlineData("ArrowUp", "arrowup")]
    [InlineData("Space", " ")]
    [InlineData("Enter", "enter")]
    [InlineData("Digit3", "3")]
    [InlineData("Escape", "escape")]
    public void MapsDomCodes(string code, string expected) => Assert.Equal(expected, KeyNames.FromDomCode(code));

    [Fact]
    public void UnknownHostKeysAreIgnored()
    {
        var input = new InputManager();
        Assert.Null(KeyNames.FromAvalonia("NoName"));
        Assert.False(input.KeyDownAvalonia("NoName"));
        Assert.Empty(input.KeysDown);
        Assert.True(input.KeyDownAvalonia("Up"));
        Assert.True(input.Pressed("arrowup"));
        input.KeyUpAvalonia("Up");
        Assert.DoesNotContain("arrowup", input.KeysDown);
    }

    [Fact]
    public void MoveVectorCombinesAxesAndCancelsOpposingKeys()
    {
        var input = new InputManager();
        input.KeyDown("d");
        input.KeyDown("ArrowUp");
        Assert.Equal(new MoveVector(1, -1), input.MoveVector());
        input.KeyDown("a");
        Assert.Equal(new MoveVector(0, -1), input.MoveVector());
        input.Clear();
        Assert.Equal(new MoveVector(0, 0), input.MoveVector());
        Assert.Null(input.Direction());
    }

    private static (GameState State, GameContent Content) Play(Func<GameProject, GameProject>? mutate = null)
    {
        var project = EngineTests.MakeProject();
        if (mutate is not null) project = mutate(project);
        return (EngineState.CreateGameState(project, seed: "input"), EngineState.CreateContentFromProject(project));
    }

    [Fact]
    public void PollPlayFrameMapsGameplayKeysToCommands()
    {
        var (state, content) = Play(p => p with
        {
            Actions =
            [
                new ActionDef { Id = "action-ok", Name = "Ok", Hotkey = "K" },
                new ActionDef { Id = "action-reserved", Name = "Reserved", Hotkey = "e" },
            ],
        });
        var input = new InputManager();
        foreach (var key in new[] { " ", "q", "t", "r", "f", "c", "z", "i", "j", "x", "k" }) input.KeyDown(key);

        var frame = InputBindings.PollPlayFrame(input, state, content);
        Assert.Equal(
            ["interact", "useTool", "useTool", "useTool", "useTool", "useTool", "sleep", "performAction"],
            frame.Commands.Select(c => c.Type));
        Assert.Equal(["watering-can", "hoe", "axe", "pickaxe", "scythe"], frame.Commands.OfType<UseToolCommand>().Select(c => c.Tool));
        Assert.Equal("action-ok", frame.Commands.OfType<PerformActionCommand>().Single().ActionId);
        Assert.True(frame.ToggleInventory && frame.ToggleQuests && frame.ToggleCrafting);
        Assert.False(frame.Escape);
    }

    [Fact]
    public void PollPlayFrameOnlyHandlesEscapeWhileAModalIsOpen()
    {
        var (state, content) = Play();
        var shopOpen = state with { Shop = new ShopSession { ShopId = "shop" } };
        var input = new InputManager();
        input.KeyDown("e");
        input.KeyDown("Escape");
        var frame = InputBindings.PollPlayFrame(input, shopOpen, content);
        Assert.IsType<CloseShopCommand>(Assert.Single(frame.Commands));
        Assert.Equal(new MoveVector(0, 0), InputBindings.MoveIntent(input, shopOpen));

        var hostModal = InputBindings.PollPlayFrame(input, shopOpen, content, hostModalOpen: true);
        Assert.Empty(hostModal.Commands);
        Assert.True(hostModal.Escape);
    }

    [Fact]
    public void MoveIntentAddsTouchVectorAndClamps()
    {
        var (state, _) = Play();
        var input = new InputManager();
        input.KeyDown("d");
        Assert.Equal(new MoveVector(1, 1), InputBindings.MoveIntent(input, state, new MoveVector(1, 1)));
    }
}
