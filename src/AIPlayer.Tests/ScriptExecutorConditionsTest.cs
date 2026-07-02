// <copyright file="ScriptExecutorConditionsTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Tests;

using System.Threading.Tasks;
using Moq;
using MUnique.OpenMU.AIPlayer.Scripting;
using MUnique.OpenMU.GameLogic;
using Xunit;

/// <summary>
/// Tests for ScriptExecutor condition evaluation.
/// Covers hp_below_threshold, mp_below_threshold, is_dead, always, no_target, in_safe_zone.
/// </summary>
public class ScriptExecutorConditionsTest : ScriptTestBase
{
    /// <summary>
    /// Tests that hp_below_threshold returns true when HP is below the threshold.
    /// </summary>
    [Fact]
    public async Task HpBelowThreshold_WhenBelowThreshold_ReturnsTrue()
    {
        // Arrange
        this.SetupHp(200, 1000); // 20% HP, threshold is 35%
        var script = SingleNodeScript("hp_below_threshold", "wait_respawn");
        var executor = this.CreateExecutor(script);

        // Act
        var result = await executor.TickAsync();

        // Assert
        Assert.True(result);
    }

    /// <summary>
    /// Tests that hp_below_threshold returns false when HP is above the threshold.
    /// </summary>
    [Fact]
    public async Task HpBelowThreshold_WhenAboveThreshold_ReturnsFalse()
    {
        // Arrange
        this.SetupHp(600, 1000); // 60% HP, threshold is 35%
        var script = SingleNodeScript("hp_below_threshold", "wait_respawn");
        var executor = this.CreateExecutor(script);

        // Act
        var result = await executor.TickAsync();

        // Assert
        Assert.False(result);
    }

    /// <summary>
    /// Tests hysteresis stays active until stop threshold.
    /// </summary>
    [Fact]
    public async Task HpBelowThreshold_Hysteresis_StaysActiveUntilStopThreshold()
    {
        // Arrange
        this.SetupHp(200, 1000); // 20% — below threshold, triggers
        var script = SingleNodeScript("hp_below_threshold", "wait_respawn");
        var executor = this.CreateExecutor(script);

        // Act — first tick triggers, activating hysteresis
        await executor.TickAsync();

        // HP recovered to 40% — still below stop threshold (35% * 1.2 = 42%)
        this.SetupHp(400, 1000);
        var result2 = await executor.TickAsync();

        // Assert — hysteresis still active, condition returns false
        Assert.False(result2);
    }

    /// <summary>
    /// Tests hysteresis deactivates above stop threshold.
    /// </summary>
    [Fact]
    public async Task HpBelowThreshold_Hysteresis_DeactivatesAboveStopThreshold()
    {
        // Arrange
        this.SetupHp(200, 1000); // 20% — triggers
        var script = SingleNodeScript("hp_below_threshold", "wait_respawn");
        var executor = this.CreateExecutor(script);

        // Act — trigger hysteresis
        await executor.TickAsync();

        // HP recovered to 50% — above stop threshold (42%), hysteresis should clear
        this.SetupHp(500, 1000);
        var result2 = await executor.TickAsync();

        // Assert — hysteresis released, but 50% > 35% threshold, so still false
        Assert.False(result2);
    }

    /// <summary>
    /// Tests that mp_below_threshold returns true when MP is below the threshold.
    /// </summary>
    [Fact]
    public async Task MpBelowThreshold_WhenBelowThreshold_ReturnsTrue()
    {
        // Arrange
        this.SetupMp(100, 1000); // 10% MP, threshold is 20%
        var script = SingleNodeScript("mp_below_threshold", "wait_respawn");
        var executor = this.CreateExecutor(script);

        // Act
        var result = await executor.TickAsync();

        // Assert
        Assert.True(result);
    }

    /// <summary>
    /// Tests that mp_below_threshold returns false when MP is above the threshold.
    /// </summary>
    [Fact]
    public async Task MpBelowThreshold_WhenAboveThreshold_ReturnsFalse()
    {
        // Arrange
        this.SetupMp(500, 1000); // 50% MP, threshold is 20%
        var script = SingleNodeScript("mp_below_threshold", "wait_respawn");
        var executor = this.CreateExecutor(script);

        // Act
        var result = await executor.TickAsync();

        // Assert
        Assert.False(result);
    }

    /// <summary>
    /// Tests hysteresis stays active until stop threshold for MP.
    /// </summary>
    [Fact]
    public async Task MpBelowThreshold_Hysteresis_StaysActiveUntilStopThreshold()
    {
        // Arrange
        this.SetupMp(100, 1000); // 10% — below threshold
        var script = SingleNodeScript("mp_below_threshold", "wait_respawn");
        var executor = this.CreateExecutor(script);

        // Act — trigger
        await executor.TickAsync();

        // MP recovered to 22% — below stop threshold (20% * 1.2 = 24%)
        this.SetupMp(220, 1000);
        var result2 = await executor.TickAsync();

        // Assert — hysteresis still active
        Assert.False(result2);
    }

    /// <summary>
    /// Tests that is_dead returns true when HP is zero.
    /// </summary>
    [Fact]
    public async Task IsDead_WhenHpIsZero_ReturnsTrue()
    {
        // Arrange
        this.SetupHp(0, 1000);
        var script = SingleNodeScript("is_dead", "wait_respawn");
        var executor = this.CreateExecutor(script);

        // Act
        var result = await executor.TickAsync();

        // Assert
        Assert.True(result);
    }

    /// <summary>
    /// Tests that is_dead returns false when HP is positive.
    /// </summary>
    [Fact]
    public async Task IsDead_WhenHpIsPositive_ReturnsFalse()
    {
        // Arrange
        this.SetupHp(500, 1000);
        var script = SingleNodeScript("is_dead", "wait_respawn");
        var executor = this.CreateExecutor(script);

        // Act
        var result = await executor.TickAsync();

        // Assert
        Assert.False(result);
    }

    /// <summary>
    /// Tests that always condition always returns true.
    /// </summary>
    [Fact]
    public async Task AlwaysCondition_AlwaysReturnsTrue()
    {
        // Arrange
        var script = SingleNodeScript("always", "wait_respawn");
        var executor = this.CreateExecutor(script);

        // Act
        var result = await executor.TickAsync();

        // Assert
        Assert.True(result);
    }

    /// <summary>
    /// Tests that no_target returns true when target is null.
    /// </summary>
    [Fact]
    public async Task NoTarget_WhenTargetIsNull_ReturnsTrue()
    {
        // Arrange
        this.Context.CurrentTarget = null;
        var script = SingleNodeScript("no_target", "wait_respawn");
        var executor = this.CreateExecutor(script);

        // Act
        var result = await executor.TickAsync();

        // Assert
        Assert.True(result);
    }

    /// <summary>
    /// Tests that no_target returns false when target is set.
    /// </summary>
    [Fact]
    public async Task NoTarget_WhenTargetIsSet_ReturnsFalse()
    {
        // Arrange
        this.Context.CurrentTarget = Mock.Of<IAttackable>();
        var script = SingleNodeScript("no_target", "wait_respawn");
        var executor = this.CreateExecutor(script);

        // Act
        var result = await executor.TickAsync();

        // Assert
        Assert.False(result);
    }

    /// <summary>
    /// Tests that in_safe_zone returns true when world state indicates safezone.
    /// </summary>
    [Fact]
    public async Task InSafeZone_WhenWorldStateIsSafezone_ReturnsTrue()
    {
        // Arrange
        this.Context.WorldState = new WorldState
        {
            AttackablesInRange = this.Context.WorldState.AttackablesInRange,
            DropsInRange = this.Context.WorldState.DropsInRange,
            NpcsInRange = this.Context.WorldState.NpcsInRange,
            CurrentMap = this.Context.WorldState.CurrentMap,
            PlayerPosition = this.Context.WorldState.PlayerPosition,
            IsAtSafezone = true,
        };
        var script = SingleNodeScript("in_safe_zone", "wait_respawn");
        var executor = this.CreateExecutor(script);

        // Act
        var result = await executor.TickAsync();

        // Assert
        Assert.True(result);
    }

    /// <summary>
    /// Tests that in_safe_zone returns false when world state is not safezone.
    /// </summary>
    [Fact]
    public async Task InSafeZone_WhenWorldStateIsNotSafezone_ReturnsFalse()
    {
        // Arrange
        this.Context.WorldState = new WorldState
        {
            AttackablesInRange = this.Context.WorldState.AttackablesInRange,
            DropsInRange = this.Context.WorldState.DropsInRange,
            NpcsInRange = this.Context.WorldState.NpcsInRange,
            CurrentMap = this.Context.WorldState.CurrentMap,
            PlayerPosition = this.Context.WorldState.PlayerPosition,
            IsAtSafezone = false,
        };
        var script = SingleNodeScript("in_safe_zone", "wait_respawn");
        var executor = this.CreateExecutor(script);

        // Act
        var result = await executor.TickAsync();

        // Assert
        Assert.False(result);
    }

    /// <summary>
    /// Tests unknown condition falls through.
    /// </summary>
    [Fact]
    public async Task UnknownCondition_FallsThrough()
    {
        // Arrange
        var script = SingleNodeScript("nonexistent_condition", "wait_respawn");
        var executor = this.CreateExecutor(script);

        // Act
        var result = await executor.TickAsync();

        // Assert — unknown condition evaluates to false, nothing executes
        Assert.False(result);
    }
}
