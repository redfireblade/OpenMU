// <copyright file="FuguKanbanBoard.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using Microsoft.Extensions.Logging;

/// <summary>
/// Fugu-style Kanban board for AI task management.
///
/// Five columns (Fugu mapping):
///   TODO     — task pool, populated by SwarmOrchestrator on global events
///   IN PROG  — currently executing task (one per AI)
///   REVIEW   — completed tasks awaiting reward evaluation (Fugu's reward feedback)
///   DONE     — completed + reward-processed, archived for statistics
///   BLOCKED  — tasks that cannot proceed (level too low, map unreachable, etc.)
///
/// REVIEW column is the key Fugu mechanism:
///   Each completed task enters REVIEW, SoftRouter evaluates its reward,
///   high-reward strategies get reinforced, low-reward ones get filtered out.
/// </summary>
public sealed class FuguKanbanBoard
{
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly int _maxDoneTasks = 500;

    // ═══ Columns ═══
    private readonly List<KanbanTask> _todo = new();
    private KanbanTask? _inProgress;
    private readonly List<KanbanTask> _review = new();
    private readonly Queue<KanbanTask> _done = new();
    private readonly List<KanbanTask> _blocked = new();

    /// <summary>Overall mission reward accumulator.</summary>
    public float TotalReward { get; private set; }

    /// <summary>Number of tasks completed in current mission.</summary>
    public int TasksCompleted { get; private set; }

    public FuguKanbanBoard(ILogger<FuguKanbanBoard> logger)
    {
        _logger = logger;
    }

    // ═══ Column queries ═══

    public IReadOnlyList<KanbanTask> Todo { get { lock (_lock) return _todo.ToList(); } }
    public KanbanTask? InProgress { get { lock (_lock) return _inProgress; } }
    public IReadOnlyList<KanbanTask> Review { get { lock (_lock) return _review.ToList(); } }
    public IReadOnlyList<KanbanTask> Done { get { lock (_lock) return _done.ToList(); } }
    public IReadOnlyList<KanbanTask> Blocked { get { lock (_lock) return _blocked.ToList(); } }

    // ═══ Operations ═══

    /// <summary>
    /// Adds a task to the TODO column.
    /// </summary>
    public void AddTask(KanbanTask task)
    {
        lock (_lock)
        {
            task.Status = KanbanStatus.Todo;
            _todo.Add(task);
            _logger.LogDebug("[Kanban] TODO ← {Task} (type={Type})", task.Id, task.TaskType);
        }
    }

    /// <summary>
    /// Moves the best-priority TODO task to IN PROGRESS.
    /// Priority = reward estimate × pheromone intensity at task location.
    /// </summary>
    public KanbanTask? PullNextTask()
    {
        lock (_lock)
        {
            if (_inProgress is not null) return _inProgress;
            if (_todo.Count == 0) return null;

            var best = _todo
                .OrderByDescending(t => t.Priority * t.PheromoneIntensity)
                .First();

            _todo.Remove(best);
            best.Status = KanbanStatus.InProgress;
            best.StartedAt = DateTime.UtcNow;
            _inProgress = best;

            _logger.LogInformation("[Kanban] IN PROG ← {Task} (prio={Prio:F2})", best.Id, best.Priority);
            return best;
        }
    }

    /// <summary>
    /// Moves IN PROGRESS to REVIEW (completion, reward pending).
    /// </summary>
    public void CompleteTask(float reward)
    {
        lock (_lock)
        {
            if (_inProgress is null) return;
            _inProgress.Status = KanbanStatus.Review;
            _inProgress.Reward = reward;
            _inProgress.CompletedAt = DateTime.UtcNow;
            _review.Add(_inProgress);
            _logger.LogInformation("[Kanban] REVIEW ← {Task} (reward={Reward:F2})", _inProgress.Id, reward);
            _inProgress = null;
        }
    }

    /// <summary>
    /// Processes the REVIEW column: evaluates rewards and moves to DONE.
    /// This is where SoftRouter reinforcement happens — high-reward task strategies
    /// get their weights increased, low-reward ones get filtered.
    /// </summary>
    public List<KanbanTask> ProcessReview()
    {
        var processed = new List<KanbanTask>();
        lock (_lock)
        {
            foreach (var task in _review.ToList())
            {
                task.Status = KanbanStatus.Done;
                _review.Remove(task);
                _done.Enqueue(task);
                TotalReward += task.Reward;
                TasksCompleted++;

                if (_done.Count > _maxDoneTasks)
                {
                    _done.Dequeue();
                }

                processed.Add(task);
            }
        }

        return processed;
    }

    /// <summary>
    /// Blocks a task that cannot be completed.
    /// </summary>
    public void BlockCurrentTask(string reason)
    {
        lock (_lock)
        {
            if (_inProgress is null) return;
            _inProgress.Status = KanbanStatus.Blocked;
            _inProgress.BlockReason = reason;
            _blocked.Add(_inProgress);
            _logger.LogWarning("[Kanban] BLOCKED ← {Task}: {Reason}", _inProgress.Id, reason);
            _inProgress = null;
        }
    }

    /// <summary>
    /// Unblocks a task (e.g., after leveling up).
    /// </summary>
    public void UnblockTask(string taskId)
    {
        lock (_lock)
        {
            var task = _blocked.FirstOrDefault(t => t.Id == taskId);
            if (task is null) return;
            _blocked.Remove(task);
            task.Status = KanbanStatus.Todo;
            _todo.Add(task);
            _logger.LogInformation("[Kanban] TODO ← {Task} (unblocked)", task.Id);
        }
    }

    /// <summary>
    /// 更新任务状态（供 SwarmRewardAggregator/Kanban 外部调用）。
    /// REVIEW 列中高 reward 任务 → DONE，低 reward → BLOCKED。
    /// </summary>
    public void UpdateTaskState(string characterId, string newStatus)
    {
        lock (_lock)
        {
            var task = _review.FirstOrDefault(t => t.AssignedWorkerId == characterId);
            if (task is null) return;
            _review.Remove(task);
            task.Status = newStatus switch
            {
                "DONE" => KanbanStatus.Done,
                "BLOCKED" => KanbanStatus.Blocked,
                _ => KanbanStatus.Todo,
            };
            if (task.Status == KanbanStatus.Done) _done.Enqueue(task);
            else if (task.Status == KanbanStatus.Blocked) _blocked.Add(task);
            else _todo.Add(task);
        }
    }

    /// <summary>
    /// Gets board statistics for diagnostics.
    /// </summary>
    public KanbanStats GetStats()
    {
        lock (_lock)
        {
            return new KanbanStats
            {
                TodoCount = _todo.Count,
                InProgressCount = _inProgress is not null ? 1 : 0,
                ReviewCount = _review.Count,
                DoneCount = _done.Count,
                BlockedCount = _blocked.Count,
                TotalReward = TotalReward,
                TasksCompleted = TasksCompleted,
            };
        }
    }
}

/// <summary>
/// A task on the Kanban board.
/// </summary>
public class KanbanTask
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string TaskType { get; init; } = string.Empty; // hunt, restock, quest, explore, sos
    public string Description { get; init; } = string.Empty;
    public float Priority { get; set; } = 1.0f;
    public float PheromoneIntensity { get; set; }
    public KanbanStatus Status { get; set; } = KanbanStatus.Todo;
    public float Reward { get; set; }
    public string? BlockReason { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int? TargetMapId { get; set; }
    public (int X, int Y)? TargetPosition { get; set; }
    public string? AssignedWorkerId { get; set; }
}

public enum KanbanStatus
{
    Todo,
    InProgress,
    Review,
    Done,
    Blocked,
}

public record KanbanStats
{
    public int TodoCount { get; init; }
    public int InProgressCount { get; init; }
    public int ReviewCount { get; init; }
    public int DoneCount { get; init; }
    public int BlockedCount { get; init; }
    public float TotalReward { get; init; }
    public int TasksCompleted { get; init; }
}
