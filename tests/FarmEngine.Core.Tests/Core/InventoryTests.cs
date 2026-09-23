using FarmEngine.Schemas;

namespace FarmEngine.Core.Tests.Core;

/// <summary>Port of packages/engine-core/src/inventory.test.ts.</summary>
public class InventoryTests
{
    private static Item MakeItem(string id, double maxStack = 99) => new()
    {
        Id = id,
        Name = id,
        Description = id,
        Type = "material",
        Stackable = true,
        MaxStack = maxStack,
        Value = 1,
    };

    private static InventorySlot Slot(Item item, double quantity) => new() { Item = item, Quantity = quantity };

    // describe('removeItem')

    [Fact]
    public void DrainsAcrossMultipleSlotsHoldingTheSameItemId()
    {
        var wood = MakeItem("wood");
        var inventory = new List<InventorySlot> { Slot(wood, 3), Slot(MakeItem("stone"), 5), Slot(wood, 4) };
        var next = Inventory.RemoveItem(inventory, "wood", 5);
        // 3 from the first slot (deleted), 2 from the second wood slot.
        Assert.Equal([Slot(MakeItem("stone"), 5), Slot(wood, 2)], next);
    }

    [Fact]
    public void RemovesOnlyWhatExistsAndPreservesUnrelatedSlots()
    {
        var wood = MakeItem("wood");
        var next = Inventory.RemoveItem([Slot(wood, 2)], "wood", 10);
        Assert.Empty(next);
        var untouched = Inventory.RemoveItem([Slot(wood, 2)], "iron", 1);
        Assert.Equal([Slot(wood, 2)], untouched);
    }

    [Fact]
    public void DoesNotMutateTheInputInventory()
    {
        var wood = MakeItem("wood");
        var inventory = new List<InventorySlot> { Slot(wood, 3) };
        Inventory.RemoveItem(inventory, "wood", 1);
        Assert.Equal(3, inventory[0].Quantity);
    }

    // describe('addItem stack caps')

    [Fact]
    public void OverflowsPastMaxStackIntoANewSlot()
    {
        var wood = MakeItem("wood", maxStack: 10);
        var result = Inventory.AddItem([Slot(wood, 8)], wood, 5, 5);
        Assert.True(result.Added);
        Assert.Equal([Slot(wood, 10), Slot(wood, 3)], result.Inventory);
    }

    [Fact]
    public void AbsorbsWhatFitsAndRejectsOverflowWhenTheInventoryIsFull()
    {
        var wood = MakeItem("wood", maxStack: 10);
        var filler = Slot(MakeItem("stone"), 1);
        var result = Inventory.AddItem([Slot(wood, 8), filler], wood, 5, 2);
        Assert.False(result.Added);
        Assert.Equal([Slot(wood, 10), filler], result.Inventory);
    }

    [Fact(Skip = "schema: Item.MaxStack is a non-nullable double, so an item without a cap cannot be represented — enable if MaxStack becomes double?")]
    public void KeepsTheHistoricalUnboundedMergeForItemsWithoutACap()
    {
        // TS deletes maxStack from the item; C# cannot express "absent" for a
        // required double. With a nullable MaxStack this would be `MaxStack = null`.
        var uncapped = MakeItem("wood");
        var result = Inventory.AddItem([Slot(uncapped, 500)], uncapped, 600, 1);
        Assert.True(result.Added);
        Assert.Equal(1100, result.Inventory[0].Quantity);
    }
}
