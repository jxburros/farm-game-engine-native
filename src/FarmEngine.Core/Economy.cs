using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>
/// Economy &amp; shops (M2) — port of economy.ts. Shops are content; buying and
/// selling are explicit commands (harvesting no longer auto-sells). Any NPC can
/// be a merchant via a dialogue option with <c>openShopId</c>.
/// </summary>
public static class Economy
{
    public static ShopDefinition? FindShop(EngineContext ctx, string shopId) =>
        ctx.Content.Shops.FirstOrDefault(shop => shop.Id == shopId);

    public static EngineStep HandleOpenShop(EngineContext ctx, GameState state, string shopId)
    {
        var shop = FindShop(ctx, shopId);
        if (shop is null)
        {
            return EngineStep.Of(state, Effect.Message(MessageLevels.Error, "That shop does not exist."));
        }
        return EngineStep.Of(state with { Shop = new ShopSession { ShopId = shopId }, Dialogue = null });
    }

    public static EngineStep HandleCloseShop(GameState state)
    {
        if (state.Shop is null) return EngineStep.Of(state);
        return EngineStep.Of(state with { Shop = null });
    }

    /// <summary>Units of an item still purchasable today under a stock entry's daily limit.</summary>
    public static double RemainingDailyStock(GameState state, string shopId, string itemId, double? dailyLimit = null)
    {
        // `!dailyLimit`: undefined, 0 and NaN all mean unlimited.
        if (dailyLimit is not { } limit || limit == 0 || double.IsNaN(limit)) return double.PositiveInfinity;
        var bought = PurchasedToday(state, shopId, itemId) ?? 0;
        return Math.Max(0, limit - bought);
    }

    /// <summary>TS <c>state.shopPurchasesToday[shopId]?.[itemId]</c>.</summary>
    private static double? PurchasedToday(GameState state, string shopId, string itemId) =>
        state.ShopPurchasesToday.TryGetValue(shopId, out var perItem) && perItem.TryGetValue(itemId, out var count) ? count : null;

    public static EngineStep HandleBuyItem(EngineContext ctx, GameState state, string itemId, double quantity)
    {
        if (state.Shop is null) return EngineStep.Of(state, Effect.Message(MessageLevels.Error, "No shop is open."));
        if (quantity <= 0) return EngineStep.Of(state);

        var shop = FindShop(ctx, state.Shop.ShopId);
        if (shop is null) return EngineStep.Of(state, Effect.Message(MessageLevels.Error, "No shop is open."));

        var entry = shop.Stock.FirstOrDefault(stockEntry => stockEntry.ItemId == itemId);
        if (entry is null) return EngineStep.Of(state, Effect.Message(MessageLevels.Error, "Not sold here."));

        if (entry.Seasons is { Count: > 0 } seasons && !seasons.Contains(state.Clock.Season))
        {
            return EngineStep.Of(state, Effect.Message(MessageLevels.Error, $"Not available in {state.Clock.Season}."));
        }

        var remaining = RemainingDailyStock(state, shop.Id, itemId, entry.DailyLimit);
        if (quantity > remaining)
        {
            return EngineStep.Of(state, Effect.Message(MessageLevels.Error, remaining == 0 ? "Sold out for today!" : $"Only {Js.Num(remaining)} left today."));
        }

        var item = ctx.Content.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null) return EngineStep.Of(state, Effect.Message(MessageLevels.Error, "Unknown item."));

        var price = entry.Price ?? item.Value;
        var total = price * quantity;
        if (state.Player.Money < total)
        {
            return EngineStep.Of(state, Effect.Message(MessageLevels.Error, "Not enough money!"));
        }

        var result = Inventory.AddItem(state.Player.Inventory, item, quantity, state.Player.MaxInventorySize);
        if (!result.Added)
        {
            return EngineStep.Of(state, Effect.Message(MessageLevels.Error, "Inventory is full!"));
        }

        var shopPurchases = entry.DailyLimit is { } dailyLimit && dailyLimit != 0 && !double.IsNaN(dailyLimit)
            ? new OrderedDictionary<string, OrderedDictionary<string, double>>(state.ShopPurchasesToday)
            {
                [shop.Id] = new OrderedDictionary<string, double>(
                    state.ShopPurchasesToday.TryGetValue(shop.Id, out var perItem) ? perItem : [])
                {
                    [itemId] = (PurchasedToday(state, shop.Id, itemId) ?? 0) + quantity,
                },
            }
            : state.ShopPurchasesToday;

        var bought = state with
        {
            Player = state.Player with
            {
                Inventory = result.Inventory,
                Money = state.Player.Money - total,
            },
            ShopPurchasesToday = shopPurchases,
        };

        // Buying counts toward collect objectives (M3).
        var quests = Quests.ProgressQuests(ctx, bought, "collect", itemId, quantity);

        return new EngineStep(
            quests.State,
            [Effect.Message(MessageLevels.Success, $"Bought {Js.Num(quantity)}x {item.Name} for ${Js.Num(total)}"), .. quests.Effects]);
    }

    public static EngineStep HandleSellItem(EngineContext ctx, GameState state, string itemId, double quantity)
    {
        if (state.Shop is null) return EngineStep.Of(state, Effect.Message(MessageLevels.Error, "No shop is open."));
        if (quantity <= 0) return EngineStep.Of(state);

        var shop = FindShop(ctx, state.Shop.ShopId);
        if (shop is null) return EngineStep.Of(state, Effect.Message(MessageLevels.Error, "No shop is open."));
        if (!shop.BuysItems) return EngineStep.Of(state, Effect.Message(MessageLevels.Error, $"{shop.Name} doesn't buy items."));

        var slot = state.Player.Inventory.FirstOrDefault(s => s.Item.Id == itemId);
        if (slot is null || slot.Quantity < quantity)
        {
            return EngineStep.Of(state, Effect.Message(MessageLevels.Error, "You don't have that many."));
        }

        var unitPrice = Math.Floor(slot.Item.Value * shop.SellPriceMultiplier);
        var total = unitPrice * quantity;

        return EngineStep.Of(
            state with
            {
                Player = state.Player with
                {
                    Inventory = Inventory.RemoveItem(state.Player.Inventory, itemId, quantity),
                    Money = state.Player.Money + total,
                },
            },
            Effect.Message(MessageLevels.Success, $"Sold {Js.Num(quantity)}x {slot.Item.Name} for ${Js.Num(total)}"));
    }

    public static EngineStep HandleRepairTool(EngineContext ctx, GameState state, string itemId)
    {
        if (state.Shop is null) return EngineStep.Of(state, Effect.Message(MessageLevels.Error, "No shop is open."));
        var shop = FindShop(ctx, state.Shop.ShopId);
        if (shop is null) return EngineStep.Of(state, Effect.Message(MessageLevels.Error, "No shop is open."));
        if (!shop.RepairsTools) return EngineStep.Of(state, Effect.Message(MessageLevels.Error, $"{shop.Name} doesn't repair tools."));

        var slot = state.Player.Inventory.FirstOrDefault(s => s.Item.Id == itemId);
        if (slot is null || slot.Item.Durability is not { } durability || slot.Item.MaxDurability is not { } maxDurability)
        {
            return EngineStep.Of(state, Effect.Message(MessageLevels.Error, "That can't be repaired."));
        }
        var missing = maxDurability - durability;
        if (missing <= 0)
        {
            return EngineStep.Of(state, Effect.Message(MessageLevels.Info, $"{slot.Item.Name} is in perfect shape."));
        }

        var cost = Math.Ceiling(missing * shop.RepairCostPerPoint);
        if (state.Player.Money < cost)
        {
            return EngineStep.Of(state, Effect.Message(MessageLevels.Error, $"Repair costs ${Js.Num(cost)} — not enough money!"));
        }

        var repaired = slot.Item with { Durability = maxDurability };
        return EngineStep.Of(
            state with
            {
                Player = state.Player with
                {
                    Inventory = Inventory.ReplaceItem(state.Player.Inventory, itemId, repaired),
                    Money = state.Player.Money - cost,
                },
            },
            Effect.Message(MessageLevels.Success, $"Repaired {slot.Item.Name} for ${Js.Num(cost)}"));
    }
}
