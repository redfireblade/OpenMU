// <copyright file="GlobalShadowMap.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>
/// AI 群体数字影子地图 — 由 AI 群体决策系统每日从各角色个人影子地图整合而来。
/// 保存个人地图中有价值的连接信息，经1周/1月后沉淀到公共数据表。
///
/// 公共信息层包含：任务表、活动表、各帮派/战盟/家族信息（需权限）、
/// 经济情况、PK记录、战力排行、财富排行、装备排行。
/// </summary>
public sealed class GlobalShadowMap
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly object _lock = new();

    // ===== 公共信息层 =====

    /// <summary>任务知识库 — 统计各任务各职业的推荐等级/属性。</summary>
    public List<QuestKnowledgeRecord> QuestKnowledge { get; set; } = new();

    /// <summary>怪物掉落统计 — 怪物 → 掉落物品概率。</summary>
    public List<DropStatRecord> DropStatistics { get; set; } = new();

    /// <summary>危险区域标记 — 坐标 → 死亡频次。</summary>
    public List<DangerZoneRecord> DangerZones { get; set; } = new();

    /// <summary>热点区域 — 好刷怪/好掉宝的区域。</summary>
    public List<HotspotRecord> Hotspots { get; set; } = new();

    /// <summary>PK 事件记录。</summary>
    public List<PkRecord> PkEvents { get; set; } = new();

    /// <summary>财富排行（每周更新）。</summary>
    public List<RankingRecord> WealthRanking { get; set; } = new();

    /// <summary>战力排行（每周更新）。</summary>
    public List<RankingRecord> PowerRanking { get; set; } = new();

    /// <summary>装备排行（每周更新）。</summary>
    public List<RankingRecord> EquipmentRanking { get; set; } = new();

    /// <summary>最后更新时间。</summary>
    public DateTime LastUpdated { get; set; }

    public GlobalShadowMap(string dataDir, ILogger logger)
    {
        this._filePath = Path.Combine(dataDir, "global_shadow_map.json");
        this._logger = logger;
        this.Load();
    }

    /// <summary>
    /// 从个人影子地图条目中提取有价值的信息，更新群体地图。
    /// 由 AI 群体决策系统定时调用（每日）。
    /// </summary>
    public void ExtractFromPersonal(ShadowMapEntry entry)
    {
        lock (_lock)
        {
            switch (entry.EntryType)
            {
                case ShadowEntryType.Death:
                    // 死亡 → 更新危险区域
                    var danger = this.DangerZones.FirstOrDefault(d =>
                        d.MapNumber == entry.MapNumber && d.X == entry.X && d.Y == entry.Y);
                    if (danger is not null)
                    {
                        danger.DeathCount++;
                        danger.LastDeathAt = entry.CreatedAt;
                    }
                    else
                    {
                        this.DangerZones.Add(new DangerZoneRecord
                        {
                            MapNumber = entry.MapNumber,
                            X = entry.X,
                            Y = entry.Y,
                            MonsterNumber = entry.MonsterNumber,
                            MonsterLevel = entry.MonsterLevel,
                            DeathCount = 1,
                            LastDeathAt = entry.CreatedAt,
                        });
                    }
                    break;

                case ShadowEntryType.Drop:
                    // 掉落 → 更新掉落统计
                    if (entry.ItemGroup is not null && entry.ItemNumber is not null)
                    {
                        var drop = this.DropStatistics.FirstOrDefault(d =>
                            d.MonsterNumber == entry.MonsterNumber
                            && d.ItemGroup == entry.ItemGroup.Value
                            && d.ItemNumber == entry.ItemNumber.Value);
                        if (drop is not null)
                        {
                            drop.ObservedCount++;
                        }
                        else
                        {
                            this.DropStatistics.Add(new DropStatRecord
                            {
                                MonsterNumber = entry.MonsterNumber,
                                MapNumber = entry.MapNumber,
                                ItemGroup = entry.ItemGroup.Value,
                                ItemNumber = entry.ItemNumber.Value,
                                ObservedCount = 1,
                            });
                        }
                    }
                    break;

                case ShadowEntryType.QuestActivity:
                    // 任务活动 → 更新任务知识
                    if (entry.QuestGroup is not null && entry.QuestNumber is not null)
                    {
                        var quest = this.QuestKnowledge.FirstOrDefault(q =>
                            q.QuestGroup == entry.QuestGroup.Value
                            && q.QuestNumber == entry.QuestNumber.Value);
                        if (quest is not null)
                        {
                            quest.TotalAttempts++;
                            quest.TotalDeaths += entry.EntryType == ShadowEntryType.Death ? 1 : 0;
                            if (entry.Level > 0)
                            {
                                quest.LastAttemptLevel = entry.Level;
                                quest.RecommendedLevel = Math.Max(quest.RecommendedLevel,
                                    entry.Level + quest.TotalDeaths * 2);
                            }
                        }
                        else
                        {
                            this.QuestKnowledge.Add(new QuestKnowledgeRecord
                            {
                                QuestGroup = entry.QuestGroup.Value,
                                QuestNumber = entry.QuestNumber.Value,
                                TotalAttempts = 1,
                                TotalDeaths = entry.EntryType == ShadowEntryType.Death ? 1 : 0,
                                LastAttemptLevel = entry.Level,
                                RecommendedLevel = entry.Level + 5,
                            });
                        }
                    }
                    break;
            }

            this.LastUpdated = DateTime.UtcNow;
        }

        this.Save();
    }

    /// <summary>
    /// 获取指定任务的推荐等级（来自群体经验）。
    /// </summary>
    public int? GetRecommendedLevel(short questGroup, short questNumber)
    {
        lock (_lock)
        {
            return this.QuestKnowledge
                .FirstOrDefault(q => q.QuestGroup == questGroup && q.QuestNumber == questNumber)
                ?.RecommendedLevel;
        }
    }

    /// <summary>
    /// 检查坐标是否在已知危险区域。
    /// </summary>
    public bool IsDangerZone(ushort mapNum, byte x, byte y)
    {
        lock (_lock)
        {
            return this.DangerZones.Any(d =>
                d.MapNumber == mapNum
                && Math.Abs(d.X - x) <= 3
                && Math.Abs(d.Y - y) <= 3
                && d.DeathCount >= 3);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(this._filePath)) return;
            var json = File.ReadAllText(this._filePath);
            if (string.IsNullOrWhiteSpace(json)) return;

            var loaded = JsonSerializer.Deserialize<GlobalShadowMap>(json, JsonOptions);
            if (loaded is null) return;

            lock (_lock)
            {
                this.QuestKnowledge = loaded.QuestKnowledge ?? new();
                this.DropStatistics = loaded.DropStatistics ?? new();
                this.DangerZones = loaded.DangerZones ?? new();
                this.Hotspots = loaded.Hotspots ?? new();
                this.PkEvents = loaded.PkEvents ?? new();
                this.WealthRanking = loaded.WealthRanking ?? new();
                this.PowerRanking = loaded.PowerRanking ?? new();
                this.EquipmentRanking = loaded.EquipmentRanking ?? new();
                this.LastUpdated = loaded.LastUpdated;
            }
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "[GlobalShadowMap] 加载失败");
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(this._filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            GlobalShadowMap snapshot;
            lock (_lock) snapshot = this;

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(this._filePath, json);
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "[GlobalShadowMap] 保存失败");
        }
    }
}

// ===== 群体地图子记录类型 =====

/// <summary>任务知识记录。</summary>
public sealed class QuestKnowledgeRecord
{
    public short QuestGroup { get; set; }
    public short QuestNumber { get; set; }
    public int TotalAttempts { get; set; }
    public int TotalDeaths { get; set; }
    public int LastAttemptLevel { get; set; }
    public int RecommendedLevel { get; set; }
}

/// <summary>掉落统计记录。</summary>
public sealed class DropStatRecord
{
    public short? MonsterNumber { get; set; }
    public ushort MapNumber { get; set; }
    public int ItemGroup { get; set; }
    public int ItemNumber { get; set; }
    public int ObservedCount { get; set; }
}

/// <summary>危险区域记录。</summary>
public sealed class DangerZoneRecord
{
    public ushort MapNumber { get; set; }
    public byte X { get; set; }
    public byte Y { get; set; }
    public short? MonsterNumber { get; set; }
    public short? MonsterLevel { get; set; }
    public int DeathCount { get; set; }
    public DateTime LastDeathAt { get; set; }
}

/// <summary>热点区域记录。</summary>
public sealed class HotspotRecord
{
    public ushort MapNumber { get; set; }
    public byte X { get; set; }
    public byte Y { get; set; }
    public string? Description { get; set; }
    public int Score { get; set; }
}

/// <summary>PK 事件记录。</summary>
public sealed class PkRecord
{
    public string? KillerName { get; set; }
    public string? VictimName { get; set; }
    public ushort MapNumber { get; set; }
    public byte X { get; set; }
    public byte Y { get; set; }
    public DateTime OccurredAt { get; set; }
}

/// <summary>排行记录（战力/财富/装备）。</summary>
public sealed class RankingRecord
{
    public string? CharacterName { get; set; }
    public int Rank { get; set; }
    public double Score { get; set; }
    public DateTime RecordedAt { get; set; }
}
