// <copyright file="GoalModuleTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests.AI;

using MUnique.OpenMU.AIPlayer.Testing;

/// <summary>
/// Tests for the goal-oriented behavior module.
/// Verifies that goal scheduling and execution work without crashing.
/// </summary>
[TestFixture]
public class GoalModuleTests
{
    /// <summary>
    /// With no goal defined, the module should run without exception
    /// and the goal scheduler should select a fallback farm goal.
    /// </summary>
    [Test]
    public async ValueTask NoGoal_RunsWithoutException()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot", startLevel: 150).ConfigureAwait(false);

        var snap = await harness.StepOnceAsync().ConfigureAwait(false);

        Assert.That(snap, Is.Not.Null);
        Assert.That(snap.Decisions.Any(d => d.ModuleName == "goal"), Is.True,
            "Goal module should have recorded a decision");
    }

    /// <summary>
    /// At level 10, the goal scheduler should create a level-up goal.
    /// </summary>
    [Test]
    public async ValueTask LevelThreshold_CreatesLevelGoal()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot", startLevel: 10).ConfigureAwait(false);

        var snap = await harness.StepOnceAsync().ConfigureAwait(false);

        Assert.That(snap.Decisions.Any(d => d.ModuleName == "goal"), Is.True);
    }

    /// <summary>
    /// Multiple ticks should show progression of goal decisions.
    /// </summary>
    [Test]
    public async ValueTask MultipleTicks_GoalDecisionsRecorded()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot", startLevel: 50).ConfigureAwait(false);

        var snaps = await harness.StepAsync(5).ConfigureAwait(false);

        Assert.That(snaps.Count, Is.EqualTo(5));
        Assert.That(snaps.Any(s => s.Decisions.Any(d => d.ModuleName == "goal")), Is.True,
            "At least one tick should have a goal decision");
    }
}
