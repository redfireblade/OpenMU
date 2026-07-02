// <copyright file="ExperienceAggregator.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision.Experience;

using System.Globalization;
using System.IO;
using System.Text.Json;

/// <summary>
/// 经验聚合器 — 收集所有AI角色的行为日志，
/// 按维度聚合为 AggregatedExperience，产出 RuleCandidate。
/// 经验=未成熟的规则，聚合的终点就是规则候选。
/// </summary>
public sealed class ExperienceAggregator
{
    private readonly string _dataDir;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private const int MinSamplesForRule = 10;      // 至少10条才能产生规则候选
    private const int MinSamplesForHighConfidence = 30; // 30条+可到0.9

    public ExperienceAggregator(string baseDir)
    {
        this._dataDir = Path.Combine(baseDir, "aiplayer_data", "experience_db");
        Directory.CreateDirectory(this._dataDir);
        Directory.CreateDirectory(Path.Combine(this._dataDir, "aggregated"));
    }

    /// <summary>
    /// 聚合所有 AI 角色的日志，生成聚合经验和规则候选。
    /// 返回生成的规则候选列表（只返回新候选，去重）。
    /// </summary>
    public async ValueTask<(List<AggregatedExperience> Aggregated, List<RuleCandidate> Candidates)> AggregateAsync(
        IEnumerable<ActionLog> allLogs)
    {
        var logs = allLogs.Where(l => l.Result == BehaviorResult.Completed || l.Result == BehaviorResult.Failed).ToList();
        if (logs.Count < MinSamplesForRule)
        {
            return (new(), new());
        }

        // 1. 按维度分组
        var groups = logs
            .GroupBy(l =>
            {
                var type = l.BehaviorType.ToString();
                return $"{type}:{l.MapId}:{l.MonsterNumber}";
            })
            .Where(g => g.Count() >= MinSamplesForRule)
            .ToList();

        var aggregated = new List<AggregatedExperience>();
        var candidates = new List<RuleCandidate>();

        foreach (var group in groups)
        {
            var count = group.Count();
            var successes = group.Count(l => l.Result == BehaviorResult.Completed);
            var successRate = (float)successes / count;

            var avgLevel = (float)group.Average(l => l.AiLevel);
            var avgGold = (float)group.Average(l => l.GoldEarned);
            var avgDuration = (float)group.Average(l => l.DurationSeconds);
            var avgKills = (float)group.Average(l => l.MonstersKilled);
            var totalDeaths = group.Sum(l => l.DeathCount);

            var first = group.First();
            var parts = group.Key.Split(':');

            var agg = new AggregatedExperience
            {
                DimensionKey = group.Key,
                EventType = parts.Length > 0 ? parts[0] : "unknown",
                MapId = first.MapId ?? 0,
                MonsterNumber = first.MonsterNumber ?? 0,
                MonsterName = string.Empty,
                MonsterLevel = first.MonsterLevel ?? 0,
                TotalAttempts = count,
                SuccessCount = successes,
                FailCount = count - successes,
                TotalDeaths = totalDeaths,
                AvgDurationSeconds = avgDuration,
                AvgGoldEarned = avgGold,
                AvgMonstersKilled = avgKills,
                MinPlayerLevel = group.Min(l => l.AiLevel),
                MaxPlayerLevel = group.Max(l => l.AiLevel),
                AvgPlayerLevel = avgLevel,
                WorthScore = CalculateWorthScore(successRate, avgGold, avgKills, totalDeaths),
                LastUpdated = DateTime.UtcNow,
            };
            aggregated.Add(agg);

            // 2. 生成规则候选
            var candidate = this.CreateCandidate(agg, count, successRate, avgLevel);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        // 3. 持久化聚合结果
        await this.SaveAggregatedAsync(aggregated).ConfigureAwait(false);

        // 4. 持久化规则候选
        await this.SaveCandidatesAsync(candidates).ConfigureAwait(false);

        return (aggregated, candidates);
    }

    /// <summary>
    /// 计算综合性价比评分 0~100。
    /// </summary>
    private static float CalculateWorthScore(float successRate, float avgGold, float avgKills, int totalDeaths)
    {
        var score = 0f;

        // 成功率权重 40%
        score += successRate * 40f;

        // 收益权重 30%（10000金币=10分封顶）
        score += Math.Min(avgGold / 10000f, 1f) * 30f;

        // 击杀权重 15%（10只=15分封顶）
        score += Math.Min(avgKills / 10f, 1f) * 15f;

        // 死亡惩罚 15%（0死=15分，每死一次扣5分）
        var deathRate = totalDeaths > 0 ? (float)totalDeaths / Math.Max(1, totalDeaths) : 0;
        score += Math.Max(0f, 1f - deathRate * 5f) * 15f;

        return Math.Clamp(score, 0, 100);
    }

    /// <summary>
    /// 从聚合经验生成规则候选。
    /// 只有"有用"的经验才会生成候选：综合评分>30 或 成功率>50%。
    /// </summary>
    private RuleCandidate? CreateCandidate(AggregatedExperience agg, int count, float successRate, float avgLevel)
    {
        if (agg.WorthScore < 30f && successRate < 0.5f)
        {
            return null; // 不值得生成规则
        }

        // 置信度: 样本量 + 成功率的综合
        var sampleConfidence = Math.Min((float)count / MinSamplesForHighConfidence, 1f) * 0.6f;
        var successConfidence = successRate * 0.4f;
        var confidence = Math.Min(sampleConfidence + successConfidence, 1f);

        var ruleId = $"exp_{agg.EventType.ToLower(CultureInfo.InvariantCulture)}_{agg.MapId}_{agg.MonsterNumber}";

        // 条件: 事件类型 + 等级范围
        var condition = agg.EventType switch
        {
            "GoldenDragonInvasion" or "RedDragonInvasion" => "golden_monster_alert",
            "BloodCastle" or "DevilSquare" or "ChaosCastle" => "event_open_and_ready",
            _ => "always",
        };

        var minLevel = agg.MinPlayerLevel;
        var category = agg.EventType switch
        {
            "GoldenDragonInvasion" or "RedDragonInvasion" => "hunt",
            "BloodCastle" or "DevilSquare" or "ChaosCastle" => "event",
            _ => "survival",
        };

        var desc = agg.MonsterNumber > 0
            ? $"[经验] 地图#{agg.MapId}怪物#{agg.MonsterNumber} 成功率{successRate:P0} 评分{agg.WorthScore:F0}"
            : $"[经验] 地图#{agg.MapId} 成功率{successRate:P0} 评分{agg.WorthScore:F0}";

        return new RuleCandidate
        {
            RuleId = ruleId,
            Condition = condition,
            ScriptId = "survival",
            SuggestedPriority = 35,
            MinLevel = minLevel,
            MaxLevel = 0,
            Parameters = agg.MonsterNumber > 0
                ? new() { { "targetMonster", agg.MonsterNumber.ToString() }, { "mapId", agg.MapId.ToString() } }
                : new() { { "mapId", agg.MapId.ToString() } },
            Category = category,
            Description = desc,
            Confidence = confidence,
            SampleSize = count,
            DimensionKey = agg.DimensionKey,
            EvidenceSummary = $"{count}次样本, 成功率{successRate:P0}, 平均等级{avgLevel:F0}, 评分{agg.WorthScore:F0}",
            PromotedToRule = false,
        };
    }

    private async ValueTask SaveAggregatedAsync(List<AggregatedExperience> list)
    {
        try
        {
            var filePath = Path.Combine(this._dataDir, "aggregated", "all.json");
            var json = JsonSerializer.Serialize(list, JsonOptions);
            await File.WriteAllTextAsync(filePath, json).ConfigureAwait(false);
        }
        catch
        {
            // 静默失败，聚合数据非关键
        }
    }

    private async ValueTask SaveCandidatesAsync(List<RuleCandidate> candidates)
    {
        if (candidates.Count == 0) return;

        try
        {
            var filePath = Path.Combine(this._dataDir, "rule_candidates.json");
            var existing = new List<RuleCandidate>();
            if (File.Exists(filePath))
            {
                var prev = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
                try { existing = JsonSerializer.Deserialize<List<RuleCandidate>>(prev, JsonOptions) ?? new(); }
                catch { }
            }

            // 合并新候选：替换同ID的，新增不存在的
            foreach (var candidate in candidates)
            {
                var idx = existing.FindIndex(c => c.RuleId == candidate.RuleId);
                if (idx >= 0)
                {
                    existing[idx] = candidate;
                }
                else
                {
                    existing.Add(candidate);
                }
            }

            var json = JsonSerializer.Serialize(existing, JsonOptions);
            await File.WriteAllTextAsync(filePath, json).ConfigureAwait(false);
        }
        catch
        {
            // 静默失败
        }
    }

    /// <summary>
    /// 读取当前所有规则候选（供 RuleEngine 消费）。
    /// </summary>
    public async ValueTask<List<RuleCandidate>> LoadCandidatesAsync()
    {
        var filePath = Path.Combine(this._dataDir, "rule_candidates.json");
        if (!File.Exists(filePath)) return new();

        try
        {
            var json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<RuleCandidate>>(json, JsonOptions) ?? new();
        }
        catch
        {
            return new();
        }
    }
}
