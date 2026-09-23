using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using FarmEngine.Core;
using FarmEngine.Runtime;
using FarmEngine.Schemas;
using FarmingRpgMaker.App.Game;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

public sealed class MinigameAndPanelsTests
{
    [AvaloniaFact]
    public void TimingBarMinigame_MountsAdvancesAndResolvesWithSpace()
    {
        using var host = new GameTestHost();
        host.EnterPlay();
        var session = host.Play.Session;

        session.RunCommand(new StartMinigameCommand("fishing"));
        host.Frames(1);
        Assert.NotNull(session.State.Minigame);
        var bar = Assert.IsType<TimingBarSession>(host.Play.Minigame);
        Assert.Contains("Hook the fish", AllVisibleText(FindByName<Border>(host.Window, "MinigameOverlay")), StringComparison.Ordinal);

        var before = bar.Position;
        host.Frames(10);
        Assert.NotEqual(before, bar.Position);

        host.Window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        host.Frames(1);
        host.Window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        host.Frames(1);

        Assert.True(bar.IsDone);
        Assert.NotNull(bar.Score);
        Assert.Null(session.State.Minigame);
        Assert.Null(host.Play.Minigame);
        Assert.Null(TryFindByName<Border>(host.Window, "MinigameOverlay"));
    }

    [AvaloniaFact]
    public void Minigame_GiveUpCancels()
    {
        using var host = new GameTestHost();
        host.EnterPlay();
        var session = host.Play.Session;
        session.RunCommand(new StartMinigameCommand("fishing"));
        host.Frames(1);
        Click(host.Window, FindByName<Button>(host.Window, "MinigameOverlayClose"));
        Assert.Null(session.State.Minigame);
    }

    [AvaloniaFact]
    public void CreatorGamePanels_ShowLiveValues_AndRunActions()
    {
        using var host = new GameTestHost();
        var project = host.Workspace.Current! with
        {
            GamePanels =
            [
                new GamePanel
                {
                    Id = "hud",
                    Title = "Journal",
                    Entries =
                    [
                        new GamePanelEntry { Kind = "money", Label = "Gold", Value = "" },
                        new GamePanelEntry { Kind = "item", Label = "Seeds", Value = "seed-wheat" },
                        new GamePanelEntry { Kind = "action", Label = "Forage", Value = "action-forage-snack" },
                    ],
                },
            ],
        };
        host.Workspace.Open(project);
        host.EnterPlay();

        var panels = FindByName<WrapPanel>(host.Window, "GamePanels");
        var text = AllVisibleText(panels);
        Assert.Contains("Journal", text, StringComparison.Ordinal);
        Assert.Contains("Gold: 100", text, StringComparison.Ordinal);
        Assert.Contains("Seeds: 10", text, StringComparison.Ordinal);

        var forage = panels.GetLogicalDescendantsOfType<Button>().Single();
        var toasts = host.Play.Toasts.History.Count;
        Click(host.Window, forage);
        Assert.True(host.Play.Toasts.History.Count > toasts, "performing the action reports back through an effect");
    }

    [AvaloniaFact]
    public void EnabledPackPlugins_RunInTheSandbox_AndTheirMutationsEnterAsCommands()
    {
        using var host = new GameTestHost();
        var pack = FarmEngine.Json.JsonDefaults.Deserialize<ContentPack>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "demo-mod.json")))!;
        host.Workspace.Open(host.Workspace.Current! with { ContentPacks = [new PackInstallation { Pack = pack, Enabled = true }] });
        host.EnterPlay();
        var session = host.Play.Session;
        Assert.True(session.HasPlugins);

        host.Play.Run(new SleepCommand()); // onDayStart → plugin queues a message mutation
        host.Frames(1);                    // drained at the frame's fixed point

        Assert.Contains(host.Play.Toasts.History, t => t.Text.Contains("glowshrooms hum softly on day 2", StringComparison.Ordinal));
        Assert.Empty(session.PluginErrors);
    }

    [AvaloniaFact]
    public void Effects_PlayMappedSounds()
    {
        using var host = new GameTestHost();
        var audio = new RuntimeGameAudio(new AudioManager());
        using var session = new PlaySession(host.Workspace.Current!, audio);
        session.RunCommand(new SleepCommand());
        Assert.Contains("sleep", audio.Played);
    }
}
