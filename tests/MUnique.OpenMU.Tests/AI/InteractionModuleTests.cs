// <copyright file="InteractionModuleTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests.AI;

using MUnique.OpenMU.AIPlayer.Testing;

/// <summary>
/// Tests for the NPC interaction behavior module.
/// Verifies that interaction decisions are recorded correctly.
/// </summary>
[TestFixture]
public class InteractionModuleTests
{
    /// <summary>
    /// With no NPCs on the map, the module should run without exception.
    /// </summary>
    [Test]
    public async ValueTask NoNpcs_RunsWithoutException()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot").ConfigureAwait(false);

        var snap = await harness.StepOnceAsync().ConfigureAwait(false);

        Assert.That(snap, Is.Not.Null);
    }

    /// <summary>
    /// The interaction module should record a decision in the tick snapshot.
    /// </summary>
    [Test]
    public async ValueTask InteractionDecision_RecordedInSnapshot()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot").ConfigureAwait(false);

        var snap = await harness.StepOnceAsync().ConfigureAwait(false);

        Assert.That(snap.Decisions.Any(d => d.ModuleName == "interaction"), Is.True,
            "Interaction module should have recorded a decision");
    }

    /// <summary>
    /// After multiple ticks, at least one should have recorded an interaction decision.
    /// Some ticks may have empty decisions due to early returns (e.g. walking).
    /// </summary>
    [Test]
    public async ValueTask MultipleTicks_SomeHaveInteractionDecisions()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot").ConfigureAwait(false);

        var snaps = await harness.StepAsync(5).ConfigureAwait(false);

        Assert.That(snaps.Count, Is.EqualTo(5));
        Assert.That(snaps.Any(s => s.Decisions.Any(d => d.ModuleName == "interaction")), Is.True,
            "At least one tick should have an interaction decision");
    }
}
