// <copyright file="AITaskManager.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

/// <summary>
/// AI 后台任务管理器 — 负责启动、跟踪、取消后台异步操作。
///
/// 设计目标：
///   1. 心跳线程快速发射任务，不等待完成
///   2. 每个任务在自己的 Task 上下文中独立运行
///   3. 自动超时取消（CancellationTokenSource + Timeout）
///   4. 轮询获取已完成任务的结果
///   5. 清理残留/泄漏的后台任务
///
/// 线程安全：所有公开方法均为线程安全。
/// </summary>
public sealed class AITaskManager : IDisposable
{
    private readonly ConcurrentDictionary<string, AITaskEntry> _tasks = new();
    private readonly ILogger _logger;
    private bool _disposed;

    public AITaskManager(ILogger logger)
    {
        this._logger = logger;
    }

    /// <summary>当前运行中的后台任务数。</summary>
    public int ActiveTaskCount => this._tasks.Values.Count(t => t.Status == TaskStatus.Running);

    /// <summary>当前注册的任务总数（含已完成尚未轮询的）。</summary>
    public int TotalTaskCount => this._tasks.Count;

    /// <summary>
    /// 启动一个后台任务，不阻塞调用线程。
    /// </summary>
    /// <param name="taskType">任务类型标识（如 "skill_auto_equip"、"mission_quest"）。</param>
    /// <param name="action">要执行的操作，接收 CancellationToken。</param>
    /// <param name="timeout">超时时间，到达后自动取消任务。</param>
    /// <returns>任务唯一 ID，可用于 HasActiveTask。</returns>
    public string RunTask(string taskType, Func<CancellationToken, ValueTask> action, TimeSpan timeout)
    {
        var id = $"{taskType}_{Guid.NewGuid():N}";
        var cts = new CancellationTokenSource(timeout);
        var entry = new AITaskEntry
        {
            Id = id,
            TaskType = taskType,
            StartedAt = DateTime.UtcNow,
            Timeout = timeout,
            Status = TaskStatus.Running,
            CTS = cts,
        };

        this._tasks[id] = entry;

        _ = Task.Run(async () =>
        {
            try
            {
                if (this._disposed)
                {
                    entry.Status = TaskStatus.Canceled;
                    return;
                }

                await action(cts.Token).ConfigureAwait(false);

                if (this._tasks.TryGetValue(id, out var t) && t.Status == TaskStatus.Running)
                    t.Status = TaskStatus.RanToCompletion;
            }
            catch (OperationCanceledException)
            {
                if (this._tasks.TryGetValue(id, out var t))
                    t.Status = TaskStatus.Canceled;
            }
            catch (Exception ex)
            {
                this._logger.LogWarning(ex, "[AITask] {Id} 失败: {Msg}", id, ex.Message);
                if (this._tasks.TryGetValue(id, out var t))
                    t.Status = TaskStatus.Faulted;
            }
        });

        this._logger.LogDebug("[AITask] 发射: {Id} (type={Type}, timeout={Timeout}s)", id, taskType, timeout.TotalSeconds);
        return id;
    }

    /// <summary>
    /// 检查指定类型是否有活跃（运行中）的任务。
    /// </summary>
    /// <param name="taskType">任务类型前缀，如 "mission_quest_1"。</param>
    public bool HasActiveTask(string taskType)
    {
        return this._tasks.Values.Any(t =>
            t.TaskType == taskType && t.Status == TaskStatus.Running);
    }

    /// <summary>
    /// 轮询并返回所有已完成的任务结果。从字典中移除后返回。
    /// </summary>
    public List<AITaskResult> PollResults()
    {
        var results = new List<AITaskResult>();
        var toRemove = new List<string>();

        foreach (var kvp in this._tasks)
        {
            var status = kvp.Value.Status;
            if (status is TaskStatus.RanToCompletion or TaskStatus.Faulted or TaskStatus.Canceled)
            {
                results.Add(new AITaskResult
                {
                    Id = kvp.Key,
                    TaskType = kvp.Value.TaskType,
                    Status = status,
                    StartedAt = kvp.Value.StartedAt,
                    Duration = DateTime.UtcNow - kvp.Value.StartedAt,
                });
                toRemove.Add(kvp.Key);
            }
        }

        foreach (var id in toRemove)
        {
            if (this._tasks.TryRemove(id, out var entry))
            {
                entry.CTS?.Dispose();
            }
        }

        return results;
    }

    /// <summary>
    /// 清理超时或运行时间过长的残留任务。
    /// 超过 maxAge 或 status 为 Running 但 timeout 已过的任务将被取消并移除。
    /// </summary>
    /// <param name="maxAge">最长允许运行时间（默认 5 分钟）。</param>
    /// <returns>清理的任务数量。</returns>
    public int CleanupStaleTasks(TimeSpan? maxAge = null)
    {
        maxAge ??= TimeSpan.FromMinutes(5);
        var now = DateTime.UtcNow;
        var cleaned = 0;

        foreach (var kvp in this._tasks)
        {
            var entry = kvp.Value;
            var age = now - entry.StartedAt;

            // 超时后的运行中任务
            if (entry.Status == TaskStatus.Running && age > maxAge.Value)
            {
                entry.CTS?.Cancel();
                entry.Status = TaskStatus.Canceled;
                this._logger.LogWarning("[AITask] 清理超时任务: {Id} (age={Age:F1}s)", kvp.Key, age.TotalSeconds);
                cleaned++;
            }
        }

        // 清理已取消/完成的残留
        var completed = this.PollResults();
        cleaned += completed.Count;

        return cleaned;
    }

    public void Dispose()
    {
        if (this._disposed) return;
        this._disposed = true;

        foreach (var kvp in this._tasks)
        {
            kvp.Value.CTS?.Cancel();
            kvp.Value.CTS?.Dispose();
        }

        this._tasks.Clear();
    }
}

/// <summary>后台任务条目。</summary>
internal sealed class AITaskEntry
{
    public string Id { get; set; } = string.Empty;
    public string TaskType { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public TimeSpan Timeout { get; set; }
    public TaskStatus Status { get; set; }
    public CancellationTokenSource? CTS { get; set; }
}

/// <summary>后台任务轮询结果。</summary>
public record AITaskResult
{
    public string Id { get; init; } = string.Empty;
    public string TaskType { get; init; } = string.Empty;
    public TaskStatus Status { get; init; }
    public DateTime StartedAt { get; init; }
    public TimeSpan Duration { get; init; }
}
