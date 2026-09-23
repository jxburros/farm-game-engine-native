using System.Text.Json;
using FarmEngine.Json;
using FarmEngine.Runtime;
using FarmEngine.Schemas;

namespace FarmEngine.Core.Tests.Runtime;

/// <summary>Port of creator-runtime.test.ts (jsdom widgets → host-agnostic session / panel models).</summary>
public class CreatorRuntimeTests
{
    [Fact]
    public void RegistersFishingAndCombatInTheSameRegistryUsedByBothHosts()
    {
        var registry = Minigames.CreateDefaultMinigameRegistry();
        Assert.NotNull(registry.Get("hold-to-catch"));
        Assert.NotNull(registry.Get("simple-battle"));
    }

    [Fact]
    public void BattleResolvesOnceAndCleansUpItsUi()
    {
        var complete = new List<double>();
        var session = Minigames.CreateDefaultMinigameRegistry().Get("simple-battle")!.Mount(
            new MinigameMountOptions(new Dictionary<string, JsonElement> { ["enemyHealth"] = Js.Value(10) }, complete.Add, () => { }));
        var battle = Assert.IsType<SimpleBattleSession>(session);
        battle.Act("magic");
        battle.Act("magic");
        Assert.Equal([1.0], complete);
        session.Dispose();
        Assert.True(session.IsDone);
    }

    [Fact]
    public void FishingKeyboardHoldReportsScoreAndCannotResolveTwice()
    {
        var complete = new List<double>();
        var session = Minigames.CreateDefaultMinigameRegistry().Get("hold-to-catch")!.Mount(
            new MinigameMountOptions(new Dictionary<string, JsonElement> { ["holdMs"] = Js.Value(1200) }, complete.Add, () => { }));
        Assert.Equal("Hold for 1.2 seconds, then release to reel in.", session.Prompt);
        session.Press();
        Assert.Equal("Reeling… release!", session.ButtonText);
        session.Update(1.2);
        session.Release();
        session.Release();
        Assert.Equal([1.0], complete);
        session.Dispose();
        Assert.True(session.IsDone);
    }

    [Fact]
    public void GamePanelsUpdateCountersRespectFlagsAndBlockActionsDuringModalInteractions()
    {
        var ran = new List<string>();
        var flags = new Dictionary<string, JsonElement> { ["known"] = Js.Value(false) };
        var state = new PanelState(3, 100, 1, flags, [], Blocked: false);
        GamePanel[] panels =
        [
            new GamePanel
            {
                Id = "p",
                Title = "Journal",
                VisibleFlag = "known",
                Entries =
                [
                    new GamePanelEntry { Kind = "money", Label = "Gold", Value = "" },
                    new GamePanelEntry { Kind = "action", Label = "Cast", Value = "rain" },
                ],
            },
        ];
        var handle = GamePanels.Mount(panels, () => state, ran.Add);
        handle.Update();
        Assert.True(handle.Views.Single().Hidden);

        flags["known"] = Js.Value(true);
        state = state with { Money = 9 };
        handle.Update();
        Assert.False(handle.Views.Single().Hidden);
        Assert.Contains(handle.Views.Single().Entries, e => e.Text == "Gold: 9");
        Assert.True(handle.Click("rain"));
        Assert.Equal(["rain"], ran);

        state = state with { Blocked = true };
        handle.Update();
        Assert.False(handle.Views.Single().Entries.Single(e => e.Kind == "action").Enabled);
        Assert.False(handle.Click("rain"));
        Assert.Single(ran);

        handle.Dispose();
        Assert.Empty(handle.Views);
    }

    [Fact]
    public void PanelEntriesRenderFlagsItemsAndText()
    {
        var items = FarmEngine.Core.ContentBuiltin.CreateDefaultItems();
        var wheat = items.First(i => i.Id == "seed-wheat");
        var state = new PanelState(0, 42.5, 3,
            new Dictionary<string, JsonElement> { ["met"] = Js.Value("yes") },
            [new InventorySlot { Item = wheat, Quantity = 4 }, new InventorySlot { Item = wheat, Quantity = 2 }]);
        var view = GamePanels.Render([
            new GamePanel
            {
                Id = "p", Title = "T",
                Entries =
                [
                    new GamePanelEntry { Kind = "flag", Label = "Met", Value = "met" },
                    new GamePanelEntry { Kind = "flag", Label = "Other", Value = "nope" },
                    new GamePanelEntry { Kind = "item", Label = "Seeds", Value = "seed-wheat" },
                    new GamePanelEntry { Kind = "energy", Label = "", Value = "" },
                    new GamePanelEntry { Kind = "day", Label = "Day", Value = "" },
                    new GamePanelEntry { Kind = "text", Label = "Tip", Value = "Water daily" },
                ],
            },
        ], state).Single();
        Assert.Equal(["Met: Yes", "Other: No", "Seeds: 6", "42.5", "Day: 3", "Tip: Water daily"], view.Entries.Select(e => e.Text));
    }

    [Fact]
    public void PanelStateProjectsTheRunningGameAndTheSyncedProject()
    {
        var project = FarmEngine.Core.Tests.Core.EngineTests.MakeProject() with
        {
            EventFlags = new OrderedDictionary<string, bool> { ["met"] = true },
        };
        var state = FarmEngine.Core.EngineState.CreateGameState(project, seed: "panels");
        var live = PanelState.FromGameState(state with { Shop = new ShopSession { ShopId = "s" } });
        Assert.Equal((100.0, 1.0, true), (live.Money, live.Day, live.Blocked));
        Assert.False(PanelState.FromGameState(state).Blocked);
        Assert.True(PanelState.FromGameState(state, hostModalOpen: true).Blocked);

        var synced = PanelState.FromProject(project, blocked: false);
        Assert.Equal((100.0, 0.0, 1.0), (synced.Money, synced.Energy, synced.Day));
        Assert.True(Js.Truthy(synced.Flags["met"]));
    }
}
