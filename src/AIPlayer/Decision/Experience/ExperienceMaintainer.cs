// <copyright file="ExperienceMaintainer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision.Experience;

using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>
/// 经验库维护器 — 每日合并+质量评分+限流淘汰+每周合并+每月LLM导出。
///
/// 核心原则：
///   1. 越精越好，不是越多越好 — 每日清理低质量经验，控制总量
///   2. 增长红线 — 每日新增 ≤ 总库容 × 2%
///   3. 分库管理 — 9个分类库，各设上限
/// </summary>
public sealed class ExperienceMaintainer
{
    private readonly string _libraryDir;
    private readonly ILogger _logger;

    // ===== 容量模型（基于 mu.dvg.cn 实际游戏知识体系）=====
    // 参考分类: mu.dvg.cn 全量内容（装备/怪物/地图/合成/职业/活动/交易/战斗/任务/强化/神器/套装/商店/元素/攻略）
    // AI 经验库积累的是"AI角色真实经历过的事件和结果"，不是游戏设计数据
    // 详见 memory/02_KNOWLEDGE/experience_library_maintenance.md
    public const int TotalCapacity = 12000;
    public const float DailyGrowthLimit = 0.02f;
    public const int MaxDailyNewEntries = (int)(TotalCapacity * DailyGrowthLimit); // 240

    public static readonly Dictionary<string, int> CategoryLimits = new()
    {
        ["equipment_knowledge"] = 1200,    // 装备经验（武器/防具/翅膀/项链/戒指/耳环/坐骑）
        ["monster_knowledge"] = 1500,      // 怪物经验（BOSS/黄金怪/掉落规律）
        ["map_knowledge"] = 1000,          // 地图经验（新手→高级→精英→元素地图）
        ["crafting_knowledge"] = 800,      // 合成经验（配方/材料/成功率实际统计）
        ["class_knowledge"] = 1500,        // 职业经验（15大职业+加点+技能+发展方向）
        ["event_knowledge"] = 500,         // 活动经验（入场/收益评估）
        ["trading_knowledge"] = 600,       // 交易经验（市场价格/趋势）
        ["combat_knowledge"] = 800,        // 战斗经验（PK/对战策略）
        ["quest_knowledge"] = 600,         // 任务经验（一般/主线/日常）
        ["enhance_knowledge"] = 600,       // 强化经验（+15/再生/卓越/萤石）
        ["artifact_knowledge"] = 400,      // 神器经验（获取/强化/搭配，7种神器）
        ["set_knowledge"] = 500,           // 套装经验（搭配策略/激活条件）
        ["shop_knowledge"] = 400,          // 商店经验（NPC/X商店/瑞币）
        ["element_knowledge"] = 600,       // 元素经验（元素地图/抗性/属性刻印）
        ["guide_knowledge"] = 600,         // 攻略经验（AI自动总结的高效玩法）
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ExperienceMaintainer(string baseDir, ILogger logger)
    {
        this._libraryDir = Path.Combine(baseDir, "aiplayer_data", "experience_db", "library");
        this._logger = logger;
        Directory.CreateDirectory(this._libraryDir);
    }

    /// <summary>
    /// 每日维护：合并24h日志 → 质量评分 → 限流 → 淘汰低质量 → 更新元数据。
    /// </summary>
    public async ValueTask<(int NewEntries, int Pruned, int Merged)> DailyMaintenanceAsync(
        ExperienceAggregator aggregator, ExperienceRuleInjector injector, List<ActionLog> dailyLogs)
    {
        // Step 1: 聚合当日日志
        if (dailyLogs.Count < 5) return (0, 0, 0);

        var (aggregated, candidates) = await aggregator.AggregateAsync(dailyLogs).ConfigureAwait(false);
        if (aggregated.Count == 0 && candidates.Count == 0) return (0, 0, 0);

        // Step 2: 注入高置信度规则
        await injector.InjectAsync(candidates).ConfigureAwait(false);

        // Step 3: 合并到经验库（含质量评分+限流+淘汰）
        var (newEntries, pruned, merged) = await this.MergeIntoLibraryAsync(aggregated).ConfigureAwait(false);

        // Step 4: 更新元数据
        await this.UpdateMetadataAsync(newEntries, pruned, merged).ConfigureAwait(false);

        return (newEntries, pruned, merged);
    }

    /// <summary>
    /// 将聚合结果合并到精炼经验库。
    /// 先分类 → 去重合并 → 限流 → 淘汰尾部。
    /// </summary>
    private async ValueTask<(int NewEntries, int Pruned, int Merged)> MergeIntoLibraryAsync(
        List<AggregatedExperience> aggregated)
    {
        var totalNew = 0;
        var totalPruned = 0;
        var totalMerged = 0;

        var categories = CategoryLimits.Keys.ToList();
        foreach (var category in categories)
        {
            // 属于此分类的聚合结果
            var relevant = aggregated
                .Where(a => GetCategoryKey(a) == category)
                .ToList();
            if (relevant.Count == 0) continue;

            // 读取已有库
            var entries = await this.LoadCategoryAsync(category).ConfigureAwait(false);

            // 去重合并：同维度键的合并
            var merged = 0;
            foreach (var agg in relevant)
            {
                var key = agg.DimensionKey;
                var existing = entries.FirstOrDefault(e => e.DimensionKey == key);
                if (existing is not null)
                {
                    // 合并：加权平均，取较新的 LastUpdated
                    var totalCount = existing.TotalAttempts + agg.TotalAttempts;
                    existing = existing with
                    {
                        TotalAttempts = totalCount,
                        SuccessCount = existing.SuccessCount + agg.SuccessCount,
                        FailCount = existing.FailCount + agg.FailCount,
                        TotalDeaths = existing.TotalDeaths + agg.TotalDeaths,
                        AvgDurationSeconds = (existing.AvgDurationSeconds * existing.TotalAttempts + agg.AvgDurationSeconds * agg.TotalAttempts) / totalCount,
                        AvgGoldEarned = (existing.AvgGoldEarned * existing.TotalAttempts + agg.AvgGoldEarned * agg.TotalAttempts) / totalCount,
                        AvgMonstersKilled = (existing.AvgMonstersKilled * existing.TotalAttempts + agg.AvgMonstersKilled * agg.TotalAttempts) / totalCount,
                        MinPlayerLevel = Math.Min(existing.MinPlayerLevel, agg.MinPlayerLevel),
                        MaxPlayerLevel = Math.Max(existing.MaxPlayerLevel, agg.MaxPlayerLevel),
                        AvgPlayerLevel = (existing.AvgPlayerLevel * existing.TotalAttempts + agg.AvgPlayerLevel * agg.TotalAttempts) / totalCount,
                        LastUpdated = DateTime.UtcNow,
                    };
                    // 重新计算评分
                    var successRate = totalCount > 0 ? (float)agg.SuccessCount / totalCount : 0;
                    existing = existing with
                    {
                        WorthScore = CalculateWorthScore(successRate, existing.AvgGoldEarned, existing.AvgMonstersKilled, existing.TotalDeaths),
                    };
                    merged++;
                }
            }

            // 新增：不存在的维度键
            var newEntries = 0;
            foreach (var agg in relevant)
            {
                if (entries.All(e => e.DimensionKey != agg.DimensionKey))
                {
                    entries.Add(agg);
                    newEntries++;
                }
            }

            // 限流：当日新增 ≤ 该分类上限 × 2%
            var categoryMax = CategoryLimits[category];
            var dailyLimit = Math.Max(1, (int)(categoryMax * DailyGrowthLimit));
            if (newEntries > dailyLimit)
            {
                // 按评分保留 Top N
                entries = entries
                    .OrderByDescending(e => e.WorthScore)
                    .Take(entries.Count - (newEntries - dailyLimit))
                    .ToList();
                this._logger.LogDebug("[ExpLib] {Cat} 限流: {New}新条目, 截断到{dailyLimit}", category, newEntries, dailyLimit);
                newEntries = dailyLimit;
            }

            // 淘汰：超过容量上限时，删除评分最低的 5%
            if (entries.Count > categoryMax)
            {
                var toPrune = entries.Count - categoryMax;
                entries = entries.OrderByDescending(e => e.WorthScore).Take(categoryMax).ToList();
                // 再移除尾部 5%
                var extraPrune = (int)(categoryMax * 0.05);
                if (extraPrune > 0 && entries.Count > 10)
                {
                    entries = entries.Take(entries.Count - extraPrune).ToList();
                    toPrune += extraPrune;
                }
                totalPruned += toPrune;
                this._logger.LogDebug("[ExpLib] {Cat} 淘汰 {ToPrune} 条低质量经验 (当前{Count}/{Max})",
                    category, toPrune, entries.Count, categoryMax);
            }

            // 写回
            await this.SaveCategoryAsync(category, entries).ConfigureAwait(false);
            totalNew += newEntries;
            totalMerged += merged;
        }

        return (totalNew, totalPruned, totalMerged);
    }

    /// <summary>
    /// 每周合并：合并相似条目（同分类+同地图+同怪物）。
    /// 降低碎片化，提高置信度。
    /// </summary>
    public async ValueTask<int> WeeklyMergeAsync()
    {
        var totalMerged = 0;
        foreach (var category in CategoryLimits.Keys)
        {
            var entries = await this.LoadCategoryAsync(category).ConfigureAwait(false);
            if (entries.Count < 5) continue;

            // 按(EventType+MapId+MonsterNumber)分组合并
            var groups = entries.GroupBy(e => $"{e.EventType}:{e.MapId}:{e.MonsterNumber}");
            var merged = new List<AggregatedExperience>();

            foreach (var group in groups)
            {
                var list = group.ToList();
                if (list.Count == 1)
                {
                    merged.Add(list[0]);
                    continue;
                }

                // 多条目合并为一条
                var totalCount = list.Sum(e => e.TotalAttempts);
                var first = list.OrderByDescending(e => e.TotalAttempts).First();
                merged.Add(first with
                {
                    TotalAttempts = totalCount,
                    SuccessCount = list.Sum(e => e.SuccessCount),
                    FailCount = list.Sum(e => e.FailCount),
                    TotalDeaths = list.Sum(e => e.TotalDeaths),
                    AvgDurationSeconds = list.Average(e => e.AvgDurationSeconds),
                    AvgGoldEarned = list.Average(e => e.AvgGoldEarned),
                    AvgMonstersKilled = list.Average(e => e.AvgMonstersKilled),
                    MinPlayerLevel = list.Min(e => e.MinPlayerLevel),
                    MaxPlayerLevel = list.Max(e => e.MaxPlayerLevel),
                    AvgPlayerLevel = (float)list.Average(e => e.AvgPlayerLevel),
                    LastUpdated = DateTime.UtcNow,
                    WorthScore = list.Average(e => e.WorthScore),
                });
                totalMerged += list.Count - 1;
            }

            await this.SaveCategoryAsync(category, merged).ConfigureAwait(false);
        }

        this._logger.LogInformation("[ExpLib] 每周合并完成: {Merged} 条被合并", totalMerged);
        return totalMerged;
    }

    /// <summary>
    /// 每月 LLM 导出：生成经验库摘要 JSON，供 LLM 分析。
    /// </summary>
    public async ValueTask<string> ExportForLlmReviewAsync()
    {
        var dump = new Dictionary<string, object>();

        foreach (var category in CategoryLimits.Keys)
        {
            var entries = await this.LoadCategoryAsync(category).ConfigureAwait(false);
            var topEntries = entries
                .OrderByDescending(e => e.WorthScore)
                .Take(50) // 每个分类只导出 top 50
                .Select(e => new
                {
                    e.DimensionKey,
                    e.EventType,
                    e.MapId,
                    e.MonsterNumber,
                    e.TotalAttempts,
                    e.SuccessCount,
                    e.WorthScore,
                    e.MinPlayerLevel,
                    e.MaxPlayerLevel,
                    e.AvgGoldEarned,
                    e.LastUpdated,
                })
                .ToList();

            dump[category] = new
            {
                totalEntries = entries.Count,
                maxCapacity = CategoryLimits[category],
                utilization = $"{entries.Count}/{CategoryLimits[category]}",
                topEntries,
            };
        }

        var metadata = await this.LoadMetadataAsync().ConfigureAwait(false);
        dump["metadata"] = metadata;

        var json = JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var filePath = Path.Combine(this._libraryDir, "..", "library_dump.json");
        await File.WriteAllTextAsync(filePath, json).ConfigureAwait(false);

        this._logger.LogInformation("[ExpLib] 每月 LLM 导出完成: {Path}", filePath);
        return filePath;
    }

    // ===== 分类辅助 =====

    private static string GetCategoryKey(AggregatedExperience agg) => agg.EventType switch
    {
        "Hunting" or "MiniGame" when agg.MonsterNumber > 0 => "monster_knowledge",
        "Hunting" => "map_knowledge",
        "MiniGame" => "event_knowledge",
        "Crafting" => "crafting_knowledge",
        "Trading" => "trading_knowledge",
        "Pvp" => "combat_knowledge",
        "Travel" => "map_knowledge",
        "Quest" => "quest_knowledge",
        _ => "quest_knowledge",
    };

    private static float CalculateWorthScore(float successRate, float avgGold, float avgKills, int totalDeaths)
    {
        var score = successRate * 40f;
        score += Math.Min(avgGold / 10000f, 1f) * 30f;
        score += Math.Min(avgKills / 10f, 1f) * 15f;
        var deathRate = totalDeaths > 0 ? Math.Min((float)totalDeaths / 100, 1f) : 0;
        score += Math.Max(0f, 1f - deathRate * 5f) * 15f;
        return Math.Clamp(score, 0, 100);
    }

    // ===== 持久化 =====

    private async ValueTask<List<AggregatedExperience>> LoadCategoryAsync(string category)
    {
        var filePath = Path.Combine(this._libraryDir, $"{category}.json");
        if (!File.Exists(filePath)) return new();
        try
        {
            var json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<AggregatedExperience>>(json, JsonOptions) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private async ValueTask SaveCategoryAsync(string category, List<AggregatedExperience> entries)
    {
        var filePath = Path.Combine(this._libraryDir, $"{category}.json");
        var json = JsonSerializer.Serialize(entries, JsonOptions);
        await File.WriteAllTextAsync(filePath, json).ConfigureAwait(false);
    }

    private async ValueTask<LibraryMetadata> LoadMetadataAsync()
    {
        var filePath = Path.Combine(this._libraryDir, "..", "library_metadata.json");
        if (!File.Exists(filePath)) return new LibraryMetadata();
        try
        {
            var json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
            return JsonSerializer.Deserialize<LibraryMetadata>(json, JsonOptions) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private async ValueTask UpdateMetadataAsync(int newEntries, int pruned, int merged)
    {
        var meta = await this.LoadMetadataAsync().ConfigureAwait(false);
        meta.LastDailyMaintenance = DateTime.UtcNow;
        meta.TotalEntries = CategoryLimits.Keys.Sum(k =>
        {
            var path = Path.Combine(this._libraryDir, $"{k}.json");
            if (!File.Exists(path)) return 0;
            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<List<AggregatedExperience>>(json, JsonOptions)?.Count ?? 0;
            }
            catch { return 0; }
        });

        meta.DailyStats = new DailyStats
        {
            Date = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            NewEntries = newEntries,
            PrunedEntries = pruned,
            MergedEntries = merged,
        };

        var filePath = Path.Combine(this._libraryDir, "..", "library_metadata.json");
        var json = JsonSerializer.Serialize(meta, JsonOptions);
        await File.WriteAllTextAsync(filePath, json).ConfigureAwait(false);
    }
}

/// <summary>
/// 经验库元数据。
/// </summary>
public sealed class LibraryMetadata
{
    public int Version { get; set; } = 1;
    public DateTime LastDailyMaintenance { get; set; }
    public DateTime LastWeeklyMerge { get; set; }
    public DateTime LastMonthlyReview { get; set; }
    public int TotalEntries { get; set; }
    public int TotalCapacity { get; set; } = ExperienceMaintainer.TotalCapacity;
    public DailyStats? DailyStats { get; set; }
}

public sealed class DailyStats
{
    public string Date { get; set; } = string.Empty;
    public int NewEntries { get; set; }
    public int PrunedEntries { get; set; }
    public int MergedEntries { get; set; }
}
