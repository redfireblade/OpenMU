// <copyright file="ExperienceMemory.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>
/// AI 角色经验记忆 — 记录每个地图/怪物区域的打怪/升级/拾取/死亡经验。
/// 这是 AI 学习系统的核心数据存储，每个 AI 角色独立保存。
///
/// 数据来源：
/// 1. ExperienceLearner — 每 tick 收集的战斗/拾取数据（AI 角色）
/// 2. PlayerBehaviorObserver — 观察真实玩家行为转化的经验（真实玩家）
///
/// 数据去向：
/// 1. HotspotRankingService — 读取评分给 AI 推荐狩猎点
/// 2. KnowledgeConsolidator — 汇入群体影子地图
/// 3. HuntingAdvisor — 为 AI 决策提供经验依据
/// </summary>
public sealed class ExperienceMemory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// 地图统计 — key=mapNumber, value=MapExperienceData
    /// </summary>
    public Dictionary<int, MapExperienceData> MapStats { get; set; } = new();

    /// <summary>
    /// 最后保存/加载时间。
    /// </summary>
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 该经验记忆的角色名。
    /// </summary>
    public string CharacterName { get; set; } = string.Empty;

    /// <summary>
    /// 记录一次怪物击杀经验。
    /// </summary>
    public void RecordKill(int mapNumber, short monsterNumber, short monsterLevel, long expGained, byte x, byte y)
    {
        var map = this.GetOrCreateMapStats(mapNumber);

        // 地图级统计
        map.TotalKills++;
        map.TotalExperience += expGained;

        // 怪物级统计
        var monsterKey = $"M{monsterNumber}";
        if (!map.MonsterStats.TryGetValue(monsterKey, out var mStat))
        {
            mStat = new MonsterKillStats();
            map.MonsterStats[monsterKey] = mStat;
        }

        mStat.KillCount++;
        mStat.TotalExperience += expGained;
        mStat.MonsterNumber = monsterNumber;
        mStat.MonsterLevel = monsterLevel;

        // 该怪物的首个击杀记录等级 — 用于"多少级能打这个怪"的推荐
        if (mStat.FirstKillLevel == 0)
        {
            mStat.FirstKillLevel = this.GetLastKnownLevel(mapNumber);
        }

        // 更新怪物经验效率（每 tick 平均经验）
        // 使用指数移动平均：EMA = α * new + (1-α) * old
        const float alpha = 0.1f;
        mStat.AverageExpPerKill = (long)(alpha * expGained + (1 - alpha) * mStat.AverageExpPerKill);
        if (mStat.AverageExpPerKill <= 0) mStat.AverageExpPerKill = expGained;

        this.LastUpdated = DateTime.UtcNow;
    }

    /// <summary>
    /// 记录一次物品掉落观察。
    /// </summary>
    public void RecordDrop(int mapNumber, short monsterNumber, int itemGroup, int itemNumber, byte x, byte y, bool isExcellent = false, bool isJewel = false)
    {
        var map = this.GetOrCreateMapStats(mapNumber);
        map.TotalDrops++;

        var monsterKey = $"M{monsterNumber}";
        if (map.MonsterStats.TryGetValue(monsterKey, out var mStat))
        {
            mStat.TotalDrops++;
            if (isExcellent) mStat.ExcellentDrops++;
            if (isJewel) mStat.JewelDrops++;
        }

        // 记录掉落坐标 — 用于分析"哪里掉了好东西"
        var dropKey = $"M{monsterNumber}_I{itemGroup}_{itemNumber}";
        var coordKey = $"{x},{y}";

        if (!map.DropCoordinates.TryGetValue(dropKey, out var coords))
        {
            coords = new List<string>();
            map.DropCoordinates[dropKey] = coords;
        }

        if (!coords.Contains(coordKey))
        {
            coords.Add(coordKey);
            // 限制最多记录 20 个坐标点，避免无限增长
            if (coords.Count > 20)
            {
                coords.RemoveAt(0);
            }
        }

        this.LastUpdated = DateTime.UtcNow;
    }

    /// <summary>
    /// 记录一次死亡。
    /// </summary>
    public void RecordDeath(int mapNumber, short? killerMonsterNumber, int level)
    {
        var map = this.GetOrCreateMapStats(mapNumber);
        map.TotalDeaths++;

        if (killerMonsterNumber.HasValue)
        {
            var monsterKey = $"M{killerMonsterNumber.Value}";
            if (map.MonsterStats.TryGetValue(monsterKey, out var mStat))
            {
                mStat.PlayerDeaths++;
            }
        }

        // 死亡时的等级 — 用于分析"多少级在这里会死"
        map.DeathLevels.Add(level);
        if (map.DeathLevels.Count > 50) map.DeathLevels.RemoveAt(0);

        this.LastUpdated = DateTime.UtcNow;
    }

    /// <summary>
    /// 记录拾取金币。
    /// </summary>
    public void RecordGoldPickup(int mapNumber, int goldAmount, byte x, byte y)
    {
        var map = this.GetOrCreateMapStats(mapNumber);
        map.TotalGoldPicked += goldAmount;

        var areaKey = $"A{x / 16},{y / 16}";
        if (!map.AreaGold.TryGetValue(areaKey, out var areaGold))
        {
            areaGold = 0;
        }

        map.AreaGold[areaKey] = areaGold + goldAmount;
        this.LastUpdated = DateTime.UtcNow;
    }

    /// <summary>
    /// 记录该角色到达过该地图时的等级。
    /// </summary>
    public void RecordMapVisit(int mapNumber, int level)
    {
        var map = this.GetOrCreateMapStats(mapNumber);
        if (!map.VisitedAtLevels.Contains(level))
        {
            map.VisitedAtLevels.Add(level);
        }

        map.VisitCount++;
        map.LastVisitLevel = level;
        this.LastUpdated = DateTime.UtcNow;
    }

    /// <summary>
    /// 获取最优狩猎地图 — 综合 EXP 效率 + 安全性评分。
    /// </summary>
    public List<(int MapNumber, double Score)> GetRankedMaps(int playerLevel, int topN = 3)
    {
        var scored = new List<(int MapNumber, double Score)>();

        foreach (var kvp in this.MapStats)
        {
            var mapNum = kvp.Key;
            var map = kvp.Value;

            if (map.TotalKills == 0) continue;

            // EXP效率：总经验/总击杀（越高越好）
            var expEfficiency = (double)map.TotalExperience / Math.Max(1, map.TotalKills);

            // 安全性：死亡数/击杀数（越低越好）
            var safetyRatio = 1.0 - Math.Min(1.0, (double)map.TotalDeaths / Math.Max(1, map.TotalKills));

            // 等级匹配度：怪物的平均等级 vs 玩家等级
            var levelMatch = 0.5;
            if (map.MonsterStats.Count > 0)
            {
                var avgMonsterLevel = map.MonsterStats.Values
                    .Where(m => m.MonsterLevel > 0)
                    .Select(m => (double)m.MonsterLevel)
                    .DefaultIfEmpty(0)
                    .Average();

                if (avgMonsterLevel > 0)
                {
                    var diff = Math.Abs(playerLevel - avgMonsterLevel);
                    levelMatch = diff <= 5 ? 1.0 :
                                 diff <= 10 ? 0.8 :
                                 diff <= 20 ? 0.5 : 0.2;
                }
            }

            // 综合评分：EXP效率40% + 安全性30% + 等级匹配30%
            // EXP效率归一化：/10000 假设合理单怪经验约10000
            var score = (Math.Min(1.0, expEfficiency / 10000.0) * 0.40)
                        + (safetyRatio * 0.30)
                        + (levelMatch * 0.30);

            scored.Add((mapNum, score * 1000.0));
        }

        return scored.OrderByDescending(s => s.Score).Take(topN).ToList();
    }

    /// <summary>
    /// 获取推荐狩猎怪物（基于该地图的经验积累）。
    /// </summary>
    public List<(short MonsterNumber, double Score)> GetRecommendedMonsters(int mapNumber, int playerLevel, int topN = 3)
    {
        if (!this.MapStats.TryGetValue(mapNumber, out var map)) return new();

        var scored = new List<(short MonsterNumber, double Score)>();

        foreach (var kvp in map.MonsterStats)
        {
            var mStat = kvp.Value;
            if (mStat.KillCount == 0) continue;

            // EXP效率：平均每击杀经验
            var expScore = Math.Min(1.0, (double)mStat.AverageExpPerKill / 10000.0);

            // 安全性：死亡数 vs 击杀数
            var safety = 1.0 - Math.Min(1.0, (double)mStat.PlayerDeaths / Math.Max(1, mStat.KillCount));

            // 等级匹配：怪物等级 vs 玩家等级
            var levelFit = mStat.MonsterLevel > 0
                ? 1.0 - Math.Min(1.0, Math.Abs(playerLevel - mStat.MonsterLevel) / 30.0)
                : 0.5;

            // 掉落收益：掉率 * 0.1（次要因素）
            var dropBonus = Math.Min(1.0, mStat.TotalDrops / Math.Max(1, mStat.KillCount) * 10.0) * 0.1;

            var score = (expScore * 0.40) + (safety * 0.30) + (levelFit * 0.20) + (dropBonus * 0.10);
            scored.Add((mStat.MonsterNumber, score * 1000.0));
        }

        return scored.OrderByDescending(s => s.Score).Take(topN).ToList();
    }

    /// <summary>
    /// 获取建议的狩猎位置（最好的掉落/经验坐标）。
    /// </summary>
    public List<(byte X, byte Y, double Score)> GetRecommendedPositions(int mapNumber, int topN = 5)
    {
        if (!this.MapStats.TryGetValue(mapNumber, out var map)) return new();

        // 从掉落坐标统计热度
        var positionScores = new Dictionary<string, double>();
        foreach (var kvp in map.DropCoordinates)
        {
            foreach (var coord in kvp.Value)
            {
                if (!positionScores.ContainsKey(coord))
                    positionScores[coord] = 0;
                positionScores[coord]++;
            }
        }

        return positionScores
            .Select(p =>
            {
                var parts = p.Key.Split(',');
                if (byte.TryParse(parts[0], out var x) && byte.TryParse(parts[1], out var y))
                    return (X: x, Y: y, Score: p.Value * 100.0);
                return (X: (byte)0, Y: (byte)0, Score: 0.0);
            })
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .Take(topN)
            .ToList();
    }

    /// <summary>
    /// 从另一个 ExperienceMemory 合并数据（用于玩家经验→AI经验的转化）。
    /// </summary>
    public void MergeFrom(ExperienceMemory other)
    {
        foreach (var kvp in other.MapStats)
        {
            var mapNum = kvp.Key;
            var otherMap = kvp.Value;

            if (!this.MapStats.TryGetValue(mapNum, out var myMap))
            {
                myMap = new MapExperienceData();
                this.MapStats[mapNum] = myMap;
            }

            myMap.TotalKills += otherMap.TotalKills;
            myMap.TotalExperience += otherMap.TotalExperience;
            myMap.TotalDeaths += otherMap.TotalDeaths;
            myMap.TotalDrops += otherMap.TotalDrops;
            myMap.TotalGoldPicked += otherMap.TotalGoldPicked;
            myMap.VisitCount += otherMap.VisitCount;

            // 合并怪物统计
            foreach (var mkvp in otherMap.MonsterStats)
            {
                if (!myMap.MonsterStats.TryGetValue(mkvp.Key, out var myMonster))
                {
                    myMonster = new MonsterKillStats();
                    myMap.MonsterStats[mkvp.Key] = myMonster;
                }

                var om = mkvp.Value;
                myMonster.KillCount += om.KillCount;
                myMonster.TotalExperience += om.TotalExperience;
                myMonster.TotalDrops += om.TotalDrops;
                myMonster.ExcellentDrops += om.ExcellentDrops;
                myMonster.JewelDrops += om.JewelDrops;
                myMonster.PlayerDeaths += om.PlayerDeaths;
                if (myMonster.MonsterNumber == 0) myMonster.MonsterNumber = om.MonsterNumber;
                if (myMonster.MonsterLevel == 0) myMonster.MonsterLevel = om.MonsterLevel;
                if (myMonster.AverageExpPerKill == 0) myMonster.AverageExpPerKill = om.AverageExpPerKill;

                // 取更高效的 EMA
                if (om.AverageExpPerKill > myMonster.AverageExpPerKill)
                    myMonster.AverageExpPerKill = om.AverageExpPerKill;
            }

            // 访问等级去重
            foreach (var lv in otherMap.VisitedAtLevels)
            {
                if (!myMap.VisitedAtLevels.Contains(lv))
                    myMap.VisitedAtLevels.Add(lv);
            }
        }

        this.LastUpdated = DateTime.UtcNow;
    }

    private int GetLastKnownLevel(int mapNumber)
    {
        if (!this.MapStats.TryGetValue(mapNumber, out var map)) return 0;
        return map.LastVisitLevel;
    }

    private MapExperienceData GetOrCreateMapStats(int mapNumber)
    {
        if (!this.MapStats.TryGetValue(mapNumber, out var map))
        {
            map = new MapExperienceData();
            this.MapStats[mapNumber] = map;
        }

        return map;
    }

    /// <summary>
    /// 从 JSON 文件加载经验记忆。
    /// </summary>
    public static async Task<ExperienceMemory?> LoadAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return null;
            var json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return null;

            var loaded = JsonSerializer.Deserialize<ExperienceMemory>(json, JsonOptions);
            if (loaded is null) return null;

            // 确保子集合不为空
            foreach (var kvp in loaded.MapStats)
            {
                kvp.Value.MonsterStats ??= new Dictionary<string, MonsterKillStats>();
                kvp.Value.DropCoordinates ??= new Dictionary<string, List<string>>();
                kvp.Value.AreaGold ??= new Dictionary<string, int>();
                kvp.Value.DeathLevels ??= new List<int>();
                kvp.Value.VisitedAtLevels ??= new List<int>();
            }

            return loaded;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 保存经验记忆到 JSON 文件。
    /// </summary>
    public async Task SaveAsync(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(this, JsonOptions);
            await File.WriteAllTextAsync(filePath, json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ExperienceMemory] Save failed: {ex.Message}");
        }
    }
}

/// <summary>
/// 每张地图的经验数据。
/// </summary>
public sealed class MapExperienceData
{
    /// <summary>总击杀数。</summary>
    public long TotalKills { get; set; }

    /// <summary>总获得经验值。</summary>
    public long TotalExperience { get; set; }

    /// <summary>总死亡次数。</summary>
    public int TotalDeaths { get; set; }

    /// <summary>总掉落数。</summary>
    public int TotalDrops { get; set; }

    /// <summary>总拾取金币。</summary>
    public long TotalGoldPicked { get; set; }

    /// <summary>访问次数。</summary>
    public int VisitCount { get; set; }

    /// <summary>最后访问时等级。</summary>
    public int LastVisitLevel { get; set; }

    /// <summary>各怪物击杀统计 — key="M{monsterNumber}"。</summary>
    public Dictionary<string, MonsterKillStats> MonsterStats { get; set; } = new();

    /// <summary>掉落坐标 — key="M{num}_I{g}_{n}" value=坐标列表。</summary>
    public Dictionary<string, List<string>> DropCoordinates { get; set; } = new();

    /// <summary>区域金币统计 — key="A{x/16},{y/16}" value=总金币。</summary>
    public Dictionary<string, int> AreaGold { get; set; } = new();

    /// <summary>死亡时等级记录。</summary>
    public List<int> DeathLevels { get; set; } = new();

    /// <summary>访问时等级记录。</summary>
    public List<int> VisitedAtLevels { get; set; } = new();
}

/// <summary>
/// 单个怪物的击杀统计。
/// </summary>
public sealed class MonsterKillStats
{
    /// <summary>怪物编号。</summary>
    public short MonsterNumber { get; set; }

    /// <summary>怪物等级。</summary>
    public short MonsterLevel { get; set; }

    /// <summary>击杀次数。</summary>
    public long KillCount { get; set; }

    /// <summary>总获得经验。</summary>
    public long TotalExperience { get; set; }

    /// <summary>平均每击杀经验（指数移动平均）。</summary>
    public long AverageExpPerKill { get; set; }

    /// <summary>总掉落数。</summary>
    public int TotalDrops { get; set; }

    /// <summary>卓越掉落数。</summary>
    public int ExcellentDrops { get; set; }

    /// <summary>宝石掉落数。</summary>
    public int JewelDrops { get; set; }

    /// <summary>该怪物造成的玩家死亡数。</summary>
    public int PlayerDeaths { get; set; }

    /// <summary>首次击杀时的角色等级。</summary>
    public int FirstKillLevel { get; set; }
}
