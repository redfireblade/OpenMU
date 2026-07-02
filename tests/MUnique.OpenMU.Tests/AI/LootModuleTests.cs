// <copyright file="LootModuleTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests.AI;

using MUnique.OpenMU.AIPlayer.Testing;

/// <summary>
/// Tests for the <see cref="AIPlayer.LootModule"/>.
/// Verifies item pickup behavior.
/// </summary>
[TestFixture]
public class LootModuleTests
{
    /// <summary>
    /// The loot module should execute without error when no items are on the ground.
    /// </summary>
    [Test]
    public async ValueTask NoDropsNearby_NoError()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot").ConfigureAwait(false);

        var snap = await harness.StepOnceAsync().ConfigureAwait(false);

        Assert.That(snap, Is.Not.Null);
        Assert.That(snap.Decisions, Is.Not.Null);
    }

    /// <summary>
    /// The loot module should execute without error across multiple ticks.
    /// </summary>
    [Test]
    public async ValueTask MultipleTicks_NoError()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot").ConfigureAwait(false);

        var snapshots = await harness.StepAsync(3).ConfigureAwait(false);

        Assert.That(snapshots, Has.Count.EqualTo(3));
        foreach (var snap in snapshots)
        {
            Assert.That(snap, Is.Not.Null);
        }
    }
}
