// <copyright file="SwarmOrchestrator.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.Evolution;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.AIPlayer.Decision;
using MUnique.OpenMU.AIPlayer.Knowledge;

/// <summary>
/// F10: Fugu-style SwarmOrchestrator.
///
/// Global events (Boss spawn, invasion, resource discovery) are decomposed
/// into sub-tasks and assigned to AI characters based on their WorkerProfile
/// capability scores. Maps to Fugu-Ultra's Conductor framework.
///
/// Protocol:
///   1. Detect global event (from SharedMemoryLayer or external trigger)
///   2. Decompose into sub-tasks: [scout, firepower, heal, loot, ...]
///   3. Query WorkerProfile from KG/SharedMemory for best workers per capability
///   4. Assign tasks via Kanban board (TODO → IN PROG)
///   5. Monitor REVIEW column for task outcomes
///   6. Update WorkerProfiles based on task success/failure
///
/// This implements Fugu's "dynamic task decomposition + capability-matched assignment"
/// without central command — allocation emerges from SharedMemoryLayer profiles.
/// </summary>
public sealed class SwarmOrchestrator
{
    private readonly ILogger _logger;
    private readonly SharedMemoryLayer _sharedMemory;
    private readonly Dictionary<string, FuguKanbanBoard> _kanbans = new(); // workerId → board

    private readonly System.Timers.Timer _monitorTimer;
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Pre-defined sub-task templates for common global events.
    /// Each template has a required capability and reward weight.
    /// </summary>
    private static readonly List<SubTaskTemplate> SubTaskTemplates = new()
    {
        new("scout", "侦察", "scouting", 10f),
        new("firepower", "火力输出", "damage", 50f),
        new("healer", "治疗支援", "support", 30f),
        new("looter", "物资拾取", "economy", 20f),
        new("tank", "承伤前沿", "defense", 40f),
        new("explore", "区域探索", "exploration", 15f),
    };

    public SwarmOrchestrator(
        SharedMemoryLayer sharedMemory,
        ILogger<SwarmOrchestrator> logger)
    {
        _sharedMemory = sharedMemory;
        _logger = logger;

        _monitorTimer = new System.Timers.Timer(MonitorInterval.TotalMilliseconds);
        _monitorTimer.Elapsed += (_, _) => MonitorAndAssign();
        _monitorTimer.AutoReset = true;
    }

    /// <summary>
    /// Registers a worker's kanban board.
    /// </summary>
    public void RegisterWorker(string workerId, FuguKanbanBoard kanban)
    {
        _kanbans[workerId] = kanban;
    }

    /// <summary>
    /// Handles a global event by decomposing into sub-tasks and assigning to workers.
    /// </summary>
    public void HandleGlobalEvent(GlobalEvent globalEvent)
    {
        _logger.LogInformation("[SwarmOrch] Global event: {Type} at ({X},{Y}) map {Map}",
            globalEvent.EventType, globalEvent.X, globalEvent.Y, globalEvent.MapId);

        var subTasks = DecomposeEvent(globalEvent);

        foreach (var subTask in subTasks)
        {
            var bestWorkers = _sharedMemory.FindWorkersByCapability(
                subTask.RequiredCapability, minScore: 0.3f, topN: 3);

            if (bestWorkers.Count == 0)
            {
                _logger.LogWarning("[SwarmOrch] No workers for capability '{Cap}'", subTask.RequiredCapability);
                continue;
            }

            foreach (var profile in bestWorkers)
            {
                if (_kanbans.TryGetValue(profile.WorkerId, out var kanban))
                {
                    kanban.AddTask(new KanbanTask
                    {
                        Id = $"{globalEvent.EventType}_{subTask.SubType}_{profile.WorkerId}",
                        TaskType = subTask.SubType,
                        Description = subTask.Description,
                        Priority = subTask.RewardWeight,
                        TargetMapId = globalEvent.MapId,
                        TargetPosition = (globalEvent.X, globalEvent.Y),
                        AssignedWorkerId = profile.WorkerId,
                    });
                }
            }
        }
    }

    /// <summary>
    /// Decomposes a global event into sub-tasks.
    /// For a Boss spawn: scout (find exact location) + firepower (damage) + healer (support) + looter (cleanup)
    /// For an invasion: tank (defense) + firepower (damage) + explore (area coverage)
    /// </summary>
    private List<SubTaskAssignment> DecomposeEvent(GlobalEvent evt)
    {
        return evt.EventType switch
        {
            "boss_spawn" => new List<SubTaskAssignment>
            {
                new("firepower", "damage", 50f, $"击杀Boss on map {evt.MapId}"),
                new("healer", "support", 30f, $"支援Boss战 on map {evt.MapId}"),
                new("looter", "economy", 25f, $"拾取Boss掉落 on map {evt.MapId}"),
                new("scout", "scouting", 10f, $"侦察Boss位置 on map {evt.MapId}"),
            },
            "invasion" => new List<SubTaskAssignment>
            {
                new("tank", "defense", 40f, $"防线防守 on map {evt.MapId}"),
                new("firepower", "damage", 40f, $"消灭入侵怪物 on map {evt.MapId}"),
                new("explore", "exploration", 15f, $"巡视区域 on map {evt.MapId}"),
            },
            _ => new List<SubTaskAssignment>
            {
                new("explore", "exploration", 10f, $"响应事件: {evt.EventType}"),
            },
        };
    }

    /// <summary>
    /// Periodic monitoring: pull tasks from TODO for each worker, process REVIEW.
    /// </summary>
    private void MonitorAndAssign()
    {
        foreach (var (workerId, kanban) in _kanbans)
        {
            // Auto-pull next task if idle
            var current = kanban.PullNextTask();
            if (current is not null)
            {
                _logger.LogDebug("[SwarmOrch] Worker {Worker} → {Task}", workerId, current.Id);
            }

            // Process REVIEW → DONE
            var completed = kanban.ProcessReview();
            foreach (var task in completed)
            {
                // Update worker profile based on task outcome
                if (task.Reward > 0)
                {
                    _sharedMemory.UpdateWorkerProfile(
                        workerId, task.TaskType, task.Reward);
                }
            }
        }
    }

    /// <summary>
    /// Start swarm monitoring.
    /// </summary>
    public void Start() => _monitorTimer.Start();

    /// <summary>
    /// Stop and clean up.
    /// </summary>
    public void Dispose()
    {
        _monitorTimer.Stop();
        _monitorTimer.Dispose();
    }
}

/// <summary>
/// A global event detected by the swarm (Boss spawn, invasion, etc.).
/// </summary>
public record GlobalEvent
{
    public string EventType { get; init; } = string.Empty;
    public int MapId { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Severity { get; init; } = 1;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Pre-defined sub-task template.
/// </summary>
public record SubTaskTemplate(
    string SubType,
    string Description,
    string RequiredCapability,
    float RewardWeight);

/// <summary>
/// A concrete sub-task assignment.
/// </summary>
public record SubTaskAssignment(
    string SubType,
    string RequiredCapability,
    float RewardWeight,
    string Description);
