// <copyright file="WorkerAvailabilityService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.Evolution;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

/// <summary>
/// Tracks AI worker availability for Fugu-style task routing.
///
/// Fugu paper: "not addressed" — the original system assumes all workers are accessible.
/// OAPS extension: monitors AI characters' alive/connected state and removes dead or
/// disconnected workers from the task pool, routing tasks to available alternatives.
///
/// Also implements dynamic aggregator selection: different task types preferentially
/// use different AI character classes based on their WorkerProfile scores.
/// </summary>
public sealed class WorkerAvailabilityService
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, WorkerStatus> _workers = new();

    public WorkerAvailabilityService(ILogger<WorkerAvailabilityService> logger)
    {
        _logger = logger;
    }

    /// <summary>Register a worker as available.</summary>
    public void RegisterWorker(string workerId, string characterClass = "unknown")
    {
        _workers[workerId] = new WorkerStatus
        {
            WorkerId = workerId,
            IsAlive = true,
            IsConnected = true,
            CharacterClass = characterClass,
            LastSeen = DateTime.UtcNow,
        };
    }

    /// <summary>Mark a worker as dead (still connected, waiting for respawn).</summary>
    public void MarkDead(string workerId)
    {
        if (_workers.TryGetValue(workerId, out var status))
        {
            status.IsAlive = false;
            status.LastSeen = DateTime.UtcNow;
            _logger.LogDebug("[WorkerAvail] {Worker} marked DEAD", workerId);
        }
    }

    /// <summary>Mark a worker as revived.</summary>
    public void MarkAlive(string workerId)
    {
        if (_workers.TryGetValue(workerId, out var status))
        {
            status.IsAlive = true;
            status.LastSeen = DateTime.UtcNow;
        }
    }

    /// <summary>Mark a worker as disconnected.</summary>
    public void MarkDisconnected(string workerId)
    {
        if (_workers.TryGetValue(workerId, out var status))
        {
            status.IsConnected = false;
            status.LastSeen = DateTime.UtcNow;
            _logger.LogWarning("[WorkerAvail] {Worker} DISCONNECTED", workerId);
        }
    }

    /// <summary>Mark a worker as reconnected.</summary>
    public void MarkConnected(string workerId)
    {
        if (_workers.TryGetValue(workerId, out var status))
        {
            status.IsConnected = true;
            status.LastSeen = DateTime.UtcNow;
        }
    }

    /// <summary>Remove a worker entirely.</summary>
    public void RemoveWorker(string workerId)
    {
        _workers.TryRemove(workerId, out _);
    }

    /// <summary>Check if a worker can receive tasks.</summary>
    public bool IsAvailable(string workerId)
    {
        return _workers.TryGetValue(workerId, out var s) && s.IsAlive && s.IsConnected;
    }

    /// <summary>
    /// Fugu-style dynamic aggregator: selects the best available worker
    /// for a given task type based on character class affinity.
    /// </summary>
    public string? SelectAggregator(string taskType)
    {
        var available = _workers.Values
            .Where(w => w.IsAlive && w.IsConnected)
            .ToList();

        if (available.Count == 0) return null;

        // Dynamic aggregator: different tasks → different worker class preferences
        return taskType switch
        {
            "firepower" or "combat" => available
                .OrderByDescending(w => ClassCombatScore(w.CharacterClass))
                .ThenBy(w => w.LastSeen)
                .First().WorkerId,

            "healer" or "support" => available
                .OrderByDescending(w => ClassSupportScore(w.CharacterClass))
                .First().WorkerId,

            "scout" or "explore" => available
                .OrderByDescending(w => ClassExplorationScore(w.CharacterClass))
                .First().WorkerId,

            "looter" or "economy" => available
                .OrderBy(w => w.LastSeen)
                .First().WorkerId, // Closest available

            _ => available[Random.Shared.Next(available.Count)].WorkerId,
        };
    }

    /// <summary>Gets all available workers.</summary>
    public List<string> GetAvailableWorkers()
    {
        return _workers.Values
            .Where(w => w.IsAlive && w.IsConnected)
            .Select(w => w.WorkerId)
            .ToList();
    }

    /// <summary>Total worker count.</summary>
    public int TotalCount => _workers.Count;

    /// <summary>Available worker count.</summary>
    public int AvailableCount => _workers.Values.Count(w => w.IsAlive && w.IsConnected);

    // ═══ Private ═══

    private static float ClassCombatScore(string cls) => cls switch
    {
        "DarkKnight" or "BladeKnight" or "BladeMaster" => 1.0f,
        "MagicGladiator" or "DuelMaster" => 0.9f,
        "DarkLord" or "LordEmperor" => 0.85f,
        "RageFighter" or "FistMaster" => 0.8f,
        "DarkWizard" or "SoulMaster" or "GrandMaster" => 0.6f,
        _ => 0.5f,
    };

    private static float ClassSupportScore(string cls) => cls switch
    {
        "FairyElf" or "MuseElf" or "HighElf" => 1.0f,
        "DarkWizard" or "SoulMaster" or "GrandMaster" => 0.7f,
        "Summoner" or "BloodySummoner" or "DimensionMaster" => 0.6f,
        _ => 0.3f,
    };

    private static float ClassExplorationScore(string cls) => cls switch
    {
        "FairyElf" or "MuseElf" => 1.0f,
        "DarkWizard" or "SoulMaster" => 0.7f,
        "RageFighter" => 0.7f,
        _ => 0.5f,
    };

    private sealed class WorkerStatus
    {
        public string WorkerId { get; init; } = string.Empty;
        public bool IsAlive { get; set; }
        public bool IsConnected { get; set; }
        public string CharacterClass { get; set; } = "unknown";
        public DateTime LastSeen { get; set; }
    }
}
