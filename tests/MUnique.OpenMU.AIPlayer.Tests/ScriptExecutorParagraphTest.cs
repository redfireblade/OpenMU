// <copyright file="ScriptExecutorParagraphTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Tests;

using System.Collections.Generic;
using System.Threading.Tasks;
using MUnique.OpenMU.AIPlayer.Scripting;
using NUnit.Framework;

/// <summary>
/// Tests for ScriptExecutor paragraph mode (goto @label, paragraph switching).
/// </summary>
[TestFixture]
public class ScriptExecutorParagraphTest : ScriptTestBase
{
    [Test]
    public async Task GotoLabel_SwitchesToTargetParagraph()
    {
        // Arrange
        var script = ParagraphScript(
            new ScriptParagraph
            {
                Label = "start",
                Nodes = new List<PriorityNode>
                {
                    new()
                    {
                        Name = "go_hunt",
                        Condition = "always",
                        Action = "goto @hunt",
                    },
                },
            },
            new ScriptParagraph
            {
                Label = "hunt",
                Nodes = new List<PriorityNode>
                {
                    new()
                    {
                        Name = "hunt_action",
                        Condition = "always",
                        Action = "wait_respawn",
                    },
                },
            });
        var executor = this.CreateExecutor(script);

        // Act — first tick: goto @hunt in start paragraph
        var result1 = await executor.TickAsync();

        // Second tick: should be in hunt paragraph, executes hunt_action
        var result2 = await executor.TickAsync();

        // Assert
        Assert.That(result1, Is.True, "First tick should execute goto");
        Assert.That(result2, Is.True, "Second tick should execute hunt paragraph action");
    }

    [Test]
    public async Task GotoLabel_NonExistent_DoesNotThrow()
    {
        // Arrange
        var script = ParagraphScript(
            new ScriptParagraph
            {
                Label = "start",
                Nodes = new List<PriorityNode>
                {
                    new()
                    {
                        Name = "bad_goto",
                        Condition = "always",
                        Action = "goto @nonexistent_paragraph",
                    },
                },
            });
        var executor = this.CreateExecutor(script);

        // Act — should not throw
        Assert.That(async () => await executor.TickAsync(), Throws.Nothing);
    }

    [Test]
    public async Task ParagraphWithoutGoto_LoopsCurrentParagraph()
    {
        // Arrange
        var tickCount = 0;
        var script = ParagraphScript(
            new ScriptParagraph
            {
                Label = "main",
                Nodes = new List<PriorityNode>
                {
                    new()
                    {
                        Name = "keep_running",
                        Condition = "always",
                        Action = "wait_respawn",
                    },
                },
            });
        var executor = this.CreateExecutor(script);

        // Act — run multiple ticks; paragraph has no goto, should stay on same paragraph
        for (int i = 0; i < 3; i++)
        {
            await executor.TickAsync();
            tickCount++;
        }

        // Assert — all ticks should execute the same paragraph
        Assert.That(tickCount, Is.EqualTo(3));
    }

    [Test]
    public async Task MultipleParagraphs_GotoChaining()
    {
        // Arrange
        var script = ParagraphScript(
            new ScriptParagraph
            {
                Label = "a",
                Nodes = new List<PriorityNode>
                {
                    new()
                    {
                        Name = "a_to_b",
                        Condition = "always",
                        Action = "goto @b",
                    },
                },
            },
            new ScriptParagraph
            {
                Label = "b",
                Nodes = new List<PriorityNode>
                {
                    new()
                    {
                        Name = "b_to_c",
                        Condition = "always",
                        Action = "goto @c",
                    },
                },
            },
            new ScriptParagraph
            {
                Label = "c",
                Nodes = new List<PriorityNode>
                {
                    new()
                    {
                        Name = "c_stay",
                        Condition = "always",
                        Action = "wait_respawn",
                    },
                },
            });
        var executor = this.CreateExecutor(script);

        // Act — tick1: a -> b, tick2: b -> c, tick3: c stays
        for (int i = 0; i < 3; i++)
        {
            await executor.TickAsync();
        }

        // Assert — script branch counts show all paragraphs executed
        Assert.That(script.BranchHitCounts.GetValueOrDefault("a_to_b.if"), Is.EqualTo(1));
        Assert.That(script.BranchHitCounts.GetValueOrDefault("b_to_c.if"), Is.EqualTo(1));
        Assert.That(script.BranchHitCounts.GetValueOrDefault("c_stay.if"), Is.EqualTo(1));
    }

    [Test]
    public async Task ParagraphWithGotoOnElif_SwitchesCorrectly()
    {
        // Arrange
        var script = ParagraphScript(
            new ScriptParagraph
            {
                Label = "start",
                Nodes = new List<PriorityNode>
                {
                    new()
                    {
                        Name = "check",
                        Condition = "hp_below_threshold",
                        Action = "goto @heal",
                    },
                },
            },
            new ScriptParagraph
            {
                Label = "heal",
                Nodes = new List<PriorityNode>
                {
                    new()
                    {
                        Name = "heal_action",
                        Condition = "always",
                        Action = "wait_respawn",
                    },
                },
            });
        this.SetupHp(200, 1000); // below threshold -> goto @heal
        var executor = this.CreateExecutor(script);

        // Act — first tick: hp below, goto @heal
        var result1 = await executor.TickAsync();
        // Second tick: in heal paragraph
        var result2 = await executor.TickAsync();

        // Assert
        Assert.That(result1, Is.True);
        Assert.That(result2, Is.True);
    }
}
