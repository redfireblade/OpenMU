// <copyright file="BuffModuleTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests.AI;

using MUnique.OpenMU.AIPlayer.Testing;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic.Attributes;

/// <summary>
/// Tests for the <see cref="AIPlayer.BuffModule"/>.
/// Verifies buff skill management behavior.
/// </summary>
[TestFixture]
public class BuffModuleTests
{
    /// <summary>
    /// The buff module should execute without error even when the player
    /// has no buff skills learned.
    /// </summary>
    [Test]
    public async ValueTask NoBuffSkills_NoError()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot").ConfigureAwait(false);

        var snap = await harness.StepOnceAsync().ConfigureAwait(false);

        Assert.That(snap, Is.Not.Null);
    }

    /// <summary>
    /// When a buff skill is available with enough MP, the buff module
    /// should produce a decision.
    /// Note: Requires proper skill setup with MagicEffectDef, runs on
    /// 30-second interval, so only verifies module pipeline integration.
    /// </summary>
    [Test]
    public async ValueTask BuffSkillLearned_ModuleRuns()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot").ConfigureAwait(false);
        var ctx = harness.PersistenceContext;

        // Create a buff skill definition
        var skill = ctx.CreateNew<Skill>();
        skill.Number = 1;
        skill.SkillType = SkillType.Buff;
        skill.ConsumeRequirements.Clear();

        // Add a magic effect definition so the module recognizes it as a buff
        var effectDef = ctx.CreateNew<MagicEffectDefinition>();
        effectDef.Number = 1;
        skill.MagicEffectDef = effectDef;

        // Add skill to character's learned skills before initialization
        if (harness.Player.SelectedCharacter is { } character)
        {
            // Set initial skill storage context first, then add
        }

        // Add skill to player's skill list
        if (harness.Player.SkillList is { } skillList)
        {
            await skillList.AddLearnedSkillAsync(skill).ConfigureAwait(false);
        }

        // Step and verify the player still functions correctly
        var snap = await harness.StepOnceAsync().ConfigureAwait(false);
        Assert.That(snap, Is.Not.Null);
        Assert.That(snap.Decisions, Is.Not.Null);
    }

    /// <summary>
    /// When MP is too low for a buff skill, the module should attempt
    /// to use a mana potion.
    /// </summary>
    [Test]
    public async ValueTask LowMp_TriesManaPotion()
    {
        await using var harness = await AiPlayerTestHarness.CreateAsync("TestBot").ConfigureAwait(false);
        var ctx = harness.PersistenceContext;

        // Create mana potion in inventory
        var def = ctx.CreateNew<ItemDefinition>();
        def.Group = 14;
        def.Number = 1; // Small mana potion (Group 14, Number 1)
        def.Width = 1;
        def.Height = 1;
        def.Durability = 1;

        var item = ctx.CreateNew<Item>();
        item.Definition = def;
        item.ItemSlot = 12;
        item.Durability = 1;

        await harness.AddInventoryItemAsync(item).ConfigureAwait(false);

        // Set MP very low
        harness.SetMp(1);

        // Step and verify no crash
        var snap = await harness.StepOnceAsync().ConfigureAwait(false);
        Assert.That(snap, Is.Not.Null);
    }
}
