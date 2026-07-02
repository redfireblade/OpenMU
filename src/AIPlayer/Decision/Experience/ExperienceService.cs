// <copyright file="ExperienceService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision.Experience;

using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>
/// 经验服务 — 全局单例（每个 AiPlayerManager 一个实例）。
///
/// 职责：
/// 1. 管理所有 AI 角色的行为日志（开始→结束模式）
/// 2. 将个人日志持久化到本地文件
/// 3. 从群体经验库同步规则和策略（拉）
/// 4. 将个人经验提交到群体聚合目录（推）
///
/// 经验记录点：
///   - 狩猎完成/失败
///   - 副本入场/退出
///   - 合成成功/失败
///   - 交易完成
///   - PK 结束
///   - 跨地图移动
/// </summary>
public sealed class ExperienceService
{
    private readonly string _dataDir;
    private readonly ILogger _logger;

    // 正在进行的行为 <roleName, <logId, ActionLog>>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, ActionLog>> _activeBehaviors = new();
    private readonly ConcurrentQueue<ActionLog> _pendingLogs = new();

    // 缓存群体规则，减少磁盘读取
    private List<RuleDef>? _cachedExperienceRules;
    private DateTime _lastRuleSync = DateTime.MinValue;
    private static readonly TimeSpan RuleCacheTtl = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ExperienceService(string baseDir, ILogger logger)
    {
        this._dataDir = Path.Combine(baseDir, "aiplayer_data");
        this._logger = logger;
        Directory.CreateDirectory(this._dataDir);
        Directory.CreateDirectory(Path.Combine(this._dataDir, "experience_db", "aggregated"));
    }

    /// <summary>
    /// 开始记录一个行为。行为结束时调用 <see cref="EndBehavior"/>。
    /// </summary>
    /// <returns>行为日志ID，传给 EndBehavior 使用。</returns>
    public Guid BeginBehavior(ActionLog log)
    {
        var behaviors = this._activeBehaviors.GetOrAdd(log.AiRoleName, _ => new());
        behaviors[log.LogId] = log;
        return log.LogId;
    }

    /// <summary>
    /// 结束一个行为，补充结果并提交到缓冲区。
    /// 自动从群体库拉取最新规则（缓存过期时）。
    /// </summary>
    /// <returns>本次行为的完整日志。</returns>
    public ActionLog? EndBehavior(string roleName, Guid logId, BehaviorResult result,
        Action<ActionLog>? updateFields = null)
    {
        if (!this._activeBehaviors.TryGetValue(roleName, out var behaviors))
        {
            return null;
        }

        if (!behaviors.TryRemove(logId, out var log))
        {
            return null;
        }

        log = log with
        {
            Result = result,
            EndTime = DateTime.UtcNow,
        };

        // 调用方补充具体结果字段
        updateFields?.Invoke(log);

        this._pendingLogs.Enqueue(log);
        this.TryFlush();

        return log;
    }

    /// <summary>
    /// 快速记录一个已完成的行为（不需要开始/结束两个步骤）。
    /// </summary>
    public void RecordBehavior(ActionLog log)
    {
        this._pendingLogs.Enqueue(log with { EndTime = DateTime.UtcNow });
        this.TryFlush();
    }

    /// <summary>
    /// 检查是否有正在进行的行为。
    /// </summary>
    public bool IsBehaviorActive(string roleName, BehaviorType type) =>
        this._activeBehaviors.TryGetValue(roleName, out var behaviors) &&
        behaviors.Values.Any(l => l.BehaviorType == type);

    /// <summary>
    /// 从群体经验库拉取规则，供 RuleEngine 加载。
    /// 缓存 TTL=5分钟。
    /// </summary>
    public List<RuleDef> PullRules()
    {
        if (this._cachedExperienceRules is not null &&
            DateTime.UtcNow - this._lastRuleSync < RuleCacheTtl)
        {
            return this._cachedExperienceRules;
        }

        try
        {
            var filePath = Path.Combine(this._dataDir, "scripts_rules", "rules_experience.json");
            if (!File.Exists(filePath))
            {
                this._cachedExperienceRules = new();
                this._lastRuleSync = DateTime.UtcNow;
                return this._cachedExperienceRules;
            }

            var json = File.ReadAllText(filePath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            this._cachedExperienceRules = JsonSerializer.Deserialize<List<RuleDef>>(json, options) ?? new();
            this._lastRuleSync = DateTime.UtcNow;

            if (this._cachedExperienceRules.Count > 0)
            {
                this._logger.LogDebug("[ExpSvc] 从群体库拉取 {Count} 条经验规则", this._cachedExperienceRules.Count);
            }

            return this._cachedExperienceRules;
        }
        catch
        {
            return this._cachedExperienceRules ?? new();
        }
    }

    /// <summary>
    /// 将等待中的日志刷到磁盘。
    /// </summary>
    public void Flush()
    {
        this.FlushCore();
    }

    private void TryFlush()
    {
        if (this._pendingLogs.Count >= 50)
        {
            this.FlushCore();
        }
    }

    private void FlushCore()
    {
        var batch = new List<ActionLog>();
        while (this._pendingLogs.TryDequeue(out var log))
        {
            batch.Add(log);
        }

        if (batch.Count == 0) return;

        // 按角色分组写入各自的日志文件
        foreach (var group in batch.GroupBy(l => l.AiRoleName))
        {
            try
            {
                var dir = Path.Combine(this._dataDir, group.Key);
                Directory.CreateDirectory(dir);
                var filePath = Path.Combine(dir, "action_logs.jsonl");
                var lines = group.Select(l => JsonSerializer.Serialize(l, JsonOptions));
                File.AppendAllLines(filePath, lines);
            }
            catch (Exception ex)
            {
                this._logger.LogWarning(ex, "[ExpSvc] 写入角色 {Role} 日志失败", group.Key);
            }
        }

        // 同时推送到群体聚合目录
        this.PushToAggregationDir(batch);
    }

    /// <summary>
    /// 将日志推送到群体聚合目录（供 ExperienceAggregator 消费）。
    /// </summary>
    private void PushToAggregationDir(List<ActionLog> logs)
    {
        try
        {
            var dir = Path.Combine(this._dataDir, "experience_db", "raw");
            Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, $"logs_{DateTime.UtcNow:yyyyMMdd}.jsonl");
            var lines = logs.Select(l => JsonSerializer.Serialize(l, JsonOptions));
            File.AppendAllLines(filePath, lines);
        }
        catch
        {
            // 静默失败
        }
    }

    /// <summary>
    /// 读取所有角色的历史日志（供聚合器使用）。
    /// </summary>
    public async ValueTask<List<ActionLog>> ReadAllLogsAsync()
    {
        var all = new List<ActionLog>();
        if (!Directory.Exists(this._dataDir)) return all;

        foreach (var roleDir in Directory.GetDirectories(this._dataDir))
        {
            var name = Path.GetFileName(roleDir);
            if (name == "experience_db" || name == "scripts_rules") continue;

            var filePath = Path.Combine(roleDir, "action_logs.jsonl");
            if (!File.Exists(filePath)) continue;

            try
            {
                var lines = await File.ReadAllLinesAsync(filePath).ConfigureAwait(false);
                all.AddRange(lines
                    .Select(l => { try { return JsonSerializer.Deserialize<ActionLog>(l, JsonOptions); } catch { return null; } })
                    .Where(l => l is not null)
                    .Select(l => l!));
            }
            catch { }
        }

        return all;
    }
}
