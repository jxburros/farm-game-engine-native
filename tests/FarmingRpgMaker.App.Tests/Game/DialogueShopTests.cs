using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using FarmEngine.Core;
using FarmingRpgMaker.App.Game;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

public sealed class DialogueShopTests
{
    /// <summary>Stand below <paramref name="npcId"/> facing up (creator debug teleport), then talk.</summary>
    private static PlaySession TalkTo(GameTestHost host, string npcId)
    {
        host.EnterPlay();
        var session = host.Play.Session;
        var npc = session.Content.Npcs.First(n => n.Id == npcId);
        session.DebugMutate((s, _) => s with { Player = s.Player with { X = npc.X + 0.5, Y = npc.Y + 1.5, Direction = "up" } });
        host.Window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
        host.Frames(1);
        host.Window.KeyReleaseQwerty(PhysicalKey.E, RawInputModifiers.None);
        host.Frames(1);
        return session;
    }

    [AvaloniaFact]
    public void Dialogue_ShowsNpcTextAndVisibleOptions_AndChoosingAdvances()
    {
        using var host = new GameTestHost();
        var session = TalkTo(host, "npc-farmer");

        Assert.Equal("dialogue-farmer-greeting", session.State.Dialogue?.DialogueId);
        Assert.Equal("Old Farmer", FindByName<TextBlock>(host.Window, "DialogueNpcName").Text);
        Assert.StartsWith("Welcome to the farm!", FindByName<TextBlock>(host.Window, "DialogueText").Text, StringComparison.Ordinal);
        var options = FindByName<StackPanel>(host.Window, "DialogueOptions").Children.OfType<Button>().ToList();
        Assert.Equal(2, options.Count);

        // Movement is blocked while talking.
        host.Window.KeyPressQwerty(PhysicalKey.D, RawInputModifiers.None);
        host.Frames(5);
        Assert.Equal(0, session.State.Player.MoveIntent.Dx);
        host.Window.KeyReleaseQwerty(PhysicalKey.D, RawInputModifiers.None);

        Click(host.Window, options[1]); // "What crops grow best here?" → next dialogue
        Assert.Equal("dialogue-farmer-crops", session.State.Dialogue?.DialogueId);

        // Number keys pick options; the last option closes the conversation.
        host.Window.KeyPressQwerty(PhysicalKey.Digit1, RawInputModifiers.None);
        host.Frames(1);
        host.Window.KeyReleaseQwerty(PhysicalKey.Digit1, RawInputModifiers.None);
        Assert.Null(session.State.Dialogue);
        Assert.Null(TryFindByName<Border>(host.Window, "DialogueBox"));
    }

    [AvaloniaFact]
    public void Dialogue_EscapeCloses()
    {
        using var host = new GameTestHost();
        var session = TalkTo(host, "npc-farmer");
        Assert.NotNull(session.State.Dialogue);
        host.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        host.Frames(1);
        host.Window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.Null(session.State.Dialogue);
    }

    [AvaloniaFact]
    public void Shop_OpensFromDialogue_BuySellAndLeave()
    {
        using var host = new GameTestHost();
        var session = TalkTo(host, "npc-merchant");
        Click(host.Window, FindByName<StackPanel>(host.Window, "DialogueOptions").Children.OfType<Button>().First());
        Assert.Equal("shop-general", session.State.Shop?.ShopId);
        var shop = FindByName<Border>(host.Window, "ShopDialog");
        Assert.Contains("General Store", AllVisibleText(shop), StringComparison.Ordinal);
        Assert.Equal("Your money: $100", FindByName<TextBlock>(host.Window, "ShopDialogSubtitle").Text);

        // Buy one bag of wheat seeds ($ from the item value).
        var seedsBefore = session.State.Player.Inventory.Where(s => s.Item.Id == "seed-wheat").Sum(s => s.Quantity);
        Click(host.Window, FindByName<Button>(host.Window, "Buy_seed-wheat"));
        Assert.True(session.State.Player.Money < 100);
        Assert.Equal(seedsBefore + 1, session.State.Player.Inventory.Where(s => s.Item.Id == "seed-wheat").Sum(s => s.Quantity));
        Assert.Equal($"Your money: ${session.State.Player.Money}", FindByName<TextBlock>(host.Window, "ShopDialogSubtitle").Text);

        // Daily stock is shown for limited items.
        Assert.Contains("5/5 left today", AllVisibleText(FindByName<Border>(host.Window, "ShopDialog")), StringComparison.Ordinal);

        // Sell tab: sell one seed back.
        Click(host.Window, FindByName<Avalonia.Controls.Primitives.ToggleButton>(host.Window, "ShopTabSell"));
        var moneyBeforeSell = session.State.Player.Money;
        var sell = FindByName<Border>(host.Window, "ShopDialog").GetLogicalDescendantsOfType<Button>().First(b => AllVisibleText(b) == "Sell 1");
        Click(host.Window, sell);
        Assert.True(session.State.Player.Money > moneyBeforeSell);

        Click(host.Window, FindByName<Button>(host.Window, "ShopDialogClose"));
        Assert.Null(session.State.Shop);
        Assert.Null(TryFindByName<Border>(host.Window, "ShopDialog"));
    }
}
