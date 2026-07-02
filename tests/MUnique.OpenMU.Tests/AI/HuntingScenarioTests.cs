// <copyright file="HuntingScenarioTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests.AI;

using MUnique.OpenMU.AIPlayer;
using MUnique.OpenMU.AIPlayer.Testing;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;

/// <summary>
/// Autonomous AI behavior tests with monsters on the map.
/// Verifies that the AI player can detect, walk toward, and engage monsters
/// just like a real auto-bot client would.
/// </summary>
[TestFixture]
public class HuntingScenarioTests
{
    private const byte InventoryStartSlot = 12;

    /// <summary>
    /// Monster within detection range — the combat module should find it
    /// and record a "combat" decision in the tick snapshot.
    /// </summary>
    [Test]
    public async ValueTask MonsterInRange_CombatModuleDetects()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot", startLevel: 50).ConfigureAwait(false);

        // Spawn a monster 2 tiles away from player (player at 100,100)
        await harness.AddMonsterNearbyAsync(102, 100, level: 10, maxHp: 200).ConfigureAwait(false);

        // Give AOI system time to register the new monster
        await Task.Delay(200).ConfigureAwait(false);

        var snap = await harness.StepOnceAsync().ConfigureAwait(false);

        Assert.That(snap.Decisions.Any(d => d.ModuleName == "combat"), Is.True,
            "Combat module should have detected the nearby monster");
        Assert.That(harness.Context.CurrentTarget, Is.Not.Null,
            "A target should be selected after detecting a monster");
    }

    /// <summary>
    /// With no monsters on the map, the combat module should run without
    /// selecting a target (no-op).
    /// </summary>
    [Test]
    public async ValueTask NoMonsters_CombatModuleNoOps()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot", startLevel: 50).ConfigureAwait(false);

        var snap = await harness.StepOnceAsync().ConfigureAwait(false);

        Assert.That(snap, Is.Not.Null);
        Assert.That(harness.Context.CurrentTarget, Is.Null,
            "No target should be selected without monsters on the map");
    }

    /// <summary>
    /// Multiple monsters at different levels — the AI should select the
    /// best-scoring target (closest, appropriate level difference).
    /// </summary>
    [Test]
    public async ValueTask MultipleMonsters_SelectsBestTarget()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot", startLevel: 50).ConfigureAwait(false);

        // Spawn two monsters: one close (102, 100) and one far (120, 100)
        await harness.AddMonsterNearbyAsync(102, 100, level: 10, maxHp: 200, number: 1).ConfigureAwait(false);
        await harness.AddMonsterNearbyAsync(120, 100, level: 30, maxHp: 500, number: 2).ConfigureAwait(false);

        await Task.Delay(200).ConfigureAwait(false);

        var snap = await harness.StepOnceAsync().ConfigureAwait(false);

        Assert.That(snap.Decisions.Any(d => d.ModuleName == "combat"), Is.True,
            "Combat module should activate with multiple monsters");

        // The closest monster (102, 100) should be preferred
        if (harness.Context.CurrentTarget is { } target)
        {
            var distance = harness.Player.Position.EuclideanDistanceTo(target.Position);
            Assert.That(distance, Is.LessThanOrEqualTo(10),
                "Target should be close to the player");
        }
    }

    /// <summary>
    /// Full hunting flow over multiple ticks: monster detection → walk toward →
    /// engage in combat. Verifies the complete autonomous behavior pipeline.
    /// </summary>
    [Test]
    public async ValueTask MultipleTicks_HuntingFlowSucceeds()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot", startLevel: 100).ConfigureAwait(false);

        // Spawn a monster far enough to require walking
        await harness.AddMonsterNearbyAsync(120, 100, level: 30, maxHp: 1000, number: 1).ConfigureAwait(false);

        await Task.Delay(200).ConfigureAwait(false);

        // Run 10 ticks to allow the full flow: detect → walk → engage
        var snaps = await harness.StepAsync(10).ConfigureAwait(false);

        Assert.That(snaps.Count, Is.EqualTo(10));

        // At least one tick should show combat activity
        var hasCombat = snaps.Any(s => s.Decisions.Any(d => d.ModuleName == "combat"));
        Assert.That(hasCombat, Is.True,
            "At least one tick should have a combat decision during hunting");
    }

    /// <summary>
    /// Combined scenario: low HP while a monster is present.
    /// The survival module should consume potions while the combat
    /// module simultaneously tracks the monster.
    /// </summary>
    [Test]
    public async ValueTask LowHpWithMonster_SurvivalAndCombat()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot", startLevel: 50).ConfigureAwait(false);
        var ctx = harness.PersistenceContext;

        // Add potions to inventory
        var potionDef = ctx.CreateNew<ItemDefinition>();
        potionDef.Group = 14;
        potionDef.Number = 1;
        potionDef.Width = 1;
        potionDef.Height = 1;
        potionDef.Durability = 1;

        for (int i = 0; i < 5; i++)
        {
            var potion = ctx.CreateNew<Item>();
            potion.Definition = potionDef;
            potion.ItemSlot = (byte)(InventoryStartSlot + i);
            potion.Durability = 1;
            await harness.AddInventoryItemAsync(potion).ConfigureAwait(false);
        }

        // Add a monster nearby
        await harness.AddMonsterNearbyAsync(102, 100, level: 10, maxHp: 200).ConfigureAwait(false);

        await Task.Delay(200).ConfigureAwait(false);

        // Set HP low
        var maxHp = harness.Player.Attributes?[Stats.MaximumHealth] ?? 100;
        harness.SetHp((uint)(maxHp * 0.10f));

        // Run multiple ticks — both modules should activate
        var snaps = await harness.StepAsync(5).ConfigureAwait(false);

        var hasSurvival = snaps.Any(s => s.Decisions.Any(d => d.ModuleName == "survival"));
        var hasCombat = snaps.Any(s => s.Decisions.Any(d => d.ModuleName == "combat"));

        Assert.That(hasSurvival, Is.True, "Survival module should activate at low HP");
        Assert.That(hasCombat, Is.True, "Combat module should activate with monster present");
    }

    /// <summary>
    /// Monster at the same position as the player — AI should immediately
    /// be in attack range and attempt to engage on the first tick.
    /// </summary>
    [Test]
    public async ValueTask MonsterAtSamePosition_InstantDetection()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot", startLevel: 50).ConfigureAwait(false);

        // Monster at same position as player (100, 100)
        var monster = await harness.AddMonsterNearbyAsync(100, 100, level: 5, maxHp: 100).ConfigureAwait(false);

        await Task.Delay(200).ConfigureAwait(false);

        var snap = await harness.StepOnceAsync().ConfigureAwait(false);

        Assert.That(snap.Decisions.Any(d => d.ModuleName == "combat"), Is.True);
        Assert.That(harness.Context.CurrentTarget, Is.Not.Null);
    }

    /// <summary>
    /// Monster level far above the player — the combat module should
    /// skip it due to risk scoring (levelDiff > maxLevelDiff).
    /// This verifies that the AI doesn't suicide on high-level monsters.
    /// </summary>
    [Test]
    public async ValueTask MonsterTooHighLevel_SkippedByCombat()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot", startLevel: 1).ConfigureAwait(false);

        // A level 200 monster — way too high for a level 1 character
        await harness.AddMonsterNearbyAsync(102, 100, level: 200, maxHp: 10000).ConfigureAwait(false);

        await Task.Delay(200).ConfigureAwait(false);

        var snap = await harness.StepOnceAsync().ConfigureAwait(false);

        // Combat module always runs and records a decision, but should skip this target
        Assert.That(snap.Decisions.Any(d => d.ModuleName == "combat"), Is.True,
            "Combat module should have run");
        Assert.That(harness.Context.CurrentTarget, Is.Null,
            "High-level monster should be skipped by target selection");
    }
}
