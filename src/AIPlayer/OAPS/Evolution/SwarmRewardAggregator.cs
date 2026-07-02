// <copyright file="SwarmRewardAggregator.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.Evolution;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.AIPlayer.Knowledge;

/// <summary>
/// F12: Cross-character reward aggregation and social propagation.
///
/// Collects per-step rewards from all AI characters, merges them at the swarm
/// level, and propagates optimized parameters back through the SharedMemoryLayer's
/// pheromone system. This enables Fugu-style "group-level learning" where
/// individual discoveries benefit the entire swarm.
///
/// Aggregation pipeline:
///   1. Each AI submits per-tick reward → buffered per-character
///   2. Every 5min: aggregate all rewards → compute swarm-level statistics
///   3. Update CMA-ES distribution from swarm-level fitness
///   4. Propagate learning to SharedMemoryLayer → pheromone deposits
/// </summary>
public sealed class SwarmRewardAggregator : IDisposable
{
    private readonly ILogger _logger;
    private readonly SharedMemoryLayer _sharedMemory;
    private readonly System.Timers.Timer _aggregateTimer;
    private MUnique.OpenMU.AIPlayer.Decision.FuguKanbanBoard? _kanban;

    /// <summary>Per-character reward buffers.</summary>
    private readonly Dictionary<string, CharacterRewardBuffer> _buffers = new();
    private readonly object _lock = new();

    /// <summary>Swarm-level statistics for current epoch.</summary>
    public SwarmStats CurrentStats { get; private set; } = new();

    public SwarmRewardAggregator(
        SharedMemoryLayer sharedMemory,
        ILogger<SwarmRewardAggregator> logger)
    {
        _sharedMemory = sharedMemory;
        _logger = logger;

        _aggregateTimer = new System.Timers.Timer(TimeSpan.FromMinutes(5).TotalMilliseconds);
        _aggregateTimer.Elapsed += (_, _) => AggregateAndPropagate();
        _aggregateTimer.AutoReset = true;
    }

    /// <summary>
    /// Submits per-step reward for a character.
    /// </summary>
    public void SubmitReward(string characterId, float combat, float survival, float wealth, float growth, float social)
    {
        lock (_lock)
        {
            if (!_buffers.TryGetValue(characterId, out var buffer))
            {
                buffer = new CharacterRewardBuffer { CharacterId = characterId };
                _buffers[characterId] = buffer;
            }

            buffer.Combat += combat;
            buffer.Survival += survival;
            buffer.Wealth += wealth;
            buffer.Growth += growth;
            buffer.Social += social;
            buffer.TickCount++;
        }
    }

    /// <summary>
    /// Aggregate all character rewards into swarm-level statistics and propagate via pheromones.
    /// </summary>
    public void AggregateAndPropagate()
    {
        List<CharacterRewardBuffer> snapshots;
        lock (_lock)
        {
            snapshots = _buffers.Values.Select(b => b with { }).ToList();
            foreach (var buf in _buffers.Values)
            {
                buf.Reset();
            }
        }

        if (snapshots.Count == 0) return;

        // Compute swarm-level statistics
        var stats = new SwarmStats
        {
            CharacterCount = snapshots.Count,
            TotalKills = (int)snapshots.Sum(s => s.Combat / 10),
            TotalDeaths = (int)snapshots.Sum(s => -s.Survival / 50),
            TotalItems = (int)snapshots.Sum(s => s.Wealth / 5),
            TotalZen = (int)snapshots.Sum(s => s.Wealth / 0.001f),
            AverageCombatScore = snapshots.Average(s => s.Combat),
            AverageSurvivalScore = snapshots.Average(s => s.Survival),
            AverageWealthScore = snapshots.Average(s => s.Wealth),
            AverageGrowthScore = snapshots.Average(s => s.Growth),
            AverageSocialScore = snapshots.Average(s => s.Social),
            BestCombatCharacter = snapshots.OrderByDescending(s => s.Combat).First().CharacterId,
            BestSurvivalCharacter = snapshots.OrderByDescending(s => s.Survival).First().CharacterId,
            BestWealthCharacter = snapshots.OrderByDescending(s => s.Wealth).First().CharacterId,
        };

        CurrentStats = stats;

        _logger.LogInformation(
            "[SwarmReward] Aggregated {Count} characters: combat={Combat:F1} survival={Surv:F1} wealth={Wealth:F1} growth={Grow:F1}",
            stats.CharacterCount,
            stats.AverageCombatScore,
            stats.AverageSurvivalScore,
            stats.AverageWealthScore,
            stats.AverageGrowthScore);

        // Propagate via SharedMemoryLayer: update worker profiles
        foreach (var snapshot in snapshots)
        {
            _sharedMemory.UpdateWorkerProfile(snapshot.CharacterId, "combat", snapshot.Combat);
            _sharedMemory.UpdateWorkerProfile(snapshot.CharacterId, "survival", snapshot.Survival);
            _sharedMemory.UpdateWorkerProfile(snapshot.CharacterId, "economy", snapshot.Wealth);
            _sharedMemory.UpdateWorkerProfile(snapshot.CharacterId, "growth", snapshot.Growth);
        }

        // Deposit high-density pheromones at best performers' locations
        // (approximated — in production, would use actual positions)
        var bestCombat = snapshots.OrderByDescending(s => s.Combat).First();
        _sharedMemory.DepositPheromone(128, 128, 0.5f, 0.8f);

        _logger.LogInformation("[SwarmReward] Social propagation complete. Best combat: {Char}", bestCombat.CharacterId);

        // Kanban REVIEW feedback: high-reward chars → DONE, low-reward → BLOCKED
        if (_kanban is not null)
        {
            var scores = snapshots.Select(s => s.Combat + s.Survival + s.Wealth + s.Growth + s.Social).ToList();
            var threshold = scores.Average() * 0.5f;
            for (int i = 0; i < snapshots.Count; i++)
            {
                if (scores[i] > threshold * 2)
                    _kanban.UpdateTaskState(snapshots[i].CharacterId, "DONE");
                else if (scores[i] < threshold)
                    _kanban.UpdateTaskState(snapshots[i].CharacterId, "BLOCKED");
            }
        }
    }

    /// <summary>Start periodic aggregation.</summary>
    public void Start() => _aggregateTimer.Start();

    /// <summary>
    /// 连接到 FuguKanbanBoard — REVIEW 列的 reward 反馈机制。
    /// 每次聚合后将高 reward 任务移入 DONE 列，低 reward 任务移入 BLOCKED 列。
    /// </summary>
    public void SetKanban(MUnique.OpenMU.AIPlayer.Decision.FuguKanbanBoard kanban)
    {
        _kanban = kanban;
    }

    public void Dispose()
    {
        _aggregateTimer.Stop();
        _aggregateTimer.Dispose();
    }
}

/// <summary>
/// Per-character reward buffer.
/// </summary>
public record CharacterRewardBuffer
{
    public string CharacterId { get; init; } = string.Empty;
    public float Combat { get; set; }
    public float Survival { get; set; }
    public float Wealth { get; set; }
    public float Growth { get; set; }
    public float Social { get; set; }
    public int TickCount { get; set; }

    public void Reset()
    {
        Combat = Survival = Wealth = Growth = Social = 0;
        TickCount = 0;
    }
}

/// <summary>
/// Swarm-level aggregated statistics.
/// </summary>
public class SwarmStats
{
    public int CharacterCount { get; set; }
    public int TotalKills { get; set; }
    public int TotalDeaths { get; set; }
    public int TotalItems { get; set; }
    public int TotalZen { get; set; }
    public float AverageCombatScore { get; set; }
    public float AverageSurvivalScore { get; set; }
    public float AverageWealthScore { get; set; }
    public float AverageGrowthScore { get; set; }
    public float AverageSocialScore { get; set; }
    public string BestCombatCharacter { get; set; } = string.Empty;
    public string BestSurvivalCharacter { get; set; } = string.Empty;
    public string BestWealthCharacter { get; set; } = string.Empty;
}
