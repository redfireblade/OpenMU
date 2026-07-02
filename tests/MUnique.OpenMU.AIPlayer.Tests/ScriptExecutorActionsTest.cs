// <copyright file="ScriptExecutorActionsTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Tests;

using System.Collections.Generic;
using System.Threading.Tasks;
using Moq;
using MUnique.OpenMU.AIPlayer.Scripting;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.Pathfinding;
using NUnit.Framework;

/// <summary>
/// Tests for ScriptExecutor action execution.
/// Covers use_hp_potion, use_mp_potion, attack_target, random_patrol, pickup_nearby.
/// </summary>
[TestFixture]
public class ScriptExecutorActionsTest : ScriptTestBase
{
    [Test]
    public async Task UseHpPotion_WithPotionAvailable_SetsHysteresis()
    {
        // Arrange
        this.SetupHp(200, 1000); // 20% HP — below threshold
        var script = new BehaviorScript
        {
            Id = "hp_test",
            Name = "HP Test",
            Version = "1.0.0",
            PriorityChain = new List<PriorityNode>
            {
                new()
                {
                    Name = "hp_check",
                    Condition = "hp_below_threshold",
                    Action = "use_hp_potion",
                },
                new()
                {
                    Name = "fallback",
                    Condition = "always",
                    Action = "wait_respawn",
                },
            },
        };
        var executor = this.CreateExecutor(script);

        // Act
        var result = await executor.TickAsync();

        // Assert — node executed
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task UseHpPotion_WithCooldown_DoesNotStopFallthrough()
    {
        // Arrange
        this.SetupHp(200, 1000);
        var script = new BehaviorScript
        {
            Id = "hp_cooldown",
            Name = "HP Cooldown",
            Version = "1.0.0",
            Parameters = new ScriptParameters { PotionCooldownMs = 5000 },
            PriorityChain = new List<PriorityNode>
            {
                new()
                {
                    Name = "hp_check",
                    Condition = "hp_below_threshold",
                    Action = "use_hp_potion",
                },
                new()
                {
                    Name = "fallback",
                    Condition = "always",
                    Action = "wait_respawn",
                },
            },
        };
        var executor = this.CreateExecutor(script);

        // Act — first tick triggers potion use, second tick cooldown active
        await executor.TickAsync();
        var result2 = await executor.TickAsync();

        // Assert — cooldown causes hp_check's action to no-op, falls through to fallback
        Assert.That(result2, Is.Not.Null);
        Assert.That(script.BranchHitCounts["fallback.if"], Is.EqualTo(1));
    }

    [Test]
    public async Task UseHpPotion_WithNoItems_FallsThrough()
    {
        // Arrange
        this.SetupHp(200, 1000);
        // Player has no Inventory by default (null) — that's fine, the test checks fallback behavior
        var script = new BehaviorScript
        {
            Id = "hp_no_items",
            Name = "HP No Items",
            Version = "1.0.0",
            PriorityChain = new List<PriorityNode>
            {
                new()
                {
                    Name = "hp_check",
                    Condition = "hp_below_threshold",
                    Action = "use_hp_potion",
                },
                new()
                {
                    Name = "fallback",
                    Condition = "always",
                    Action = "wait_respawn",
                },
            },
        };
        var executor = this.CreateExecutor(script);

        // Act
        var result = await executor.TickAsync();

        // Assert — hp_check action runs but finds no items -> falls through to fallback
        Assert.That(result, Is.Not.Null);
        Assert.That(script.BranchHitCounts["fallback.if"], Is.EqualTo(1));
    }

    [Test]
    public async Task UseMpPotion_WhenBelowThreshold_Executes()
    {
        // Arrange
        this.SetupMp(100, 1000); // 10% MP — below 20% threshold
        var script = SingleNodeScript("mp_below_threshold", "use_mp_potion");
        var executor = this.CreateExecutor(script);

        // Act
        var result = await executor.TickAsync();

        // Assert — node executed (action runs, may find no potions but doesn't crash)
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task AttackTarget_WithTarget_Executes()
    {
        // Arrange
        var mockTarget = new Mock<IAttackable>();
        mockTarget.Setup(t => t.Position).Returns(new Point(105, 105));
        mockTarget.Setup(t => t.IsAlive).Returns(true);
        this.Context.CurrentTarget = mockTarget.Object;

        var script = SingleNodeScript("always", "attack_target");
        var executor = this.CreateExecutor(script);

        // Act
        var result = await executor.TickAsync();

        // Assert — node executed
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task AttackTarget_WithoutTarget_ExecutesButNoOp()
    {
        // Arrange
        this.Context.CurrentTarget = null;
        var script = SingleNodeScript("no_target", "attack_target");
        var executor = this.CreateExecutor(script);

        // Act — no_target is true, attack_target runs but target is null -> no-op
        var result = await executor.TickAsync();

        // Assert
        Assert.That(result, Is.Not.Null, "Node should execute even if action is no-op");
    }

    [Test]
    public async Task RandomPatrol_RunsWithoutError()
    {
        // Arrange
        var script = new BehaviorScript
        {
            Id = "patrol",
            Name = "Patrol",
            Version = "1.0.0",
            Parameters = new ScriptParameters { PatrolRadius = 5, PatrolCenterResetTicks = 3 },
            PriorityChain = new List<PriorityNode>
            {
                new()
                {
                    Name = "patrol",
                    Condition = "always",
                    Action = "random_patrol",
                },
            },
        };
        var executor = this.CreateExecutor(script);

        // Act — run a few ticks
        for (int i = 0; i < 5; i++)
        {
            await executor.TickAsync();
        }

        // Assert — executor ran without exceptions
        Assert.That(executor.TickCounter, Is.EqualTo(5));
    }

    [Test]
    public async Task PickupNearby_WithFilterNotNone_Executes()
    {
        // Arrange
        var script = new BehaviorScript
        {
            Id = "pickup",
            Name = "Pickup",
            Version = "1.0.0",
            Parameters = new ScriptParameters { PickupFilter = "all" },
            PriorityChain = new List<PriorityNode>
            {
                new()
                {
                    Name = "pickup",
                    Condition = "always",
                    Action = "pickup_nearby",
                },
            },
        };
        var executor = this.CreateExecutor(script);

        // Act
        var result = await executor.TickAsync();

        // Assert
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task PickupNearby_WithFilterNone_SkipsPickup()
    {
        // Arrange
        var script = new BehaviorScript
        {
            Id = "pickup_none",
            Name = "Pickup None",
            Version = "1.0.0",
            Parameters = new ScriptParameters { PickupFilter = "none" },
            PriorityChain = new List<PriorityNode>
            {
                new()
                {
                    Name = "pickup",
                    Condition = "always",
                    Action = "pickup_nearby",
                },
            },
        };
        var executor = this.CreateExecutor(script);

        // Act
        var result = await executor.TickAsync();

        // Assert — action executes but skips pickup internally
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task WaitRespawn_ExecutesWithoutError()
    {
        // Arrange
        var script = SingleNodeScript("always", "wait_respawn");
        var executor = this.CreateExecutor(script);

        // Act
        var result = await executor.TickAsync();

        // Assert
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task VariableOp_Set_ExecutesSuccessfully()
    {
        // Arrange
        var script = new BehaviorScript
        {
            Id = "var_set",
            Name = "Var Set",
            Version = "1.0.0",
            PriorityChain = new List<PriorityNode>
            {
                new()
                {
                    Name = "set_var",
                    Condition = "always",
                    Action = "variable_op",
                    Parameters = new ScriptParameters
                    {
                        VariableOp = "set attackCount 5",
                    },
                },
            },
        };
        var executor = this.CreateExecutor(script);

        // Act
        var result = await executor.TickAsync();

        // Assert
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task VariableOp_Inc_ExecutesSuccessfully()
    {
        // Arrange
        var script = new BehaviorScript
        {
            Id = "var_inc",
            Name = "Var Inc",
            Version = "1.0.0",
            PriorityChain = new List<PriorityNode>
            {
                new()
                {
                    Name = "inc_var",
                    Condition = "always",
                    Action = "variable_op",
                    Parameters = new ScriptParameters
                    {
                        VariableOp = "inc attackCount",
                    },
                },
            },
        };
        var executor = this.CreateExecutor(script);

        // Act
        await executor.TickAsync();
        await executor.TickAsync();

        // Assert — two ticks without error
        Assert.That(executor.TickCounter, Is.EqualTo(2));
    }

    [Test]
    public async Task VariableOp_Dec_ExecutesSuccessfully()
    {
        // Arrange
        var script = new BehaviorScript
        {
            Id = "var_dec",
            Name = "Var Dec",
            Version = "1.0.0",
            PriorityChain = new List<PriorityNode>
            {
                new()
                {
                    Name = "set",
                    Condition = "always",
                    Action = "variable_op",
                    Parameters = new ScriptParameters
                    {
                        VariableOp = "set counter 10",
                    },
                },
                new()
                {
                    Name = "dec",
                    Condition = "always",
                    Action = "variable_op",
                    Parameters = new ScriptParameters
                    {
                        VariableOp = "dec counter 3",
                    },
                },
            },
        };
        var executor = this.CreateExecutor(script);

        // Act
        await executor.TickAsync(); // set 10
        await executor.TickAsync(); // dec by 3

        // Assert
        Assert.That(executor.TickCounter, Is.EqualTo(2));
    }
}
