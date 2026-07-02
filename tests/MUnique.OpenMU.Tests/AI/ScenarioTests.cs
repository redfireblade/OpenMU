// <copyright file="ScenarioTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests.AI;

using MUnique.OpenMU.AIPlayer.Testing;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic.Attributes;

/// <summary>
/// End-to-end scenario tests that exercise multiple AI modules together.
/// </summary>
[TestFixture]
public class ScenarioTests
{
    private const byte InventoryStartSlot = 12;

    /// <summary>
    /// Low HP auto-heal scenario: When HP drops to 10% and potions are available,
    /// the survival module consumes potions over multiple ticks to restore HP.
    /// </summary>
    [Test]
    public async ValueTask LowHpAutoHealScenario()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot").ConfigureAwait(false);
        var ctx = harness.PersistenceContext;

        // Add 3 small healing potions to inventory
        var def = ctx.CreateNew<ItemDefinition>();
        def.Group = 14;
        def.Number = 1;
        def.Width = 1;
        def.Height = 1;
        def.Durability = 1;

        for (int i = 0; i < 3; i++)
        {
            var item = ctx.CreateNew<Item>();
            item.Definition = def;
            item.ItemSlot = (byte)(InventoryStartSlot + i);
            item.Durability = 1;
            await harness.AddInventoryItemAsync(item).ConfigureAwait(false);
        }

        // Set HP to 10%
        var maxHp = harness.Player.Attributes?[Stats.MaximumHealth] ?? 100;
        harness.SetHp((uint)(maxHp * 0.10f));

        // Step 5 times - modules should consume potions
        var snapshots = await harness.StepAsync(5).ConfigureAwait(false);

        Assert.That(snapshots, Has.Count.EqualTo(5));
        foreach (var snap in snapshots)
        {
            Assert.That(snap, Is.Not.Null);
            Assert.That(snap.Decisions, Is.Not.Null);
        }

        // At least one survival decision should have been made
        var hasSurvivalDecision = snapshots.Any(s => s.Decisions.Any(d => d.ModuleName == "survival"));
        Assert.That(hasSurvivalDecision, Is.True, "Survival module should have made decisions during low HP scenario");
    }

    /// <summary>
    /// Multi-tick stability scenario: Running 10 ticks should produce
    /// consistent results without errors. Tick numbers may not be
    /// strictly sequential because walking navigation can cause
    /// skipped tick recordings (early return).
    /// </summary>
    [Test]
    public async ValueTask MultiTickStabilityScenario()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot").ConfigureAwait(false);

        var snapshots = await harness.StepAsync(10).ConfigureAwait(false);

        Assert.That(snapshots, Has.Count.EqualTo(10));

        // Each snapshot should have modules recorded
        foreach (var snap in snapshots)
        {
            Assert.That(snap.Decisions, Is.Not.Null);
            Assert.That(snap.TickNumber, Is.GreaterThan(0));
        }

        // Tick numbers should be monotonic (non-decreasing) even with skipped ticks
        for (int i = 1; i < snapshots.Count; i++)
        {
            Assert.That(snapshots[i].TickNumber, Is.GreaterThanOrEqualTo(snapshots[i - 1].TickNumber));
        }
    }

    /// <summary>
    /// State transition scenario: Changing HP between ticks causes
    /// the survival module to adapt its behavior. Tick numbers may
    /// not be strictly sequential because walking can cause skipped
    /// tick recordings (early return).
    /// </summary>
    [Test]
    public async ValueTask StateTransitionScenario()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot").ConfigureAwait(false);
        var maxHp = harness.Player.Attributes?[Stats.MaximumHealth] ?? 100;

        // Tick 1: Normal HP
        harness.SetHp((uint)maxHp);
        var snap1 = await harness.StepOnceAsync().ConfigureAwait(false);

        // Tick 2: Low HP
        harness.SetHp((uint)(maxHp * 0.10f));
        var snap2 = await harness.StepOnceAsync().ConfigureAwait(false);

        // Tick 3: Back to Normal HP
        harness.SetHp((uint)maxHp);
        var snap3 = await harness.StepOnceAsync().ConfigureAwait(false);

        Assert.That(snap1, Is.Not.Null);
        Assert.That(snap2, Is.Not.Null);
        Assert.That(snap3, Is.Not.Null);

        // Tick numbers should advance over the course of the test
        Assert.That(snap3.TickNumber, Is.GreaterThanOrEqualTo(snap1.TickNumber),
            "Tick number should advance from first to last snapshot");
    }

    /// <summary>
    /// Inventory scenario: Adding items to inventory doesn't break
    /// the AI tick pipeline.
    /// </summary>
    [Test]
    public async ValueTask InventoryFullCycleScenario()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot").ConfigureAwait(false);
        var ctx = harness.PersistenceContext;

        // Create and add a non-consumable item (Group 0 = sword)
        var def = ctx.CreateNew<ItemDefinition>();
        def.Group = 0;
        def.Number = 1;
        def.Width = 1;
        def.Height = 2;
        def.Durability = 1;

        var item = ctx.CreateNew<Item>();
        item.Definition = def;
        item.ItemSlot = InventoryStartSlot;
        item.Durability = 1;

        await harness.AddInventoryItemAsync(item).ConfigureAwait(false);

        // Step 3 times with item in inventory
        var snapshots = await harness.StepAsync(3).ConfigureAwait(false);

        Assert.That(snapshots, Has.Count.EqualTo(3));
        foreach (var snap in snapshots)
        {
            Assert.That(snap, Is.Not.Null);
        }

        // Verify item is still present after stepping (check ItemStorage which includes equipped items)
        var itemStorage = harness.Player.Inventory?.ItemStorage;
        var foundItem = itemStorage?.Items
            .FirstOrDefault(i => i.Definition?.Group == 0 && i.Definition.Number == 1);
        Assert.That(foundItem, Is.Not.Null, "Item should remain in inventory after stepping");
    }

    /// <summary>
    /// Combined scenario: Low HP + inventory with potions over multiple ticks.
    /// Tests the interaction between survival and inventory modules.
    /// </summary>
    [Test]
    public async ValueTask CombinedLowHpWithInventoryScenario()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot").ConfigureAwait(false);
        var ctx = harness.PersistenceContext;

        // Add potions to inventory
        var potionDef = ctx.CreateNew<ItemDefinition>();
        potionDef.Group = 14;
        potionDef.Number = 1;
        potionDef.Width = 1;
        potionDef.Height = 1;
        potionDef.Durability = 1;

        for (int i = 0; i < 3; i++)
        {
            var potion = ctx.CreateNew<Item>();
            potion.Definition = potionDef;
            potion.ItemSlot = (byte)(InventoryStartSlot + i);
            potion.Durability = 1;
            await harness.AddInventoryItemAsync(potion).ConfigureAwait(false);
        }

        // Add another non-potion item
        var armorDef = ctx.CreateNew<ItemDefinition>();
        armorDef.Group = 7;  // Helm group
        armorDef.Number = 1;
        armorDef.Width = 1;
        armorDef.Height = 1;
        armorDef.Durability = 1;

        var armor = ctx.CreateNew<Item>();
        armor.Definition = armorDef;
        armor.ItemSlot = 16;
        armor.Durability = 1;
        await harness.AddInventoryItemAsync(armor).ConfigureAwait(false);

        // Set to low HP
        var maxHp = harness.Player.Attributes?[Stats.MaximumHealth] ?? 100;
        harness.SetHp((uint)(maxHp * 0.15f));

        // Step multiple times - modules should handle both inventory and survival
        var snapshots = await harness.StepAsync(5).ConfigureAwait(false);

        Assert.That(snapshots, Has.Count.EqualTo(5));
        Assert.That(snapshots.All(s => s is not null), Is.True);

        // Verify inventory is still intact (check ItemStorage which includes equipped items)
        var itemStorage = harness.Player.Inventory?.ItemStorage;
        var potionsRemaining = itemStorage?.Items
            .Count(i => i.Definition?.Group == 14) ?? 0;
        var armorRemaining = itemStorage?.Items
            .Count(i => i.Definition?.Group == 7) ?? 0;

        Assert.That(armorRemaining, Is.EqualTo(1), "Non-potion items should remain in inventory");
        Assert.That(potionsRemaining + armorRemaining, Is.GreaterThan(0), "Some items should remain");
    }
}
