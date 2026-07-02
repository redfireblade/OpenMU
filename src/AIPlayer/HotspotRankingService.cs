// <copyright file="HotspotRankingService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using Microsoft.Extensions.Logging;

/// <summary>
/// 热点排名服务 — 综合分析 <see cref="ExperienceMemory"/> 中各地图/怪物/区域的
/// 经验效率、掉落率、安全性等指标，生成排名推荐给 AI 角色。
///
/// 排名算法：
/// - EXP效率 = 总经验 / 总击杀（归一化）
/// - 安全性 = 1 - (死亡数 / 击杀数)
/// - 掉落收益 = 掉落数 / 击杀数
/// - 综合 = EXP×40% + 安全×30% + 掉落×20% + 等级匹配×10%
/// </summary>
public static class HotspotRankingService
{
    /// <summary>
    /// 获取热点排名（按综合评分降序）。
    /// </summary>
    /// <param name="stats">角色记忆中的 HotspotStats 字典。</param>
    /// <param name="topN">返回前 N 个热点。</param>
    /// <param name="logger">日志记录器（可选）。</param>
    /// <returns>(Key, Score) 列表，Key格式 "mapNum_x_y"。</returns>
    public static List<(string Key, double Score)> GetHotspotRanking(
        Dictionary<string, HotspotStats> stats, int topN, ILogger? logger)
    {
        if (stats is null || stats.Count == 0) return new();

        var scored = new List<(string Key, double Score)>();

        foreach (var kvp in stats)
        {
            var hotspot = kvp.Value;
            if (hotspot.TotalActiveSeconds <= 0) continue;

            // EXP效率：总经验 / 活跃秒数
            var expPerSecond = hotspot.TotalExperience / Math.Max(1, hotspot.TotalActiveSeconds);
            var expScore = Math.Min(1.0, expPerSecond / 100.0);

            // 击杀效率：击杀数 / 活跃秒数
            var killsPerSecond = hotspot.TotalKills / Math.Max(1, hotspot.TotalActiveSeconds);
            var killScore = Math.Min(1.0, killsPerSecond / 2.0);

            // 安全性：1 - (死亡/击杀)
            var safetyScore = 1.0 - Math.Min(1.0, (double)hotspot.TotalDeaths / Math.Max(1, hotspot.TotalKills));

            // 掉落收益：掉落/击杀
            var dropRate = (double)hotspot.TotalDrops / Math.Max(1, hotspot.TotalKills);
            var dropScore = Math.Min(1.0, dropRate * 10.0);

            // 稀有掉落加分：卓越/宝石
            var rareBonus = (hotspot.ExcellentDrops * 5.0 + hotspot.JewelDrops * 10.0) / Math.Max(1, hotspot.TotalKills);

            // 综合评分
            var score = (expScore * 0.35)
                      + (killScore * 0.15)
                      + (safetyScore * 0.25)
                      + (dropScore * 0.15)
                      + (rareBonus * 0.10);

            scored.Add((kvp.Key, score * 1000.0));
        }

        var result = scored.OrderByDescending(s => s.Score).Take(topN).ToList();

        if (logger is not null && result.Count > 0)
        {
            logger.LogDebug("[HotspotRanking] Top {Count}: {Results}",
                result.Count,
                string.Join(", ", result.Select(r => $"{r.Key}={r.Score:F1}")));
        }

        return result;
    }

    /// <summary>
    /// 获取推荐狩猎地图列表（基于 ExperienceMemory）。
    /// </summary>
    public static List<(int MapNumber, double Score, string Reason)> GetRecommendedMaps(
        ExperienceMemory expMem, int playerLevel, int topN = 3)
    {
        if (expMem is null) return new();

        var ranked = expMem.GetRankedMaps(playerLevel, topN);
        var result = new List<(int, double, string)>();

        foreach (var (mapNum, score) in ranked)
        {
            if (!expMem.MapStats.TryGetValue(mapNum, out var mapData)) continue;

            var avgExp = mapData.TotalKills > 0
                ? mapData.TotalExperience / mapData.TotalKills
                : 0;

            var deathRate = mapData.TotalKills > 0
                ? (double)mapData.TotalDeaths / mapData.TotalKills * 100
                : 0;

            var reason = $"EXP/击杀:{avgExp} 死亡率:{deathRate:F1}% 访问{mapData.VisitCount}次";
            result.Add((mapNum, score, reason));
        }

        return result;
    }

    /// <summary>
    /// 获取推荐狩猎怪物（基于 ExperienceMemory 的地图统计）。
    /// </summary>
    public static List<(short MonsterNumber, double Score, string Reason)> GetRecommendedMonsters(
        ExperienceMemory expMem, int mapNumber, int playerLevel, int topN = 5)
    {
        if (expMem is null) return new();

        var ranked = expMem.GetRecommendedMonsters(mapNumber, playerLevel, topN);
        var result = new List<(short, double, string)>();

        foreach (var (monsterNum, score) in ranked)
        {
            if (!expMem.MapStats.TryGetValue(mapNumber, out var mapData)) continue;
            if (!mapData.MonsterStats.TryGetValue($"M{monsterNum}", out var mStat)) continue;

            var reason = $"Lv{mStat.MonsterLevel} 平均EXP:{mStat.AverageExpPerKill} 击杀{mStat.KillCount}次";
            result.Add((monsterNum, score, reason));
        }

        return result;
    }
}
