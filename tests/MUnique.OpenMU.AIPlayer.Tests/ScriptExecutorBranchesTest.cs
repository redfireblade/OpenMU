// <copyright file="ScriptExecutorBranchesTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Tests;

using System.Collections.Generic;
using System.Threading.Tasks;
using MUnique.OpenMU.AIPlayer.Scripting;
using NUnit.Framework;

/// <summary>
/// Tests for ScriptExecutor IF/ELIF/ELSE branch logic.
/// </summary>
[TestFixture]
public class ScriptExecutorBranchesTest : ScriptTestBase
{
    [Test]
    public async Task IfTrue_ExecutesIfBranch()
    {
        // Arrange
        var script = BranchScript(
            ifCondition: "always", ifAction: "wait_respawn");
        var executor = this.CreateExecutor(script);

        // Act
        var result = await executor.TickAsync();

        // Assert — if matched and executed
        Assert.That(result.ConditionMet, Is.True);
        Assert.That(script.BranchHitCounts.GetValueOrDefault("branch_node.if"), Is.EqualTo(1));
    }

    [Test]
    public async Task IfFalse_ElifTrue_ExecutesElifBranch()
    {
        // Arrange
        this.SetupHp(1000, 1000); // 100% — hp_below_threshold false
        var script = BranchScript(
            ifCondition: "hp_below_threshold", ifAction: "wait_respawn",
            elifCondition: "always", elifAction: "wait_respawn");
        var executor = this.CreateExecutor(script);

        // Act
        var result = await executor.TickAsync();

        // Assert — IF false, ELIF true -> ELIF executes
        Assert.That(result.ConditionMet, Is.True);
        Assert.That(script.BranchHitCounts.GetValueOrDefault("branch_node.elif.0"), Is.EqualTo(1));
        Assert.That(script.BranchHitCounts.ContainsKey("branch_node.if"), Is.False);
    }

    [Test]
    public async Task IfFalse_ElifFalse_Else_ExecutesElseBranch()
    {
        // Arrange
        this.SetupHp(1000, 1000); // 100% HP
        this.SetupMp(1000, 1000); // 100% MP
        var node = new PriorityNode
        {
            Name = "branch_node",
            Condition = "hp_below_threshold",
            Action = "wait_respawn",
            ElifNodes = new List<PriorityNode>
            {
                new()
                {
                    Name = "elif_node",
                    Condition = "mp_below_threshold",
                    Action = "wait_respawn",
                },
            },
            ElseNode = new PriorityNode
            {
                Name = "else_node",
                Condition = "always",
                Action = "wait_respawn",
            },
        };
        var script = new BehaviorScript
        {
            Id = "if_elif_else",
            Name = "IF/ELIF/ELSE",
            Version = "1.0.0",
            PriorityChain = new List<PriorityNode> { node },
        };
        var executor = this.CreateExecutor(script);

        // Act
        var result = await executor.TickAsync();

        // Assert — all conditions false, else executes
        Assert.That(result.ConditionMet, Is.True);
        Assert.That(script.BranchHitCounts.GetValueOrDefault("branch_node.else"), Is.EqualTo(1));
        Assert.That(script.BranchHitCounts.ContainsKey("branch_node.if"), Is.False);
        Assert.That(script.BranchHitCounts.ContainsKey("branch_node.elif.0"), Is.False);
    }

    [Test]
    public async Task IfFalse_NoElifNoElse_FallsThrough()
    {
        // Arrange
        this.SetupHp(1000, 1000); // 100% HP — hp_below_threshold false
        var script = BranchScript(
            ifCondition: "hp_below_threshold", ifAction: "wait_respawn");
        var executor = this.CreateExecutor(script);

        // Act
        var result = await executor.TickAsync();

        // Assert — no branch matched, falls through, no nodes executed
        Assert.That(result.ConditionMet, Is.False);
    }

    [Test]
    public async Task BranchHitCounts_TracksAcrossMultipleTicks()
    {
        // Arrange
        var script = BranchScript(
            ifCondition: "always", ifAction: "wait_respawn",
            elifCondition: "always", elifAction: "wait_respawn",
            elseAction: "wait_respawn");
        var executor = this.CreateExecutor(script);

        // Act — run multiple ticks
        for (int i = 0; i < 3; i++)
        {
            await executor.TickAsync();
        }

        // Assert — IF always true, so only IF branch gets hits
        Assert.That(script.BranchHitCounts.GetValueOrDefault("branch_node.if"), Is.EqualTo(3));
        Assert.That(script.BranchHitCounts.ContainsKey("branch_node.elif.0"), Is.False);
        Assert.That(script.BranchHitCounts.ContainsKey("branch_node.else"), Is.False);
    }

    [Test]
    public async Task MultiplePriorityNodes_FirstMatchedExecutes()
    {
        // Arrange — first node's condition false, second true
        this.SetupHp(1000, 1000); // hp_below_threshold false
        var script = new BehaviorScript
        {
            Id = "multi_priority",
            Name = "Multi Priority",
            Version = "1.0.0",
            PriorityChain = new List<PriorityNode>
            {
                new() { Name = "hp_check", Condition = "hp_below_threshold", Action = "wait_respawn" },
                new() { Name = "always_node", Condition = "always", Action = "wait_respawn" },
            },
        };
        var executor = this.CreateExecutor(script);

        // Act
        var result = await executor.TickAsync();

        // Assert — first node skipped, second node executed
        Assert.That(result.ConditionMet, Is.True);
        Assert.That(script.BranchHitCounts.GetValueOrDefault("always_node.if"), Is.EqualTo(1));
    }

    [Test]
    public async Task IfTrueWithElse_OnlyIfExecutes()
    {
        // Arrange
        var script = BranchScript(
            ifCondition: "always", ifAction: "wait_respawn",
            elseAction: "wait_respawn");
        var executor = this.CreateExecutor(script);

        // Act
        var result = await executor.TickAsync();

        // Assert
        Assert.That(result.ConditionMet, Is.True);
        Assert.That(script.BranchHitCounts.GetValueOrDefault("branch_node.if"), Is.EqualTo(1));
        Assert.That(script.BranchHitCounts.ContainsKey("branch_node.else"), Is.False);
    }
}
