// <copyright file="FuguEvolutionService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.Evolution;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OAPS.Mind;

/// <summary>
/// F7: CMA-ES online tuning pipeline integrated across the AI swarm.
///
/// Runs a 5-minute evolution cycle where all 30 AI characters evaluate
/// candidate parameter vectors in parallel. The SwarmCMAES aggregates
/// rewards and evolves the distribution for the next generation.
///
/// Fugu mapping: This is the ES stage that refines routing after SFT.
/// </summary>
public sealed class FuguEvolutionService : IDisposable
{
    private readonly ILogger _logger;
    private readonly SwarmCMAES _optimizer;
    private readonly List<FuguOrchestrator> _orchestrators = new();
    private readonly System.Timers.Timer _evolutionTimer;
    private readonly ConcurrentQueue<EvolutionEvent> _pendingEvents = new();

    private int _generationCounter;
    private DateTime _lastEvolution = DateTime.MinValue;
    private static readonly TimeSpan EvolutionPeriod = TimeSpan.FromMinutes(5);

    public FuguEvolutionService(ILogger<FuguEvolutionService> logger)
    {
        _logger = logger;
        _optimizer = new SwarmCMAES(
            parameterCount: ActionSpace.Size,
            populationSize: 16,
            eliteCount: 4,
            initialSigma: 0.3f,
            logger: logger);

        _evolutionTimer = new System.Timers.Timer(EvolutionPeriod.TotalMilliseconds);
        _evolutionTimer.Elapsed += async (_, _) => await EvolveAsync().ConfigureAwait(false);
        _evolutionTimer.AutoReset = true;
    }

    /// <summary>Current generation number.</summary>
    public int Generation => _optimizer.Generation;

    /// <summary>Best reward achieved.</summary>
    public float BestReward { get; private set; }

    /// <summary>
    /// Registers an orchestrator for evolution.
    /// Each AI character's FuguOrchestrator participates in the swarm.
    /// </summary>
    public void RegisterOrchestrator(FuguOrchestrator orchestrator)
    {
        lock (_orchestrators)
        {
            _orchestrators.Add(orchestrator);
        }
    }

    /// <summary>
    /// Submits an evolution event from an AI character (per-step reward data).
    /// </summary>
    public void SubmitEvent(EvolutionEvent evt)
    {
        _pendingEvents.Enqueue(evt);
    }

    /// <summary>
    /// Starts the periodic evolution cycle.
    /// </summary>
    public void Start()
    {
        _evolutionTimer.Start();
        _logger.LogInformation("[FuguEvolution] Started: period={Period}min", EvolutionPeriod.TotalMinutes);
    }

    /// <summary>
    /// Manually triggers an evolution generation.
    /// </summary>
    public async Task<float> EvolveAsync(CancellationToken ct = default)
    {
        _generationCounter++;
        _logger.LogInformation("[FuguEvolution] Generation {Gen} starting...", _generationCounter);

        // Collect current orchestrator state
        FuguOrchestrator[] orchestrators;
        lock (_orchestrators)
        {
            orchestrators = _orchestrators.ToArray();
        }

        if (orchestrators.Length == 0)
        {
            _logger.LogWarning("[FuguEvolution] No orchestrators registered.");
            return 0f;
        }

        // Drain pending events
        var events = new List<EvolutionEvent>();
        while (_pendingEvents.TryDequeue(out var evt))
        {
            events.Add(evt);
        }

        // Sample population from current distribution
        var population = _optimizer.SamplePopulation();
        var rewards = new List<SwarmCMAES.EpisodeReward>();

        // Distribute candidates across orchestrators for parallel evaluation
        var tasks = new List<Task<(int Index, float Combat, float Survival, float Wealth, float Growth, float Social)>>();
        for (int k = 0; k < population.Count; k++)
        {
            int candidateIdx = k;
            var orchestrator = orchestrators[k % orchestrators.Length];

            tasks.Add(Task.Run(() =>
            {
                // Apply candidate parameters temporarily
                var originalBiases = (float[])orchestrator.Router.OnlineBiases.Clone();
                Array.Copy(population[candidateIdx], orchestrator.Router.OnlineBiases, ActionSpace.Size);

                // Evaluate using recent event data
                var candidateEvents = events
                    .Where(e => Math.Abs((e.Timestamp - DateTime.UtcNow).TotalMinutes) < 5)
                    .ToList();

                float combat = candidateEvents.Sum(e => e.Kills * 10f + e.DamageDealt * 0.01f);
                float survival = candidateEvents.Sum(e => -e.Deaths * 50f);
                float wealth = candidateEvents.Sum(e => e.ItemsPicked * 5f + e.ZenGained * 0.001f);
                float growth = candidateEvents.Sum(e => e.LevelsGained * 100f);
                float social = candidateEvents.Sum(e => e.InformationShares * 5f);

                // Restore original
                Array.Copy(originalBiases, orchestrator.Router.OnlineBiases, ActionSpace.Size);

                return (candidateIdx, combat, survival, wealth, growth, social);
            }, ct));
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var (idx, combat, survival, wealth, growth, social) in results)
        {
            rewards.Add(new SwarmCMAES.EpisodeReward
            {
                CandidateIndex = idx,
                Combat = combat,
                Survival = survival,
                Wealth = wealth,
                Growth = growth,
                Social = social,
            });
        }

        // Evolve the distribution
        BestReward = _optimizer.Evolve(rewards);

        // Apply best parameters to ALL orchestrators
        lock (_orchestrators)
        {
            foreach (var orch in _orchestrators)
            {
                _optimizer.ApplyToRouter(orch.Router);
            }
        }

        _logger.LogInformation(
            "[FuguEvolution] Gen {Gen} complete: best={Reward:F2}, sigma={Sigma:F4}, orchestrators={Orch}, events={Evt}",
            _generationCounter, BestReward, _optimizer.Sigma, orchestrators.Length, events.Count);

        _lastEvolution = DateTime.UtcNow;

        return BestReward;
    }

    public void Dispose()
    {
        _evolutionTimer.Stop();
        _evolutionTimer.Dispose();
    }
}

/// <summary>
/// Per-step evolution event from an AI character.
/// Submitted after each tick's outcome for aggregation by FuguEvolutionService.
/// </summary>
public record EvolutionEvent
{
    public int Kills { get; init; }
    public float DamageDealt { get; init; }
    public int Deaths { get; init; }
    public int ItemsPicked { get; init; }
    public int ZenGained { get; init; }
    public int LevelsGained { get; init; }
    public int InformationShares { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
