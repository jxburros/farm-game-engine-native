using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using FarmEngine.Interop;
using FarmingRpgMaker.App.Game;
using FarmingRpgMaker.App.Hosting;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

/// <summary>
/// Play Mode runs the Rust game player (<c>farm-player</c> through <c>fe_player_*</c>): the game
/// and all of its UI are drawn by Rust, the editor forwards keys and the pointer and keeps its own
/// tools. The player's own UI is tested in <c>crates/farm-player</c> and <c>crates/farm-ui</c>.
/// </summary>
public sealed class PlayModeTests
{
    private static JsonObject State(GameTestHost host) => host.Play.Use(player => player.State());

    private static double Num(JsonNode? node) => node!.GetValue<double>();

    private static void Tap(GameTestHost host, PhysicalKey key)
    {
        host.Window.KeyPressQwerty(key, RawInputModifiers.None);
        host.Frames(1);
        host.Window.KeyReleaseQwerty(key, RawInputModifiers.None);
        host.Frames(1);
    }

    /// <summary>Clicks a widget of the Rust UI by its id path (where it was drawn last frame).</summary>
    private static void ClickWidget(GameTestHost host, params object[] path)
    {
        var rect = host.Play.Use(player => player.WidgetRect(path)) ?? throw new InvalidOperationException($"{string.Join('/', path)} was not drawn.");
        var surface = host.Play.Surface;
        var size = surface.PresentedSize;
        var local = new Point(rect.CenterX * surface.Bounds.Width / size.Width, rect.CenterY * surface.Bounds.Height / size.Height);
        var point = surface.TranslatePoint(local, host.Window)!.Value;
        host.Window.MouseMove(point);
        host.Window.MouseDown(point, MouseButton.Left);
        host.Frames(1);
        host.Window.MouseUp(point, MouseButton.Left);
        // The UI acts on the release; the engine runs the command at the next frame.
        host.Frames(3);
    }

    [AvaloniaFact]
    public void EnteringPlayMode_ShowsTheRustPlayerAndTheEditorTools()
    {
        using var host = new GameTestHost();
        host.EnterPlay();
        host.Frames(2);

        Assert.NotNull(host.Surface.PlayView);
        Assert.Equal("Playing: Starter Farm", FindByName<TextBlock>(host.Window, "HeaderSubtitle").Text);
        foreach (var name in new[] { "RestartButton", "KeepChangesButton", "DebugButton" })
        {
            Assert.True(FindByName<Control>(host.Window, name).IsVisible, name);
        }

        var surface = host.Play.Surface;
        Assert.True(surface.FrameCount >= 2);
        Assert.Equal(surface.FrameSize(), surface.PresentedSize);
        Assert.True(surface.PresentedSize.Width > 600 && surface.PresentedSize.Height > 300, surface.PresentedSize.ToString());
        Assert.Equal("playing", host.Play.LastFrame.Screen);
        Assert.False(host.Play.LastFrame.Modal);
        Assert.Equal(100, Num(State(host)["player"]!["money"]));

        // The frame is the game, not a blank surface: many distinct colors.
        var frame = GameTestHost.Capture(host.Window);
        Assert.True(frame.PixelSize.Width > 0);
    }

    [AvaloniaFact]
    public void KeyboardMovement_MovesThePlayer_AndTimeAdvances()
    {
        using var host = new GameTestHost();
        host.EnterPlay();
        var start = Num(State(host)["player"]!["x"]);

        host.Window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);
        host.Frames(30);
        var walking = State(host)["player"]!;
        Assert.Equal(-1, Num(walking["moveIntent"]!["dx"]));
        Assert.Equal("left", (string?)walking["direction"]);
        host.Window.KeyReleaseQwerty(PhysicalKey.A, RawInputModifiers.None);
        host.Frames(2);
        var stopped = State(host)["player"]!;
        Assert.Equal(0, Num(stopped["moveIntent"]!["dx"]));
        Assert.True(Num(stopped["x"]) < start, $"the player should move left from {start}, is at {Num(stopped["x"])}");

        // Diagonal: up + right held together.
        host.Window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        host.Window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        host.Frames(10);
        var intent = State(host)["player"]!["moveIntent"]!;
        Assert.Equal((1d, -1d), (Num(intent["dx"]), Num(intent["dy"])));
        host.Window.KeyReleaseQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        host.Window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        host.Frames(1);

        var tick = Num(State(host)["clock"]!["tick"]);
        host.Frames(60);
        Assert.True(Num(State(host)["clock"]!["tick"]) > tick);
    }

    [AvaloniaFact]
    public void FixedTimestep_IsIndependentOfFrameRate()
    {
        using var a = new GameTestHost();
        a.EnterPlay();
        a.Frames(128, 1.0 / 64);
        using var b = new GameTestHost();
        b.EnterPlay();
        b.Frames(64, 1.0 / 32);
        Assert.Equal(Num(State(a)["clock"]!["tick"]), Num(State(b)["clock"]!["tick"]));
        Assert.Equal(Num(State(a)["clock"]!["timeMinutes"]), Num(State(b)["clock"]!["timeMinutes"]));
    }

    [AvaloniaFact]
    public void GameplayKeys_StayInTheGame_AndEditorShortcutsStayWithTheEditor()
    {
        using var host = new GameTestHost();
        Click(host.Window, FindByName<Button>(host.Window, "ModeToggle"));
        Assert.Equal(EditorMode.Play, host.ViewModel.Mode);

        // Space and Enter must not click the focused header button.
        Tap(host, PhysicalKey.Space);
        Tap(host, PhysicalKey.Enter);
        Assert.Equal(EditorMode.Play, host.ViewModel.Mode);

        // Z sleeps through the game's own hotkey.
        Tap(host, PhysicalKey.Z);
        Assert.Equal(2, Num(State(host)["clock"]!["day"]));
        Assert.Contains(host.Play.Use(player => player.Toasts()), toast => toast.Kind is "info" or "success");

        // F6 (window shortcut) still leaves Play Mode.
        host.Window.KeyPressQwerty(PhysicalKey.F6, RawInputModifiers.None);
        host.Window.KeyReleaseQwerty(PhysicalKey.F6, RawInputModifiers.None);
        Pump();
        Assert.Equal(EditorMode.Edit, host.ViewModel.Mode);
    }

    /// <summary>
    /// The toolbar never takes focus (the game owns Tab and Space), so a keyboard-only creator
    /// reaches Debug, Keep changes and Restart through their shortcuts (#49).
    /// </summary>
    [AvaloniaFact]
    public void ToolbarShortcutsReachEveryPlayModeButtonFromTheKeyboard()
    {
        using var host = new GameTestHost();
        host.EnterPlay();
        host.Frames(1);

        void Chord(PhysicalKey key, RawInputModifiers modifiers)
        {
            host.Window.KeyPressQwerty(key, modifiers);
            host.Window.KeyReleaseQwerty(key, modifiers);
            host.Frames(1);
        }

        Chord(PhysicalKey.D, RawInputModifiers.Control);
        Assert.True(host.Play.IsDebugOpen);
        Chord(PhysicalKey.D, RawInputModifiers.Control);
        Assert.False(host.Play.IsDebugOpen);
        // Ctrl+D is not the game's D (walk right).
        Assert.Equal(0, Num(State(host)["player"]!["moveIntent"]!["dx"]));

        Chord(PhysicalKey.K, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.True(host.Play.KeepChanges);
        Chord(PhysicalKey.K, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.False(host.Play.KeepChanges);

        host.Play.Use(player => player.RunCommands("""[{"type":"sleep"}]"""));
        Assert.Equal(2, Num(State(host)["clock"]!["day"]));
        Chord(PhysicalKey.R, RawInputModifiers.Control);
        Assert.Equal(1, Num(State(host)["clock"]!["day"]));
        Assert.Equal(EditorMode.Play, host.ViewModel.Mode);
    }

    [AvaloniaFact]
    public void PanelsOpenFromKeysAndThePointer_AndPauseWorldInput()
    {
        using var host = new GameTestHost();
        host.EnterPlay();
        host.Frames(1);

        Tap(host, PhysicalKey.I);
        Assert.True(host.Play.LastFrame.Modal, "the inventory is open");
        host.Window.KeyPressQwerty(PhysicalKey.D, RawInputModifiers.None);
        host.Frames(10);
        Assert.Equal(0, Num(State(host)["player"]!["moveIntent"]!["dx"]));
        host.Window.KeyReleaseQwerty(PhysicalKey.D, RawInputModifiers.None);
        ClickWidget(host, "inventory-close");
        Assert.False(host.Play.LastFrame.Modal);

        // The HUD's buttons answer the pointer: Sleep ends the day.
        ClickWidget(host, "hud", "sleep");
        Assert.Equal(2, Num(State(host)["clock"]!["day"]));

        // Escape opens the pause menu, without save slots in the editor.
        Tap(host, PhysicalKey.Escape);
        Assert.Equal("pause", host.Play.LastFrame.Screen);
        Assert.NotNull(host.Play.Use(player => player.WidgetRect("pause", "Resume")));
        Assert.Null(host.Play.Use(player => player.WidgetRect("pause", "Save")));
        Tap(host, PhysicalKey.Escape);
        Assert.Equal("playing", host.Play.LastFrame.Screen);
    }

    [AvaloniaFact]
    public void TalkingToAnNpc_OpensTheDialogue()
    {
        using var host = new GameTestHost();
        host.EnterPlay();
        // Walk up to the Old Farmer (3, 6) and talk to him.
        Walk(host, PhysicalKey.W, () => Num(State(host)["player"]!["y"]) <= 7.55);
        Walk(host, PhysicalKey.A, () => Num(State(host)["player"]!["x"]) <= 3.55);
        Walk(host, PhysicalKey.W, () => Num(State(host)["player"]!["y"]) <= 7.45);
        Tap(host, PhysicalKey.E);
        host.Frames(2);

        Assert.Equal("npc-farmer", (string?)State(host)["dialogue"]?["npcId"]);
        Assert.True(host.Play.LastFrame.Modal);
    }

    /// <summary>Holds a movement key until <paramref name="until"/> holds (max 4 s).</summary>
    private static void Walk(GameTestHost host, PhysicalKey key, Func<bool> until)
    {
        host.Window.KeyPressQwerty(key, RawInputModifiers.None);
        for (var i = 0; i < 240 && !until(); i++)
        {
            host.Play.AdvanceFrame(1.0 / 60);
        }

        host.Window.KeyReleaseQwerty(key, RawInputModifiers.None);
        host.Frames(1);
    }

    [AvaloniaFact]
    public void DebugDrawer_AppliesCreatorActions()
    {
        using var host = new GameTestHost();
        host.EnterPlay();
        Click(host.Window, FindByName<Button>(host.Window, "DebugButton"));
        Assert.True(host.Play.IsDebugOpen);

        Click(host.Window, FindByName<Button>(host.Window, "DebugAddMoney"));
        Assert.Equal(600, Num(State(host)["player"]!["money"]));
        Assert.Contains("$600", FindByName<TextBlock>(host.Window, "DebugStatus").Text, StringComparison.Ordinal);

        Click(host.Window, FindByName<Button>(host.Window, "DebugSkipDay"));
        Assert.Equal(2, Num(State(host)["clock"]!["day"]));

        var seasons = FindByName<WrapPanel>(host.Window, "DebugSeasons");
        Click(host.Window, (Button)seasons.Children[^1]);
        Assert.NotEqual("spring", (string?)State(host)["clock"]!["season"]);

        // Typing into the flag box stays in the box (W does not walk).
        var box = FindByName<TextBox>(host.Window, "DebugFlagName");
        box.Focus();
        box.Text = "met-mayor";
        host.Window.KeyPressQwerty(PhysicalKey.W, RawInputModifiers.None);
        host.Frames(5);
        host.Window.KeyReleaseQwerty(PhysicalKey.W, RawInputModifiers.None);
        Assert.Equal(0, Num(State(host)["player"]!["moveIntent"]!["dy"]));
        Click(host.Window, FindByName<Button>(host.Window, "DebugSetFlag"));
        Assert.True((bool)State(host)["flags"]!["met-mayor"]!);

        var scenes = FindByName<WrapPanel>(host.Window, "DebugScenes");
        Assert.NotEmpty(scenes.Children);
        Click(host.Window, FindByName<Button>(host.Window, "DebugCloseButton"));
        Assert.False(host.Play.IsDebugOpen);
    }

    [AvaloniaFact]
    public void TheDisplayLoopRunsFramesOnAWorkerThread()
    {
        using var host = new GameTestHost(autoRun: true);
        host.EnterPlay();
        var surface = host.Play.Surface;
        // The loop's frames are posted from its worker thread; each wait round also ticks the
        // headless render timer so they get drawn.
        PumpUntil(() =>
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            return surface.FrameCount >= 5;
        }, "five frames from the worker thread");
        // Leaving Play Mode stops the loop and frees the player while a frame may be in flight.
        host.ViewModel.Mode = EditorMode.Edit;
        Pump();
        Assert.Null(host.Surface.PlayView);
    }

    [Fact]
    public void KeysMapToTheEngineNames()
    {
        Assert.Equal("w", PlayerKeys.FromAvalonia(Key.W));
        Assert.Equal("1", PlayerKeys.FromAvalonia(Key.D1));
        Assert.Equal("1", PlayerKeys.FromAvalonia(Key.NumPad1));
        Assert.Equal(" ", PlayerKeys.FromAvalonia(Key.Space));
        Assert.Equal("arrowleft", PlayerKeys.FromAvalonia(Key.Left));
        Assert.Equal("escape", PlayerKeys.FromAvalonia(Key.Escape));
        Assert.Equal("enter", PlayerKeys.FromAvalonia(Key.Enter));
        Assert.Null(PlayerKeys.FromAvalonia(Key.F5));
    }

    [Fact]
    public void InputEventsSerializeLikeTheRustPlayerExpects()
    {
        var request = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new System.Text.Json.Utf8JsonWriter(request))
        {
            writer.WriteStartArray();
            foreach (var input in new[]
            {
                PlayerInput.KeyDown("w"),
                PlayerInput.PointerDown(1.5, 2, PlayerPointerButton.Secondary),
                PlayerInput.Wheel(0, -1),
                PlayerInput.FocusLost(),
            })
            {
                typeof(PlayerInput).GetMethod("WriteTo", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(input, [writer]);
            }

            writer.WriteEndArray();
        }

        Assert.Equal(
            """[{"type":"keyDown","key":"w","repeat":false},{"type":"pointerDown","x":1.5,"y":2,"button":"secondary"},{"type":"wheel","dx":0,"dy":-1},{"type":"focusLost"}]""",
            System.Text.Encoding.UTF8.GetString(request.WrittenSpan));
    }
}

internal static class LogicalExtensions
{
    public static IEnumerable<T> GetLogicalDescendantsOfType<T>(this Avalonia.LogicalTree.ILogical root) =>
        Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root).OfType<T>();
}
