// <copyright file="SwarmCMAES.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.Evolution;

using Microsoft.Extensions.Logging;
using OAPS.Mind;

/// <summary>
/// Fugu-style sep-CMA-ES (Separable Covariance Matrix Adaptation Evolution Strategy).
///
/// Replaces v3.0 simple genetic algorithm with population-based distribution optimization:
///   1. Sample μ candidates from current distribution N(θ_t, σ_t · D_t)
///   2. Run each candidate on an AI character for an evaluation episode (5 min)
///   3. Collect per-step rewards: Combat + Survival + Wealth + Growth + Social
///   4. Rank by fitness, take top-λ elite
///   5. Recombine via fitness-weighted averaging → update θ_{t+1}, σ_{t+1}, D_{t+1}
///
/// Fugu mapping: This is the ES stage that refines routing behavior at fine granularity
/// after SFT provides a strong initialization region.
/// </summary>
public sealed class SwarmCMAES
{
    private readonly ILogger _logger;

    // ═══ Population parameters ═══
    private readonly int _populationSize;    // μ — total candidates per generation
    private readonly int _eliteCount;         // λ — number of elites kept
    private readonly int _parameterCount;     // n — dimension of the search space

    // ═══ Distribution parameters ═══
    private float[] _mean;                    // θ_t — current mean vector
    private float _sigma;                     // σ_t — global step size
    private float[] _diagScale;               // D_t — separable diagonal scaling (sep-CMA-ES)

    // ═══ CMA-ES internal state ═══
    private float[] _ps;                      // Evolution path for sigma
    private float[] _pc;                      // Evolution path for C (simplified for sep-CMA)
    private int _generation;

    // ═══ Constants ═══
    private const float MinSigma = 0.001f;
    private const float MaxSigma = 1.0f;
    private const float SigmaLearningRate = 0.3f;
    private const float DampingFactor = 0.5f;

    /// <summary>
    /// Reward per evaluation episode — aggregated from per-step rewards.
    /// </summary>
    public record EpisodeReward
    {
        public int CandidateIndex { get; init; }
        public float Combat { get; init; }      // Kills × 10
        public float Survival { get; init; }     // HP recovered × 0.05 - Deaths × 50
        public float Wealth { get; init; }       // Items picked × 5 + Zen × 0.001
        public float Growth { get; init; }       // Levels gained × 100
        public float Social { get; init; }       // Information shared × 5
        public float Total => Combat + Survival + Wealth + Growth + Social;
    }

    /// <summary>
    /// Initializes the CMA-ES optimizer.
    /// </summary>
    /// <param name="parameterCount">Number of parameters to optimize (e.g., ActionSpace.Size for biases).</param>
    /// <param name="populationSize">Candidates per generation (default: 16 for 30-AI parallel eval).</param>
    /// <param name="eliteCount">Number of elites kept per generation.</param>
    /// <param name="initialMean">Initial parameter vector (usually from SFT stage).</param>
    /// <param name="initialSigma">Initial step size.</param>
    /// <param name="logger">Logger.</param>
    public SwarmCMAES(
        int parameterCount,
        int populationSize = 16,
        int eliteCount = 4,
        float[]? initialMean = null,
        float initialSigma = 0.3f,
        ILogger? logger = null)
    {
        _parameterCount = parameterCount;
        _populationSize = Math.Min(populationSize, 32);
        _eliteCount = Math.Min(eliteCount, _populationSize / 2);
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        // Initialize mean from SFT stage or zeros
        _mean = initialMean ?? new float[parameterCount];
        if (initialMean is null)
        {
            // Heuristic init: small random values
            for (int i = 0; i < parameterCount; i++)
            {
                _mean[i] = (float)(Random.Shared.NextDouble() * 0.1 - 0.05);
            }
        }

        _sigma = initialSigma;
        _diagScale = new float[parameterCount];
        Array.Fill(_diagScale, 1.0f);

        _ps = new float[parameterCount];
        _pc = new float[parameterCount];
        _generation = 0;
    }

    /// <summary>
    /// Current generation number.
    /// </summary>
    public int Generation => _generation;

    /// <summary>
    /// Current mean vector (best parameter estimate).
    /// </summary>
    public float[] Mean => _mean;

    /// <summary>
    /// Current step size.
    /// </summary>
    public float Sigma => _sigma;

    /// <summary>
    /// Samples a population of candidates from the current distribution.
    /// Each candidate is a parameter vector: θ^(k) = θ_t + σ_t · D_t · z^(k)
    /// </summary>
    public List<float[]> SamplePopulation()
    {
        var population = new List<float[]>(_populationSize);
        for (int k = 0; k < _populationSize; k++)
        {
            var candidate = new float[_parameterCount];
            for (int i = 0; i < _parameterCount; i++)
            {
                // Sample from N(0, 1)
                var z = SampleGaussian();
                candidate[i] = _mean[i] + _sigma * _diagScale[i] * z;
            }

            population.Add(candidate);
        }

        return population;
    }

    /// <summary>
    /// Evolves the distribution using the elite candidates' rewards.
    /// This is the core Fugu-style CMA-ES update:
    ///   θ_{t+1} = θ_t + σ_t · D_t · Σ_{j=1}^λ w_j · z_{j:λ}
    /// </summary>
    /// <param name="rewards">Fitness scores for each candidate.</param>
    /// <returns>Best reward achieved this generation.</returns>
    public float Evolve(List<EpisodeReward> rewards)
    {
        if (rewards.Count == 0)
        {
            _logger.LogWarning("[CMA-ES] No rewards to evolve from.");
            return 0f;
        }

        _generation++;

        // Sort by total reward descending
        var ranked = rewards.OrderByDescending(r => r.Total).ToList();
        var elites = ranked.Take(_eliteCount).ToList();

        // Compute fitness weights (log-weighted as in sep-CMA-ES)
        var weights = new float[_eliteCount];
        float weightSum = 0f;
        for (int j = 0; j < _eliteCount; j++)
        {
            weights[j] = MathF.Log(_eliteCount + 0.5f) - MathF.Log(j + 1.0f);
            weightSum += weights[j];
        }

        for (int j = 0; j < _eliteCount; j++)
        {
            weights[j] /= weightSum;
        }

        // Compute weighted average of elite parameters
        var newMean = new float[_parameterCount];
        var weightedZ = new float[_parameterCount];

        for (int j = 0; j < _eliteCount; j++)
        {
            for (int i = 0; i < _parameterCount; i++)
            {
                newMean[i] += weights[j] * elites[j].CandidateIndex;
                // Approximate z from the distribution
                weightedZ[i] += weights[j] * (elites[j].CandidateIndex - _mean[i]) / (_sigma * _diagScale[i]);
            }
        }

        // Update evolution paths and step size (sep-CMA-ES style)
        float muEff = 1f / weights.Sum(w => w * w);
        float cSigma = (muEff + 2f) / (_parameterCount + muEff + 5f);

        for (int i = 0; i < _parameterCount; i++)
        {
            _ps[i] = (1f - cSigma) * _ps[i] + MathF.Sqrt(cSigma * (2f - cSigma) * muEff) * weightedZ[i];
        }

        // Update sigma based on evolution path norm
        float psNorm = MathF.Sqrt(_ps.Sum(x => x * x));
        float expectedNorm = MathF.Sqrt(_parameterCount);
        _sigma *= MathF.Exp((psNorm / expectedNorm - 1f) * SigmaLearningRate / DampingFactor);
        _sigma = Math.Clamp(_sigma, MinSigma, MaxSigma);

        // Update mean
        for (int i = 0; i < _parameterCount; i++)
        {
            _mean[i] += _sigma * _diagScale[i] * weightedZ[i];
        }

        // Update diagonal scaling (simplified)
        float cDiag = 0.1f / _parameterCount;
        for (int i = 0; i < _parameterCount; i++)
        {
            _pc[i] = (1f - cDiag) * _pc[i] + MathF.Sqrt(cDiag * (2f - cDiag) * muEff) * weightedZ[i];
            _diagScale[i] *= MathF.Exp(cDiag / DampingFactor * (_pc[i] * _pc[i] - 1f));
            _diagScale[i] = Math.Clamp(_diagScale[i], 0.1f, 10f);
        }

        var bestReward = elites[0].Total;
        _logger.LogInformation(
            "[CMA-ES] Gen {Gen}: best reward={Reward:F2}, sigma={Sigma:F4}, elites={Elites}",
            _generation, bestReward, _sigma, _eliteCount);

        return bestReward;
    }

    /// <summary>
    /// Computes a combined reward from per-step metrics.
    /// </summary>
    public static float ComputeFitness(
        int kills, float damageDealt, int deaths,
        int itemsPicked, int zenGained, int levelsGained,
        int skillsLearned, int informationShares)
    {
        return kills * 10f
               + damageDealt * 0.01f
               - deaths * 50f
               + itemsPicked * 5f
               + zenGained * 0.001f
               + levelsGained * 100f
               + skillsLearned * 50f
               + informationShares * 5f;
    }

    /// <summary>
    /// Applies the evolved parameters to a SoftRouter's online biases.
    /// </summary>
    public void ApplyToRouter(SoftRouter router)
    {
        var biases = router.OnlineBiases;
        Array.Copy(_mean, biases, Math.Min(_mean.Length, biases.Length));
    }

    // ═══ Private ═══

    /// <summary>
    /// Samples from N(0, 1) using Box-Muller transform.
    /// </summary>
    private static float SampleGaussian()
    {
        float u1 = MathF.Max(float.Epsilon, (float)Random.Shared.NextDouble());
        float u2 = (float)Random.Shared.NextDouble();
        return MathF.Sqrt(-2f * MathF.Log(u1)) * MathF.Cos(2f * MathF.PI * u2);
    }
}
