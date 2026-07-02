// <copyright file="SurvivalModuleTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests.AI;

using MUnique.OpenMU.AIPlayer.Testing;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;

/// <summary>
/// Tests for the <see cref="AIPlayer.SurvivalModule"/>.
/// Verifies HP monitoring and potion consumption behavior.
/// </summary>
[TestFixture]
public class SurvivalModuleTests
{
    private const byte InventoryStartSlot = 12;

    /// <summary>
    /// When HP is low and a potion is available, the survival module
    /// should consume the potion (removed from inventory after use).
    /// </summary>
    [Test]
    public async ValueTask LowHp_ConsumesPotion()
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

        // Verify item is in inventory before stepping
        var beforeItem = harness.Player.Inventory?.Items
            .FirstOrDefault(i => i.ItemSlot == InventoryStartSlot);
        Assert.That(beforeItem, Is.Not.Null, "Item should be in inventory before step");

        var maxHp = harness.Player.Attributes?[Stats.MaximumHealth] ?? 100;
        var lowHp = (uint)(maxHp * 0.30f);
        harness.SetHp(lowHp);

        var snap = await harness.StepOnceAsync().ConfigureAwait(false);

        Assert.That(snap.SurvivalLevel, Is.Not.EqualTo(AIPlayer.SurvivalManager.SurvivalLevel.Normal),
            "Survival level should indicate low HP");

        // Item should have been consumed and removed from inventory
        var afterItem = harness.Player.Inventory?.Items
            .FirstOrDefault(i => i.ItemSlot == InventoryStartSlot);
        Assert.That(afterItem, Is.Null, "Potion should be consumed and removed from inventory");

        // Verify survival module recorded a decision
        Assert.That(snap.Decisions.Any(d => d.ModuleName == "survival"), Is.True);
    }

    /// <summary>
    /// When HP is full, no potion should be consumed and HP should remain at max.
    /// </summary>
    [Test]
    public async ValueTask NormalHp_NoPotionUsed()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot").ConfigureAwait(false);

        var maxHp = harness.Player.Attributes?[Stats.MaximumHealth] ?? 100;
        harness.SetHp((uint)maxHp);

        var snap = await harness.StepOnceAsync().ConfigureAwait(false);

        Assert.That(snap.SurvivalLevel, Is.EqualTo(AIPlayer.SurvivalManager.SurvivalLevel.Normal));
    }

    /// <summary>
    /// When HP drops below 15%, the survival level should indicate emergency.
    /// </summary>
    [Test]
    public async ValueTask VeryLowHp_EmergencyLevel()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot").ConfigureAwait(false);

        var maxHp = harness.Player.Attributes?[Stats.MaximumHealth] ?? 100;
        var emergencyHp = (uint)(maxHp * 0.10f);

        harness.SetHp(emergencyHp);

        var snap = await harness.StepOnceAsync().ConfigureAwait(false);

        Assert.That(snap.SurvivalLevel, Is.EqualTo(AIPlayer.SurvivalManager.SurvivalLevel.Emergency));
    }
}
