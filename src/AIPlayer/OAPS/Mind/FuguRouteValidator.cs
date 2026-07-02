// <copyright file="FuguRouteValidator.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.Mind;

using Microsoft.Extensions.Logging;

/// <summary>
/// Fugu-style two-tier route validation.
///
/// Fugu paper: "setting r_i to 0 for responses from which the lists of subtasks,
/// worker agents, and access lists cannot be parsed" (Format condition)
/// and 0.5 for incomplete but well-formatted workflows (Correctness condition).
///
/// Applied to OAPS: every SoftRouter decision is validated through:
///   Tier 1 (Format): Is the action index valid? Is the state vector complete?
///   Tier 2 (Correctness): Would this action make things worse? (safety override)
/// </summary>
public sealed class FuguRouteValidator
{
    private readonly ILogger _logger;

    // Safety thresholds
    private const float CriticalHpFraction = 0.08f;
    private const float LowHpFraction = 0.20f;
    private const int MaxLevelDiff = 20;

    public FuguRouteValidator(ILogger<FuguRouteValidator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Validates a routing decision. Returns the validated action index
    /// (may be overridden by safety rules) and a reward modifier.
    ///
    /// Reward structure (Fugu-style):
    ///   0.0 = format error (invalid action)
    ///   0.5 = well-formatted but likely incorrect
    ///   1.0 = valid and likely correct
    /// </summary>
    public (int ActionIndex, float RewardModifier) Validate(
        StateVector state, ActionDistribution dist, int proposedAction)
    {
        // Tier 1: Format check
        if (proposedAction < 0 || proposedAction >= ActionSpace.Size)
        {
            _logger.LogWarning("[FuguVal] Format error: invalid action index {Idx}", proposedAction);
            return (ActionSpace.Wait, 0.0f);
        }

        // Check for state vector completeness
        if (float.IsNaN(state.HpPercent) || float.IsNaN(state.MpPercent))
        {
            _logger.LogWarning("[FuguVal] Format error: NaN in state vector");
            return (ActionSpace.Wait, 0.0f);
        }

        float rewardModifier = 1.0f;

        // Tier 2: Correctness checks with safety overrides

        // Safety override 1: HP critically low → must flee or heal
        if (state.HpPercent <= CriticalHpFraction && !state.IsAtSafeZone)
        {
            int safeAction = state.MpPercent > 0.1f ? ActionSpace.FleeSafeZone : ActionSpace.HealPotion;
            if (proposedAction != safeAction)
            {
                _logger.LogDebug(
                    "[FuguVal] Safety override: HP={Hp:F1}% → action {From}→{To}",
                    state.HpPercent * 100, ActionSpace.Names[proposedAction], ActionSpace.Names[safeAction]);
                return (safeAction, 0.5f); // Correctness penalty: well-formatted but unsafe
            }
        }

        // Safety override 2: At safe zone → don't attack or flee
        if (state.IsAtSafeZone && (proposedAction == ActionSpace.AttackMelee
                                    || proposedAction == ActionSpace.AttackSkill
                                    || proposedAction == ActionSpace.FleeSafeZone))
        {
            _logger.LogDebug("[FuguVal] Safety override: safe zone → no combat/flee");
            return (ActionSpace.Wait, 0.5f);
        }

        // Safety override 3: Level diff too large → don't attack
        if (state.NearestMonsterLevel - state.Level > MaxLevelDiff
            && (proposedAction == ActionSpace.AttackMelee || proposedAction == ActionSpace.AttackSkill))
        {
            if (dist.Probabilities[ActionSpace.RelocateHotspot] > 0.1f)
            {
                return (ActionSpace.RelocateHotspot, 0.5f);
            }

            rewardModifier = 0.5f; // Penalize but allow if no better option
        }

        // Safety override 4: No targets but proposed attack → redirect
        if (state.TargetCount == 0
            && (proposedAction == ActionSpace.AttackMelee || proposedAction == ActionSpace.AttackSkill))
        {
            if (dist.Probabilities[ActionSpace.RandomPatrol] > 0.1f)
            {
                return (ActionSpace.RandomPatrol, 0.5f);
            }

            return (ActionSpace.Wait, 0.0f);
        }

        return (proposedAction, rewardModifier);
    }
}
