// <copyright file="FuguOrchestrator.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.Mind;

using System.IO;
using Microsoft.Extensions.Logging;
using OAPS.Evolution;
using MUnique.OpenMU.AIPlayer.Knowledge;

/// <summary>
/// Fugu-style orchestrator — the bridge between the Fugu components and the
/// existing AI lifecycle. This is the main entry point for v4.0 routing.
///
/// Lifecycle:
///   1. On AI tick (400ms): FuguOrchestrator.Decide(state) → Action
///   2. After action: FuguOrchestrator.RecordOutcome(reward)
///   3. Every 5 minutes: FuguOrchestrator.EvolveGeneration()
///   4. Periodic: FuguOrchestrator.TrainSoftClassifier()
///
/// This replaces the static priorityChain evaluation in ScriptExecutor with
/// Fugu-style per-step adaptive routing.
/// </summary>
public sealed class FuguOrchestrator : IDisposable
{
    private readonly ILogger _logger;
    private readonly SoftRouter _router;
    private readonly SoftBucketStore _bucketStore;
    private readonly SharedMemoryLayer _sharedMemory;
    private readonly SwarmCMAES _optimizer;

    // ═══ Tracking ═══
    private readonly List<PerStepRecord> _currentEpisode = new();
    private float _cumulativeReward;
    private int _episodeStepCount;

    // ═══ SFT training state ═══
    private DateTime _lastSftTraining = DateTime.MinValue;
    private static readonly TimeSpan SftTrainingInterval = TimeSpan.FromMinutes(30);
    private DateTime _lastEvolution = DateTime.MinValue;
    private static readonly TimeSpan EvolutionInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Initializes the Fugu orchestrator.
    /// </summary>
    /// <param name="sftWeightsPath">Path to SFT weights binary (null = random init).</param>
    /// <param name="workerId">Unique ID for this AI character (used for WorkerProfile).</param>
    /// <param name="sharedMemory">The shared memory layer (swarm-wide, singleton).</param>
    /// <param name="logger">Logger.</param>
    public FuguOrchestrator(
        string? sftWeightsPath,
        string workerId,
        SharedMemoryLayer sharedMemory,
        ILogger logger)
    {
        _logger = logger;
        _sharedMemory = sharedMemory;

        // Load or initialize SoftRouter
        float[,] sftWeights;
        if (sftWeightsPath is not null && File.Exists(sftWeightsPath))
        {
            sftWeights = SoftRouter.LoadSftWeights(sftWeightsPath);
        }
        else
        {
            sftWeights = InitializeRandomSftWeights();
            _logger.LogInformation("[Fugu] Initialized random SFT weights for worker {Worker}", workerId);
        }

        _router = new SoftRouter(sftWeights, logger);
        _bucketStore = new SoftBucketStore(logger);

        // Initialize CMA-ES from current router biases
        _optimizer = new SwarmCMAES(
            parameterCount: ActionSpace.Size,
            populationSize: 16,
            eliteCount: 4,
            initialMean: _router.OnlineBiases,
            initialSigma: 0.3f,
            logger: logger);
    }

    /// <summary>
    /// Gets the SoftRouter (for external access, e.g., personality modulation).
    /// </summary>
    public SoftRouter Router => _router;

    /// <summary>
    /// Gets the SharedMemoryLayer reference.
    /// </summary>
    public SharedMemoryLayer SharedMemory => _sharedMemory;

    /// <summary>
    /// Builds a StateVector from the current world state and shared memory context.
    /// This is the bridge from existing AiPlayer data to the Fugu routing system.
    /// </summary>
    public StateVector BuildStateVector(
        int hp, int maxHp, int mp, int maxMp,
        int level, bool isAtSafeZone, int freeSlots, bool isSurrounded,
        int targetCount, int nearestMonsterLevel, int nearbyItems,
        float hotspotDistance, float timeSinceLastKill, bool isDebuffed,
        int posX, int posY)
    {
        var (pheromone, _) = _sharedMemory.ReadPheromone(posX, posY);
        var nearbyAiCount = 0; // TODO: from SharedMemory's worker tracking
        var bestRouteScore = _sharedMemory.GetBestRouteScore(posX, posY);
        var estimatedDropRate = _sharedMemory.GetDropRate(posX / 16, posY / 16);

        return new StateVector
        {
            HpPercent = maxHp > 0 ? (float)hp / maxHp : 0f,
            MpPercent = maxMp > 0 ? (float)mp / maxMp : 0f,
            Level = level,
            IsAtSafeZone = isAtSafeZone,
            InventorySlotsFree = freeSlots,
            IsSurrounded = isSurrounded,
            TargetCount = targetCount,
            NearestMonsterLevel = nearestMonsterLevel,
            NearbyItemCount = nearbyItems,
            HotspotDistance = hotspotDistance,
            TimeSinceLastKill = timeSinceLastKill,
            IsDebuffed = isDebuffed,
            NearbyAiCount = nearbyAiCount,
            PheromoneIntensity = pheromone,
            BestRouteScore = bestRouteScore,
            EstimatedDropRate = estimatedDropRate,
        };
    }

    /// <summary>
    /// Makes a decision — the core Fugu routing call replacing static priorityChain.
    /// </summary>
    /// <param name="state">Current state vector.</param>
    /// <param name="personalityTemperature">Temperature from PersonalityEngine (1.0 = neutral).</param>
    /// <returns>The selected action index and its name.</returns>
    public (int ActionIndex, string ActionName, float Confidence) Decide(
        StateVector state, float personalityTemperature = 1.0f)
    {
        _router.SetTemperature(personalityTemperature);
        var distribution = _router.Evaluate(state);

        // Sample (stochastic) for exploration, with safety override
        int action;
        if (state.HpPercent < 0.15f && !state.IsAtSafeZone)
        {
            // Safety override: low HP → force flee or heal
            action = state.HpPercent < 0.1f
                ? ActionSpace.FleeSafeZone
                : ActionSpace.HealPotion;
        }
        else
        {
            action = _router.SampleAction(distribution);
        }

        var confidence = distribution.Probabilities[action];

        // Record for later learning
        _currentEpisode.Add(new PerStepRecord
        {
            State = state,
            SelectedAction = action,
            Distribution = distribution,
            Timestamp = DateTime.UtcNow,
        });

        _episodeStepCount++;

        return (action, ActionSpace.Names[action], confidence);
    }

    /// <summary>
    /// Records the outcome of the last action (Fugu's per-step empirical feedback).
    /// </summary>
    public void RecordOutcome(
        bool success, float damageDealt, int kills, int deaths,
        int itemsPicked, int zenGained, int levelsGained, int skillsLearned)
    {
        if (_currentEpisode.Count == 0) return;

        var lastStep = _currentEpisode[^1];
        var reward = SwarmCMAES.ComputeFitness(
            kills, damageDealt, deaths,
            itemsPicked, zenGained, levelsGained,
            skillsLearned, informationShares: 0);

        _router.RecordReward(lastStep.SelectedAction, reward);
        _cumulativeReward += reward;

        // Deposit pheromone at last known position (if kill happened)
        if (kills > 0 && lastStep.State is { } s)
        {
            // Approximate position from state context
            _sharedMemory.DepositPheromone(128, 128, 0.1f * kills, 0.5f);
        }
    }

    /// <summary>
    /// Runs a CMA-ES evolution generation.
    /// This should be called every 5 minutes across the swarm.
    /// </summary>
    public async Task<float> EvolveGenerationAsync(CancellationToken ct = default)
    {
        if (DateTime.UtcNow - _lastEvolution < EvolutionInterval)
        {
            return _cumulativeReward;
        }

        _lastEvolution = DateTime.UtcNow;

        // Sample population from current distribution
        var population = _optimizer.SamplePopulation();

        // In a real swarm, each candidate would be evaluated on a different AI character.
        // Here we simulate evaluation using recorded rewards.
        var rewards = new List<SwarmCMAES.EpisodeReward>();
        for (int k = 0; k < population.Count; k++)
        {
            // Apply candidate to router temporarily
            var originalBiases = (float[])_router.OnlineBiases.Clone();
            Array.Copy(population[k], _router.OnlineBiases, ActionSpace.Size);

            // Evaluate: use cumulative reward as a proxy
            var fitness = _cumulativeReward;
            rewards.Add(new SwarmCMAES.EpisodeReward
            {
                CandidateIndex = k,
                Combat = fitness * 0.4f,
                Survival = fitness * 0.2f,
                Wealth = fitness * 0.2f,
                Growth = fitness * 0.15f,
                Social = fitness * 0.05f,
            });

            // Restore original
            Array.Copy(originalBiases, _router.OnlineBiases, ActionSpace.Size);
        }

        var bestReward = _optimizer.Evolve(rewards);

        // Apply best evolved parameters back to router
        _optimizer.ApplyToRouter(_router);

        // Update worker profile
        _sharedMemory.UpdateWorkerProfile("self", "combat", rewards[0].Combat);
        _sharedMemory.UpdateWorkerProfile("self", "survival", rewards[0].Survival);

        _logger.LogInformation(
            "[Fugu] Generation {Gen} complete. Best reward: {Reward:F2}, Sigma: {Sigma:F4}",
            _optimizer.Generation, bestReward, _optimizer.Sigma);

        // Persist SFT weights periodically
        if (DateTime.UtcNow - _lastSftTraining > SftTrainingInterval)
        {
            _lastSftTraining = DateTime.UtcNow;
            await SaveWeightsAsync().ConfigureAwait(false);
        }

        // Reset episode tracking
        _currentEpisode.Clear();
        _cumulativeReward = 0f;
        _episodeStepCount = 0;

        return bestReward;
    }

    /// <summary>
    /// Trains the soft classifier from accumulated behavior data.
    /// </summary>
    public void TrainSoftClassifier(IEnumerable<SoftBucketEntry>? entries = null)
    {
        if (entries is not null)
        {
            foreach (var entry in entries)
            {
                _bucketStore.AddEntry(entry);
            }
        }

        _bucketStore.TrainFromEntries(learningRate: 0.01f, epochs: 5, batchSize: 64);
    }

    /// <summary>
    /// Saves learned weights to disk.
    /// </summary>
    public async Task SaveWeightsAsync()
    {
        var scriptsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "learned");
        Directory.CreateDirectory(scriptsDir);

        var sftPath = Path.Combine(scriptsDir, "fugu_sft_weights.bin");
        _router.SaveSftWeights(sftPath);

        var classifierPath = Path.Combine(scriptsDir, "fugu_classifier_weights.bin");
        _bucketStore.SaveWeights(classifierPath);

        _logger.LogInformation("[Fugu] Saved weights: SFT={Sft}, Classifier={Cls}", sftPath, classifierPath);
    }

    /// <summary>
    /// Gets diagnostic information for debugging.
    /// </summary>
    public FuguDiagnostics GetDiagnostics()
    {
        return new FuguDiagnostics
        {
            Generation = _optimizer.Generation,
            Sigma = _optimizer.Sigma,
            CumulativeReward = _cumulativeReward,
            EpisodeStepCount = _episodeStepCount,
            OnlineBiases = (float[])_router.OnlineBiases.Clone(),
            RouterTemperature = 1.0f, // actual from current personality
        };
    }

    public void Dispose()
    {
        // Save weights on disposal
        SaveWeightsAsync().GetAwaiter().GetResult();
    }

    // ═══ Private helpers ═══

    private static float[,] InitializeRandomSftWeights()
    {
        var weights = new float[StateVector.Dimension, ActionSpace.Size];
        float scale = MathF.Sqrt(2f / StateVector.Dimension);
        for (int d = 0; d < StateVector.Dimension; d++)
        {
            for (int a = 0; a < ActionSpace.Size; a++)
            {
                weights[d, a] = (float)(Random.Shared.NextDouble() * 2 - 1) * scale * 0.1f;
            }
        }

        return weights;
    }

    private readonly record struct PerStepRecord
    {
        public StateVector State { get; init; }
        public int SelectedAction { get; init; }
        public ActionDistribution Distribution { get; init; }
        public DateTime Timestamp { get; init; }
    }
}

/// <summary>
/// Diagnostic snapshot of the Fugu orchestrator state.
/// </summary>
public record FuguDiagnostics
{
    public int Generation { get; init; }
    public float Sigma { get; init; }
    public float CumulativeReward { get; init; }
    public int EpisodeStepCount { get; init; }
    public float[] OnlineBiases { get; init; } = Array.Empty<float>();
    public float RouterTemperature { get; init; }
}
