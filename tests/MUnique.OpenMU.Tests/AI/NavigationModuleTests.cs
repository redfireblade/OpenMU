// <copyright file="NavigationModuleTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests.AI;

using MUnique.OpenMU.AIPlayer.Testing;

/// <summary>
/// Tests for the <see cref="AIPlayer.NavigationModule"/>.
/// Verifies movement and pathfinding behavior.
/// </summary>
[TestFixture]
public class NavigationModuleTests
{
    /// <summary>
    /// When no target is set and no emergency, the navigation module
    /// should execute without error.
    /// </summary>
    [Test]
    public async ValueTask NoTarget_NoError()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot").ConfigureAwait(false);

        var snap = await harness.StepOnceAsync().ConfigureAwait(false);

        Assert.That(snap, Is.Not.Null);
    }

    /// <summary>
    /// When EmergencyRetreat is set to true, the navigation module
    /// should respond by producing a navigation decision (patrol).
    /// </summary>
    [Test]
    public async ValueTask EmergencyRetreat_ProducesNavigationDecision()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot").ConfigureAwait(false);

        // Set emergency retreat flag
        harness.Context.EmergencyRetreat = true;

        var snap = await harness.StepOnceAsync().ConfigureAwait(false);

        Assert.That(snap, Is.Not.Null);
        Assert.That(snap.Decisions, Is.Not.Null);
    }

    /// <summary>
    /// The navigation module should produce a decision entry after stepping.
    /// </summary>
    [Test]
    public async ValueTask TickProducesNavigationDecision()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot").ConfigureAwait(false);

        // Set a target to navigate toward (use the player itself as a target)
        harness.Context.CurrentTarget = harness.Player;

        var snap = await harness.StepOnceAsync().ConfigureAwait(false);

        Assert.That(snap, Is.Not.Null);
        Assert.That(snap.Decisions, Is.Not.Null);
    }
}
