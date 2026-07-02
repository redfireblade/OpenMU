// <copyright file="InventoryModuleTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests.AI;

using MUnique.OpenMU.AIPlayer.Testing;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;

/// <summary>
/// Tests for item management in the AI player context.
/// Verifies that items can be added to and found in the player's inventory.
/// </summary>
[TestFixture]
public class InventoryModuleTests
{
    private const byte InventoryStartSlot = 12;

    /// <summary>
    /// Items added via the async inventory API should be findable through the player's Inventory.
    /// </summary>
    [Test]
    public async ValueTask AddItem_ItemFoundInInventory()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot").ConfigureAwait(false);

        var ctx = harness.PersistenceContext;
        var def = ctx.CreateNew<ItemDefinition>();
        def.Group = 14;
        def.Number = 1;
        def.Width = 1;
        def.Height = 1;
        def.Durability = 1;

        var item = ctx.CreateNew<Item>();
        item.Definition = def;
        item.ItemSlot = InventoryStartSlot;
        item.Durability = 1;

        await harness.AddInventoryItemAsync(item).ConfigureAwait(false);

        var foundItem = harness.Player.Inventory?.Items
            .FirstOrDefault(i => i.Definition?.Group == 14 && i.Definition.Number == 1);

        Assert.That(foundItem, Is.Not.Null, "Added item should be findable in inventory");
    }

    /// <summary>
    /// Ticking with items in inventory should not throw.
    /// </summary>
    [Test]
    public async ValueTask TickWithItems_NoErrors()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot").ConfigureAwait(false);

        var ctx = harness.PersistenceContext;
        var def = ctx.CreateNew<ItemDefinition>();
        def.Group = 14;
        def.Number = 1;
        def.Width = 1;
        def.Height = 1;
        def.Durability = 1;

        var item = ctx.CreateNew<Item>();
        item.Definition = def;
        item.ItemSlot = InventoryStartSlot;
        item.Durability = 1;

        await harness.AddInventoryItemAsync(item).ConfigureAwait(false);

        var snap = await harness.StepOnceAsync().ConfigureAwait(false);
        Assert.That(snap, Is.Not.Null);
    }
}
