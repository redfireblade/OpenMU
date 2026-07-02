// <copyright file="CombatModuleTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests.AI;

using MUnique.OpenMU.AIPlayer.Testing;
using MUnique.OpenMU.GameLogic.Attributes;

/// <summary>
/// Tests for the <see cref="AIPlayer.CombatModule"/>.
/// Verifies target selection and combat behavior.
/// </summary>
[TestFixture]
public class CombatModuleTests
{
    /// <summary>
    /// The AI player should tick without error even with no monsters on the map.
    /// No target should be selected.
    /// </summary>
    [Test]
    public async ValueTask NoMonsters_NoTarget()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot").ConfigureAwait(false);

        var snap = await harness.StepOnceAsync().ConfigureAwait(false);

        Assert.That(snap.HasTarget, Is.False, "No target should be selected without monsters");
        Assert.That(snap.Decisions, Is.Not.Null);
    }

    /// <summary>
    /// The player's attributes should be properly initialized after creation.
    /// </summary>
    [Test]
    public async ValueTask PlayerHasCorrectAttributes()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot").ConfigureAwait(false);

        var level = harness.Player.Attributes?[Stats.Level] ?? 0;
        var maxHp = harness.Player.Attributes?[Stats.MaximumHealth] ?? 0;

        Assert.That(level, Is.EqualTo(1), "Starting level should be 1");
        Assert.That(maxHp, Is.GreaterThan(0), "MaxHP should be calculated");
    }

    /// <summary>
    /// Stepping once should produce tick number 1.
    /// </summary>
    [Test]
    public async ValueTask FirstTickIsNumberedOne()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot").ConfigureAwait(false);

        var snap = await harness.StepOnceAsync().ConfigureAwait(false);

        Assert.That(snap.TickNumber, Is.EqualTo(1), "First tick should be number 1");
    }
}
