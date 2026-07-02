// <copyright file="SoftRouter.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.Mind;

using System.IO;
using Microsoft.Extensions.Logging;
using OAPS.Evolution;

/// <summary>
/// Fugu-style per-step adaptive router.
///
/// Architecture (maps to Fugu's SelectionHead):
///   StateVector(h) → SFT weight matrix → logits → + CMA-ES online offset
///   → softmax (modulated by PersonalityEngine temperature) → ActionDistribution
///   → sample or argmax → selected Action
///
/// Two-stage training:
///   Stage 1 (SFT): Offline training from behavior_events.json → learns broad routing baseline
///   Stage 2 (ES):   Online CMA-ES fine-tuning → adapts to complex multi-step patterns
///
/// This replaces the static priorityChain in v3.0 scripts.
/// </summary>
public sealed class SoftRouter
{
    private readonly ILogger _logger;

    // ═══ SFT Stage: Pretrained weights (offline, from behavior_events.json) ═══
    // Shape: [StateVector.Dimension, ActionSpace.Size]
    // Each column is a weight vector for one action's logit computation.
    private readonly float[,] _sftWeights;

    // ═══ ES Stage: Online CMA-ES offset parameters (per-action bias terms) ═══
    // These are evolved online by SwarmCMAES to capture patterns SFT missed.
    private readonly float[] _onlineBiases;

    // ═══ Temperature for softmax (modulated by PersonalityEngine) ═══
    private float _temperature = 1.0f;

    /// <summary>
    /// Initializes the SoftRouter.
    /// </summary>
    /// <param name="sftWeights">SFT-trained weight matrix [StateDim, ActionCount].</param>
    /// <param name="logger">Logger.</param>
    public SoftRouter(float[,] sftWeights, ILogger logger)
    {
        _sftWeights = sftWeights;
        _logger = logger;
        _onlineBiases = new float[ActionSpace.Size];
    }

    /// <summary>
    /// Gets or sets the online bias parameters (from CMA-ES evolution).
    /// </summary>
    public float[] OnlineBiases => _onlineBiases;

    /// <summary>
    /// Sets the temperature for softmax (1.0 = neutral, &lt;1 = more deterministic, &gt;1 = more exploratory).
    /// </summary>
    public void SetTemperature(float temperature)
    {
        _temperature = Math.Clamp(temperature, 0.1f, 5.0f);
    }

    /// <summary>
    /// Evaluates the action distribution for the given state.
    /// This is the core Fugu mapping: hidden_state → action_logits → softmax → distribution.
    /// </summary>
    /// <param name="state">Current state vector.</param>
    /// <returns>Probability distribution over actions.</returns>
    public ActionDistribution Evaluate(StateVector state)
    {
        var features = state.Encode();
        var logits = new float[ActionSpace.Size];

        // SFT baseline: feature × weight matrix → raw logits
        for (int a = 0; a < ActionSpace.Size; a++)
        {
            float logit = 0f;
            for (int d = 0; d < StateVector.Dimension; d++)
            {
                logit += features[d] * _sftWeights[d, a];
            }

            logits[a] = logit + _onlineBiases[a];
        }

        // Softmax with temperature
        var probs = SoftmaxWithTemperature(logits, _temperature);

        return new ActionDistribution(logits, probs);
    }

    /// <summary>
    /// Samples an action index from the distribution (stochastic routing, like Fugu).
    /// </summary>
    public int SampleAction(ActionDistribution distribution, float? explorationOverride = null)
    {
        var probs = distribution.Probabilities;
        var rng = explorationOverride ?? (float)Random.Shared.NextDouble();

        float cumulative = 0f;
        for (int i = 0; i < probs.Length; i++)
        {
            cumulative += probs[i];
            if (rng <= cumulative)
            {
                return i;
            }
        }

        // Fallback: argmax
        return ArgMax(probs);
    }

    /// <summary>
    /// Selects the best action deterministically (argmax, for critical safety decisions).
    /// </summary>
    public int SelectBestAction(ActionDistribution distribution)
    {
        return ArgMax(distribution.Probabilities);
    }

    /// <summary>
    /// Records the reward for the last action, updating online biases via gradient-like adjustment.
    /// This is the per-step empirical feedback that drives Fugu's routing improvement.
    /// </summary>
    public void RecordReward(int actionIndex, float reward, float learningRate = 0.01f)
    {
        // Simple SGD update: bias += lr * reward * (1 - prob_of_this_action)
        // Positive reward → increase bias for this action
        // Negative reward → decrease bias for this action
        _onlineBiases[actionIndex] += learningRate * reward;

        _logger.LogDebug(
            "[SoftRouter] Reward {Reward:F3} for action {Action} → bias now {Bias:F4}",
            reward, ActionSpace.Names[actionIndex], _onlineBiases[actionIndex]);
    }

    /// <summary>
    /// Loads SFT weights from a binary file (trained offline from behavior_events.json).
    /// </summary>
    public static float[,] LoadSftWeights(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        var weights = new float[StateVector.Dimension, ActionSpace.Size];
        Buffer.BlockCopy(bytes, 0, weights, 0, bytes.Length);
        return weights;
    }

    /// <summary>
    /// Saves SFT weights to a binary file.
    /// </summary>
    public void SaveSftWeights(string filePath)
    {
        var bytes = new byte[StateVector.Dimension * ActionSpace.Size * sizeof(float)];
        Buffer.BlockCopy(_sftWeights, 0, bytes, 0, bytes.Length);
        File.WriteAllBytes(filePath, bytes);
    }

    // ═══ Private helpers ═══

    private static float[] SoftmaxWithTemperature(float[] logits, float temperature)
    {
        var result = new float[logits.Length];
        float maxLogit = float.MinValue;
        for (int i = 0; i < logits.Length; i++)
        {
            if (logits[i] > maxLogit) maxLogit = logits[i];
        }

        float sum = 0f;
        for (int i = 0; i < logits.Length; i++)
        {
            result[i] = MathF.Exp((logits[i] - maxLogit) / temperature);
            sum += result[i];
        }

        if (sum > 0f)
        {
            for (int i = 0; i < result.Length; i++)
            {
                result[i] /= sum;
            }
        }
        else
        {
            // Uniform fallback
            Array.Fill(result, 1f / result.Length);
        }

        return result;
    }

    private static int ArgMax(float[] values)
    {
        int best = 0;
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] > values[best]) best = i;
        }

        return best;
    }
}

/// <summary>
/// Action probability distribution — the output of SoftRouter.Evaluate().
/// </summary>
public readonly record struct ActionDistribution(float[] Logits, float[] Probabilities);

/// <summary>
/// The action space definition — all possible actions the router can select.
/// Needs to be kept in sync with the SFT training process.
/// </summary>
public static class ActionSpace
{
    public const int Size = 8;

    // Action indices (must match training order)
    public const int HealPotion = 0;
    public const int AttackMelee = 1;
    public const int AttackSkill = 2;
    public const int PickupNearby = 3;
    public const int RandomPatrol = 4;
    public const int RelocateHotspot = 5;
    public const int FleeSafeZone = 6;
    public const int Wait = 7;

    public static readonly string[] Names =
    {
        "heal_potion",
        "attack_melee",
        "attack_skill",
        "pickup_nearby",
        "random_patrol",
        "relocate_hotspot",
        "flee_safezone",
        "wait",
    };
}
