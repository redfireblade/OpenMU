// <copyright file="ScriptExecutorVariablesTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Tests;

using System.Collections.Generic;
using System.Threading.Tasks;
using MUnique.OpenMU.AIPlayer.Scripting;
using NUnit.Framework;

/// <summary>
/// Tests for ScriptExecutor variable system (set/inc/dec, variable conditions).
/// </summary>
[TestFixture]
public class ScriptExecutorVariablesTest : ScriptTestBase
{
    [Test]
    public async Task VariableCondition_Equals_WhenMatch_ReturnsTrue()
    {
        // Arrange
        var script = new BehaviorScript
        {
            Id = "var_eq",
            Name = "Var EQ",
            Version = "1.0.0",
            Parameters = new ScriptParameters
            {
                VariableExpression = "attackCount == 0",
            },
            PriorityChain = new List<PriorityNode>
            {
                new()
                {
                    Name = "check_var",
                    Condition = "variable",
                    Action = "wait_respawn",
                },
            },
        };
        var executor = this.CreateExecutor(script);

        // Act — attackCount doesn't exist, defaults to 0, so 0 == 0 is true
        var result = await executor.TickAsync();

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task VariableCondition_Equals_WhenNoMatch_FallsThrough()
    {
        // Arrange
        var script = new BehaviorScript
        {
            Id = "var_eq_false",
            Name = "Var EQ False",
            Version = "1.0.0",
            Parameters = new ScriptParameters
            {
                VariableExpression = "attackCount == 5",
            },
            PriorityChain = new List<PriorityNode>
            {
                new()
                {
                    Name = "check_var",
                    Condition = "variable",
                    Action = "wait_respawn",
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

        // Act — attackCount = 0 (default), 0 != 5, so variable false, fallback executes
        var result = await executor.TickAsync();

        // Assert — fallback executed
        Assert.That(result, Is.True);
        Assert.That(script.BranchHitCounts.GetValueOrDefault("fallback.if"), Is.EqualTo(1));
    }

    [Test]
    public async Task VariableCondition_GreaterThan_Works()
    {
        // Arrange
        var script = new BehaviorScript
        {
            Id = "var_gt",
            Name = "Var GT",
            Version = "1.0.0",
            Parameters = new ScriptParameters
            {
                VariableExpression = "attackCount > 3",
            },
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
                new()
                {
                    Name = "check_var",
                    Condition = "variable",
                    Action = "wait_respawn",
                },
            },
        };
        var executor = this.CreateExecutor(script);

        // Act — first 3 ticks: inc to 4, then check should pass
        await executor.TickAsync(); // inc: 1
        await executor.TickAsync(); // inc: 2
        await executor.TickAsync(); // inc: 3
        var result4 = await executor.TickAsync(); // inc: 4, then check: 4 > 3 true

        // Assert
        Assert.That(result4, Is.True);
    }

    [Test]
    public async Task VariableCondition_LessThan_Works()
    {
        // Arrange
        var script = new BehaviorScript
        {
            Id = "var_lt",
            Name = "Var LT",
            Version = "1.0.0",
            Parameters = new ScriptParameters
            {
                VariableExpression = "healthPct < 50",
            },
            PriorityChain = new List<PriorityNode>
            {
                new()
                {
                    Name = "check_var",
                    Condition = "variable",
                    Action = "wait_respawn",
                },
            },
        };
        var executor = this.CreateExecutor(script);

        // Act — healthPct = 0 (default), 0 < 50 true
        var result = await executor.TickAsync();

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task VariableCondition_GreaterThanOrEqual_WhenFalse_FallsThrough()
    {
        // Arrange
        var script = new BehaviorScript
        {
            Id = "var_gte",
            Name = "Var GTE",
            Version = "1.0.0",
            Parameters = new ScriptParameters
            {
                VariableExpression = "level >= 50",
            },
            PriorityChain = new List<PriorityNode>
            {
                new()
                {
                    Name = "check_var",
                    Condition = "variable",
                    Action = "wait_respawn",
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

        // Act — level = 0 (default), 0 < 50, so >= false, fallback executes
        var result = await executor.TickAsync();

        // Assert
        Assert.That(result, Is.True);
        Assert.That(script.BranchHitCounts.GetValueOrDefault("fallback.if"), Is.EqualTo(1));
    }

    [Test]
    public async Task VariableCondition_LessThanOrEqual_WhenTrue_Executes()
    {
        // Arrange
        var script = new BehaviorScript
        {
            Id = "var_lte",
            Name = "Var LTE",
            Version = "1.0.0",
            Parameters = new ScriptParameters
            {
                VariableExpression = "attempts <= 10",
            },
            PriorityChain = new List<PriorityNode>
            {
                new()
                {
                    Name = "check_var",
                    Condition = "variable",
                    Action = "wait_respawn",
                },
            },
        };
        var executor = this.CreateExecutor(script);

        // Act — attempts = 0 (default), 0 <= 10 true
        var result = await executor.TickAsync();

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task Set_Increments_Decrements_WorkInSequence()
    {
        // Arrange
        var script = new BehaviorScript
        {
            Id = "var_seq",
            Name = "Var Sequence",
            Version = "1.0.0",
            Parameters = new ScriptParameters
            {
                VariableExpression = "counter == 5",
            },
            PriorityChain = new List<PriorityNode>
            {
                new()
                {
                    Name = "inc",
                    Condition = "always",
                    Action = "variable_op",
                    Parameters = new ScriptParameters
                    {
                        VariableOp = "inc counter",
                    },
                },
                new()
                {
                    Name = "check",
                    Condition = "variable",
                    Action = "wait_respawn",
                },
            },
        };
        var executor = this.CreateExecutor(script);

        // Act — 5 ticks: inc from 0 to 5
        bool checkPassed = false;
        for (int i = 0; i < 5; i++)
        {
            await executor.TickAsync(); // inc
            checkPassed = (await executor.TickAsync()).ConditionMet; // check
        }

        // Assert — after 5 incs, counter = 5, check passes
        Assert.That(checkPassed, Is.True);
    }

    [Test]
    public async Task SetVariable_ExecutesSuccessfully()
    {
        // Arrange
        var script = new BehaviorScript
        {
            Id = "var_set_op",
            Name = "Var Set Op",
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
                        VariableOp = "dec counter",
                    },
                },
            },
        };
        var executor = this.CreateExecutor(script);

        // Act
        await executor.TickAsync(); // set 10
        await executor.TickAsync(); // dec (now 9)

        // Assert
        Assert.That(executor.TickCounter, Is.EqualTo(2));
    }

    [Test]
    public async Task IncWithDelta_IncrementsBySpecifiedAmount()
    {
        // Arrange
        var script = new BehaviorScript
        {
            Id = "var_inc_delta",
            Name = "Var Inc Delta",
            Version = "1.0.0",
            PriorityChain = new List<PriorityNode>
            {
                new()
                {
                    Name = "inc_by_5",
                    Condition = "always",
                    Action = "variable_op",
                    Parameters = new ScriptParameters
                    {
                        VariableOp = "inc counter 5",
                    },
                },
            },
        };
        var executor = this.CreateExecutor(script);

        // Act
        await executor.TickAsync();
        await executor.TickAsync();

        // Assert — no crash
        Assert.That(executor.TickCounter, Is.EqualTo(2));
    }

    [Test]
    public async Task VariableCondition_WithNullExpression_FallsThrough()
    {
        // Arrange
        var script = new BehaviorScript
        {
            Id = "var_null",
            Name = "Var Null",
            Version = "1.0.0",
            PriorityChain = new List<PriorityNode>
            {
                new()
                {
                    Name = "check_var",
                    Condition = "variable",
                    Action = "wait_respawn",
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

        // Act — VariableExpression is null, condition returns false
        var result = await executor.TickAsync();

        // Assert — fallback executes
        Assert.That(result, Is.True);
        Assert.That(script.BranchHitCounts.GetValueOrDefault("fallback.if"), Is.EqualTo(1));
    }

    [Test]
    public async Task VariableCondition_InvalidExpression_FallsThrough()
    {
        // Arrange
        var script = new BehaviorScript
        {
            Id = "var_bad_expr",
            Name = "Var Bad Expr",
            Version = "1.0.0",
            Parameters = new ScriptParameters
            {
                VariableExpression = "not_valid",
            },
            PriorityChain = new List<PriorityNode>
            {
                new()
                {
                    Name = "check_var",
                    Condition = "variable",
                    Action = "wait_respawn",
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

        // Act — invalid expression "not_valid" (no operator or value)
        var result = await executor.TickAsync();

        // Assert — fallback executes
        Assert.That(result, Is.True);
        Assert.That(script.BranchHitCounts.GetValueOrDefault("fallback.if"), Is.EqualTo(1));
    }
}
