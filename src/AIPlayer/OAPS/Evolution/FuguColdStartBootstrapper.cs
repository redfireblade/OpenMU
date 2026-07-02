// <copyright file="FuguColdStartBootstrapper.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.Evolution;

using System.IO;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.AIPlayer;
using OAPS.Mind;

/// <summary>
/// Cold-start bootstrapper for SFT weights.
///
/// Fugu paper insight: "supervision is derived directly from measured worker performance
/// and does not require generation from the orchestrator itself."
///
/// This bootstraps initial SFT weights from the existing v3.0 rule engine's performance
/// data, avoiding random initialization and providing a strong starting region for CMA-ES.
/// </summary>
public sealed class FuguColdStartBootstrapper
{
    private readonly ILogger _logger;

    /// <summary>SFT weights output directory.</summary>
    public string WeightsDir { get; }

    public FuguColdStartBootstrapper(ILogger<FuguColdStartBootstrapper> logger, string? weightsDir = null)
    {
        _logger = logger;
        WeightsDir = weightsDir ?? Path.Combine(AppContext.BaseDirectory, "scripts", "learned");
        Directory.CreateDirectory(WeightsDir);
    }

    /// <summary>
    /// Generates initial SFT weights from rule-engine-based heuristics.
    /// Uses domain knowledge to create a reasonable starting distribution:
    /// - Low HP → high probability for heal/flee
    /// - Many targets → high probability for combat
    /// - Nearby items → high probability for pickup
    /// - No monsters → high probability for patrol/relocate
    ///
    /// This is the Fugu SFT-stage equivalent: broad coverage of expected behaviors
    /// without needing actual online training data.
    /// </summary>
    public float[,] BootstrapWeights()
    {
        var weights = new float[StateVector.Dimension, ActionSpace.Size];

        // Heuristic initialization based on domain knowledge (Fugu SFT-stage approximation)
        // Each column = action, each row = how much state feature influences that action

        // heal_potion (0): HP% low → high weight
        weights[0, 0] = -2.0f;    // HpPercent↓ → heal↑ (negative = inverse correlation)
        weights[4, 0] = 0.5f;     // in safe zone → don't heal

        // attack_melee (1): targets > 0 → high weight
        weights[6, 1] = 1.5f;     // TargetCount↑ → attack↑
        weights[0, 1] = 1.0f;     // HP good → can attack
        weights[4, 1] = -1.0f;    // safe zone → don't attack
        weights[5, 1] = 1.0f;     // surrounded → attack

        // attack_skill (2): similar to melee but prefers MP available
        weights[6, 2] = 1.5f;     // TargetCount↑ → skill↑
        weights[1, 2] = 1.5f;     // MP% high → can use skill
        weights[0, 2] = 1.0f;     // HP good
        weights[4, 2] = -1.0f;    // safe zone → don't skill

        // pickup_nearby (3): items nearby → pickup
        weights[8, 3] = 2.0f;     // NearbyItemCount↑ → pickup↑
        weights[3, 3] = 0.5f;     // inventory free → worth picking up

        // random_patrol (4): no targets → patrol
        weights[6, 4] = -1.5f;    // TargetCount↑ → don't patrol
        weights[9, 4] = 1.0f;     // far from hotspot → patrol to find one
        weights[10, 4] = -1.0f;   // recently killed → still have targets

        // relocate_hotspot (5): no recent kills, far from hotspot
        weights[10, 5] = -2.0f;   // TimeSinceLastKill high → relocate
        weights[9, 5] = 1.5f;     // HotspotDistance high → relocate
        weights[13, 5] = 1.0f;    // PheromoneIntensity → go where others went

        // flee_safezone (6): HP critically low
        weights[0, 6] = -3.0f;    // HpPercent low → flee
        weights[4, 6] = -2.0f;    // not in safe zone → flee
        weights[5, 6] = 1.5f;     // surrounded → flee

        // wait (7): default, low probability
        for (int d = 0; d < StateVector.Dimension; d++)
        {
            weights[d, 7] = -0.3f; // everything reduces wait probability
        }

        // Normalize each action column to [-1, 1] range
        for (int a = 0; a < ActionSpace.Size; a++)
        {
            float maxAbs = 0f;
            for (int d = 0; d < StateVector.Dimension; d++)
            {
                if (MathF.Abs(weights[d, a]) > maxAbs)
                    maxAbs = MathF.Abs(weights[d, a]);
            }

            if (maxAbs > 1f)
            {
                for (int d = 0; d < StateVector.Dimension; d++)
                {
                    weights[d, a] /= maxAbs;
                }
            }
        }

        var path = Path.Combine(WeightsDir, "fugu_sft_weights_bootstrap.bin");
        SaveWeights(weights, path);
        _logger.LogInformation("[FuguBoot] Bootstrapped SFT weights saved to {Path}", path);

        return weights;
    }

    /// <summary>
    /// Refines the bootstrapped weights using observed performance data
    /// from the BehaviorEventStore. Maps Fugu's "measured worker performance"
    /// to weight adjustments.
    /// </summary>
    public float[,] RefineFromEvents(BehaviorEventStore eventStore, float[,] baseWeights)
    {
        var allChars = eventStore.GetAllCharacterNames();
        if (allChars.Count == 0) return baseWeights;

        int totalKills = 0;
        int totalEvents = 0;
        foreach (var name in allChars)
        {
            var events = eventStore.GetEvents(name);
            totalKills += events.Count(e => e.EventType == BehaviorEventType.MonsterKilled);
            totalEvents += events.Count;
        }

        if (totalEvents == 0) return baseWeights;

        // Adjust weights based on observed data: more kills → increase combat weight
        float combatRatio = Math.Min((float)totalKills / totalEvents, 1f);
        float adjustment = 1f + combatRatio * 0.5f; // Scale combat actions up to 1.5x

        for (int d = 0; d < StateVector.Dimension; d++)
        {
            baseWeights[d, ActionSpace.AttackMelee] *= adjustment;
            baseWeights[d, ActionSpace.AttackSkill] *= adjustment;
            baseWeights[d, ActionSpace.Wait] *= 1f - combatRatio * 0.3f;
        }

        _logger.LogInformation(
            "[FuguBoot] Refined weights from {Chars} chars, {Kills} kills, {Events} events, combatRatio={Ratio:F2}",
            allChars.Count, totalKills, totalEvents, combatRatio);

        return baseWeights;
    }

    private static void SaveWeights(float[,] weights, string path)
    {
        var bytes = new byte[StateVector.Dimension * ActionSpace.Size * sizeof(float)];
        Buffer.BlockCopy(weights, 0, bytes, 0, bytes.Length);
        File.WriteAllBytes(path, bytes);
    }
}
