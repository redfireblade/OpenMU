// <copyright file="PathConsolidator.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using Microsoft.Extensions.Logging;

/// <summary>
/// AI 群体路径整合器（群体决策系统的一部分）。
/// 每日/每周/每月从各角色的 PathMemoryStore 中扫描所有路径记录，
/// 提取最优路径合并到 GlobalShadowMap 的公共数据表中。
///
/// 原理类似蚂蚁信息素：
/// - 多次执行同一任务的 AI 角色会留下路径记录
/// - 步数短、死亡少的路径得分更高
/// - 群体整合后，新 AI 可直接查询到最优路径
/// </summary>
public sealed class PathConsolidator
{
    private readonly ILogger _logger;

    public PathConsolidator(ILogger logger)
    {
        this._logger = logger;
    }

    /// <summary>
    /// 整合所有记录到群体影子地图（每日调用）。
    /// </summary>
    public void ConsolidateDaily(List<QuestPathRecord> allRecords, GlobalShadowMap globalMap)
    {
        // 按任务+地图分组
        var grouped = allRecords
            .GroupBy(r => (r.QuestGroup, r.QuestNumber, r.MapNumber))
            .ToList();

        foreach (var group in grouped)
        {
            var (questGroup, questNumber, mapNumber) = group.Key;
            var bestPath = group.OrderBy(r => r.PathScore).First();

            this._logger.LogInformation(
                "[PathConsolidate] Q{Group}/{Number} Map{Map}: {Count} 条记录, 最优路径 {Name} {Steps}步 {Deaths}死 得分{Score:F1}",
                questGroup, questNumber, mapNumber, group.Count(),
                bestPath.CharacterName, bestPath.StepCount, bestPath.DeathCount, bestPath.PathScore);

            // 更新群体地图中的热点区域
            foreach (var pointStr in bestPath.PathPoints.Take(100)) // 最多取 100 个点
            {
                var parts = pointStr.Split(',');
                if (parts.Length == 2 && byte.TryParse(parts[0], out var x) && byte.TryParse(parts[1], out var y))
                {
                    // 路径上的点标记为热点
                    var existing = globalMap.Hotspots.FirstOrDefault(h =>
                        h.MapNumber == mapNumber && h.X == x && h.Y == y);
                    if (existing is not null)
                    {
                        existing.Score += 10;
                    }
                    else
                    {
                        globalMap.Hotspots.Add(new HotspotRecord
                        {
                            MapNumber = mapNumber,
                            X = x, Y = y,
                            Description = $"Q{questGroup}/{questNumber} 路径",
                            Score = 10,
                        });
                    }
                }
            }
        }

        globalMap.LastUpdated = DateTime.UtcNow;
        globalMap.Save();

        this._logger.LogInformation("[PathConsolidate] 每日整合完成: {GroupCount} 个任务路线", grouped.Count);
    }
}
