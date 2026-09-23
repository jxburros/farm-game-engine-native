using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>
/// Pure inventory helpers. All return new lists; slots are copied on write.
/// Port of inventory.ts.
/// </summary>
public static class Inventory
{
    /// <summary>
    /// Add <c>quantity</c> of <c>item</c>. Mirrors the historical app semantics:
    /// an existing slot with the same item id absorbs the quantity (optionally
    /// only when the item is stackable); otherwise a new slot is appended if
    /// there is room.
    /// </summary>
    public static AddItemResult AddItem(
        List<InventorySlot> inventory,
        Item item,
        double quantity,
        double maxInventorySize,
        AddItemOptions? options = null)
    {
        options ??= new AddItemOptions();
        var index = inventory.FindIndex(slot => slot.Item.Id == item.Id);
        if (index != -1 && (options.RequireStackableForMerge != true || inventory[index].Item.Stackable))
        {
            var slot = inventory[index];
            // Honor an authored stack cap (the item editor writes maxStack); items
            // without one keep the historical unbounded-merge behavior.
            // NOTE: Item.MaxStack is a required number in the schema, so in C# it is
            // never "undefined"; the nullable view keeps this line valid if the
            // schema ever makes it optional.
            double? cap = slot.Item.MaxStack;
            if (cap is { } capValue && slot.Quantity + quantity > capValue)
            {
                var roomInSlot = Math.Max(0, capValue - slot.Quantity);
                var overflow = quantity - roomInSlot;
                if (inventory.Count >= maxInventorySize)
                {
                    // No room for an overflow slot: absorb what fits, reject the rest.
                    if (roomInSlot == 0) return new AddItemResult(inventory, false);
                    var capped = inventory.Select((s, i) => i == index ? s with { Quantity = capValue } : s).ToList();
                    return new AddItemResult(capped, false);
                }
                var next = inventory.Select((s, i) => i == index ? s with { Quantity = capValue } : s).ToList();
                return new AddItemResult([.. next, new InventorySlot { Item = item, Quantity = overflow }], true);
            }
            var merged = inventory.Select((s, i) => i == index ? s with { Quantity = s.Quantity + quantity } : s).ToList();
            return new AddItemResult(merged, true);
        }
        if (inventory.Count < maxInventorySize)
        {
            return new AddItemResult([.. inventory, new InventorySlot { Item = item, Quantity = quantity }], true);
        }
        return new AddItemResult(inventory, false);
    }

    /// <summary>
    /// Remove <c>quantity</c> of <c>itemId</c>, draining across EVERY slot holding it;
    /// deletes emptied slots. Multiple slots per item id can exist (stack caps,
    /// pack reconciliation), and <c>hasIngredients</c> counts across all of them —
    /// consuming from only the first slot allowed item duplication.
    /// </summary>
    public static List<InventorySlot> RemoveItem(List<InventorySlot> inventory, string itemId, double quantity)
    {
        if (!inventory.Any(slot => slot.Item.Id == itemId)) return inventory;
        var remainingToRemove = quantity;
        var next = new List<InventorySlot>();
        foreach (var slot in inventory)
        {
            if (slot.Item.Id != itemId || remainingToRemove <= 0)
            {
                next.Add(slot);
                continue;
            }
            var removed = Math.Min(slot.Quantity, remainingToRemove);
            remainingToRemove -= removed;
            if (slot.Quantity > removed)
            {
                next.Add(slot with { Quantity = slot.Quantity - removed });
            }
        }
        return next;
    }

    public static InventorySlot? FindSlot(List<InventorySlot> inventory, Func<InventorySlot, bool> predicate) =>
        inventory.FirstOrDefault(predicate);

    public static InventorySlot? FindToolSlot(List<InventorySlot> inventory, string toolType) =>
        inventory.FirstOrDefault(slot => slot.Item.ToolType == toolType);

    /// <summary>Replace the item object in the slot matching <c>itemId</c> (e.g. durability change).</summary>
    public static List<InventorySlot> ReplaceItem(List<InventorySlot> inventory, string itemId, Item item) =>
        inventory.Select(slot => slot.Item.Id == itemId ? slot with { Item = item } : slot).ToList();

    /// <summary>TS <c>AddItemResult</c>.</summary>
    public sealed record AddItemResult(List<InventorySlot> Inventory, bool Added);

    /// <summary>TS <c>addItem</c> options bag <c>{ requireStackableForMerge?: boolean }</c>.</summary>
    public sealed record AddItemOptions
    {
        public bool? RequireStackableForMerge { get; init; }
    }
}
