using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using FarmEngine.Core;
using FarmingRpgMaker.App.Game;
using FarmingRpgMaker.App.Hosting;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

public sealed class PlayModeTests
{
    [AvaloniaFact]
    public void EnteringPlayMode_ShowsHudToolbarAndCanvas()
    {
        using var host = new GameTestHost();
        host.EnterPlay();

        Assert.NotNull(host.Surface.PlayView);
        Assert.Equal("Playing: Starter Farm", FindByName<TextBlock>(host.Window, "HeaderSubtitle").Text);
        Assert.Equal("$100", FindByName<TextBlock>(host.Window, "HudMoney").Text);
        Assert.Equal("6:00 AM", FindByName<TextBlock>(host.Window, "HudTime").Text);
        Assert.Equal("1 / 28", FindByName<TextBlock>(host.Window, "HudDay").Text);
        Assert.Equal("Spring", FindByName<TextBlock>(host.Window, "HudSeason").Text);
        Assert.Contains("Inventory (6/20)", AllVisibleText(FindByName<Button>(host.Window, "InventoryButton")), StringComparison.Ordinal);
        Assert.NotNull(host.Play.Canvas.Snapshot);
        Assert.NotNull(host.Play.Canvas.Snapshot!.Camera);
    }

    [AvaloniaFact]
    public void KeyboardMovement_MovesThePlayer_AndTimeAdvances()
    {
        using var host = new GameTestHost();
        host.EnterPlay();
        var session = host.Play.Session;
        var start = (session.State.Player.X, session.State.Player.Y);

        host.Window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);
        host.Frames(30);
        Assert.Equal(-1, session.State.Player.MoveIntent.Dx);
        Assert.Equal("left", session.State.Player.Direction);
        host.Window.KeyReleaseQwerty(PhysicalKey.A, RawInputModifiers.None);
        host.Frames(2);
        Assert.Equal(0, session.State.Player.MoveIntent.Dx);
        Assert.True(session.State.Player.X < start.X, $"tick {session.State.Clock.Tick} player should move left from {start.X}, is at {session.State.Player.X}");

        // Diagonal: up + right held together.
        host.Window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        host.Window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        host.Frames(10);
        Assert.Equal((1d, -1d), (session.State.Player.MoveIntent.Dx, session.State.Player.MoveIntent.Dy));
        host.Window.KeyReleaseQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        host.Window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        host.Frames(1);

        // One minute of game time per real second in the starter farm.
        host.Frames(130);
        Assert.Equal("6:02 AM", FindByName<TextBlock>(host.Window, "HudTime").Text);
        Assert.Equal(57, session.State.Clock.Tick);
    }

    [AvaloniaFact]
    public void GameplayKeys_DoNotActivateTheFocusedHeaderButton()
    {
        using var host = new GameTestHost();
        Click(host.Window, FindByName<Button>(host.Window, "ModeToggle"));
        Assert.Equal(EditorMode.Play, host.ViewModel.Mode);

        host.Window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        host.Frames(1);
        host.Window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        host.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        host.Frames(1);
        host.Window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Pump();

        Assert.Equal(EditorMode.Play, host.ViewModel.Mode);

        // F6 (window shortcut) still leaves Play Mode.
        host.Window.KeyPressQwerty(PhysicalKey.F6, RawInputModifiers.None);
        host.Window.KeyReleaseQwerty(PhysicalKey.F6, RawInputModifiers.None);
        Pump();
        Assert.Equal(EditorMode.Edit, host.ViewModel.Mode);
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
        Assert.Equal(a.Play.Session.State.Clock.Tick, b.Play.Session.State.Clock.Tick);
        Assert.Equal(a.Play.Session.State.Clock.TimeMinutes, b.Play.Session.State.Clock.TimeMinutes);
        Assert.Equal(40, a.Play.Session.State.Clock.Tick);
    }

    [AvaloniaFact]
    public void ToolAndSleepHotkeys_RunCommands_AndMessagesBecomeToasts()
    {
        using var host = new GameTestHost();
        host.EnterPlay();
        var session = host.Play.Session;

        host.Window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.None);
        host.Frames(1);
        host.Window.KeyReleaseQwerty(PhysicalKey.Z, RawInputModifiers.None);
        host.Frames(1);

        Assert.Equal(2, session.State.Clock.Day);
        Assert.Equal("2 / 28", FindByName<TextBlock>(host.Window, "HudDay").Text);
        Assert.NotEmpty(host.Play.Toasts.History);

        // Tilling grass with T reports through a toast (success or error, from the engine).
        var before = host.Play.Toasts.History.Count;
        host.Window.KeyPressQwerty(PhysicalKey.T, RawInputModifiers.None);
        host.Frames(1);
        host.Window.KeyReleaseQwerty(PhysicalKey.T, RawInputModifiers.None);
        Assert.True(host.Play.Toasts.History.Count >= before);
    }

    [AvaloniaFact]
    public void PanelsToggleWithHotkeys_AndPauseWorldInput()
    {
        using var host = new GameTestHost();
        host.EnterPlay();
        var session = host.Play.Session;

        host.Window.KeyPressQwerty(PhysicalKey.I, RawInputModifiers.None);
        host.Frames(1);
        host.Window.KeyReleaseQwerty(PhysicalKey.I, RawInputModifiers.None);
        Assert.Equal(HostPanel.Inventory, host.Play.OpenPanel);
        Assert.Contains("Wheat Seeds", AllVisibleText(FindByName<Border>(host.Window, "InventoryPanel")), StringComparison.Ordinal);

        // Movement keys do nothing while a panel is open.
        host.Window.KeyPressQwerty(PhysicalKey.D, RawInputModifiers.None);
        host.Frames(10);
        Assert.Equal(0, session.State.Player.MoveIntent.Dx);
        host.Window.KeyReleaseQwerty(PhysicalKey.D, RawInputModifiers.None);

        host.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        host.Frames(1);
        host.Window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.Equal(HostPanel.None, host.Play.OpenPanel);

        // The panel's own hotkey toggles it closed again.
        host.Window.KeyPressQwerty(PhysicalKey.X, RawInputModifiers.None);
        host.Frames(1);
        host.Window.KeyReleaseQwerty(PhysicalKey.X, RawInputModifiers.None);
        Assert.Equal(HostPanel.Crafting, host.Play.OpenPanel);
        host.Window.KeyPressQwerty(PhysicalKey.X, RawInputModifiers.None);
        host.Frames(1);
        host.Window.KeyReleaseQwerty(PhysicalKey.X, RawInputModifiers.None);
        Assert.Equal(HostPanel.None, host.Play.OpenPanel);

        host.Window.KeyPressQwerty(PhysicalKey.J, RawInputModifiers.None);
        host.Frames(1);
        host.Window.KeyReleaseQwerty(PhysicalKey.J, RawInputModifiers.None);
        Assert.Equal(HostPanel.Quests, host.Play.OpenPanel);
        Assert.Contains("Quest Log", AllVisibleText(FindByName<Border>(host.Window, "QuestLog")), StringComparison.Ordinal);

        Click(host.Window, FindByName<Button>(host.Window, "CraftButton"));
        Assert.Equal(HostPanel.Crafting, host.Play.OpenPanel);
        Assert.Contains("HAND CRAFTING", AllVisibleText(FindByName<Border>(host.Window, "CraftingDialog")), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void DebugDrawer_AppliesCreatorMutations()
    {
        using var host = new GameTestHost();
        host.EnterPlay();
        Click(host.Window, FindByName<Button>(host.Window, "DebugButton"));
        Assert.True(host.Play.IsDebugOpen);
        var drawer = FindByName<Border>(host.Window, "DebugDrawer");
        var plus = drawer.GetLogicalDescendantsOfType<Button>().First(b => AllVisibleText(b) == "+$500");
        Click(host.Window, plus);
        Assert.Equal(600, host.Play.Session.State.Player.Money);
        Assert.Equal("$600", FindByName<TextBlock>(host.Window, "HudMoney").Text);
    }

    [AvaloniaFact]
    public void SessionEffects_MapToToastKinds()
    {
        using var host = new GameTestHost();
        host.EnterPlay();
        var session = host.Play.Session;
        session.RunCommand(new UseToolCommand("pickaxe"));
        session.RunCommand(new SleepCommand());
        Assert.Contains(host.Play.Toasts.History, t => t.Kind is ToastKind.Error or ToastKind.Info or ToastKind.Success);
        Assert.All(host.Play.Toasts.VisibleTexts, text => Assert.False(string.IsNullOrEmpty(text)));
        Assert.True(host.Play.Toasts.Children.Count <= ToastHost.MaxVisible);
    }
}

internal static class LogicalExtensions
{
    public static IEnumerable<T> GetLogicalDescendantsOfType<T>(this Avalonia.LogicalTree.ILogical root) =>
        Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root).OfType<T>();
}
