// <copyright file="BehaviorPattern.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.Evolution;

using MUnique.OpenMU.AIPlayer;

/// <summary>
/// 狩猎模式 — 描述玩家在特定地图区域击杀特定怪物的行为习惯。
/// 通过聚合同地图同一 16x16 区域内的同种怪物击杀事件生成。
/// 置信度随击杀样本数增长，20 次击杀达到满置信度 (1.0)。
/// </summary>
public record HuntingPattern
{
    /// <summary>地图编号。</summary>
    public int MapNumber { get; init; }

    /// <summary>区域中心 X 坐标 (16x16 区块中心)。</summary>
    public byte X { get; init; }

    /// <summary>区域中心 Y 坐标 (16x16 区块中心)。</summary>
    public byte Y { get; init; }

    /// <summary>怪物编号。</summary>
    public short MonsterNumber { get; init; }

    /// <summary>怪物名称。</summary>
    public string? MonsterName { get; init; }

    /// <summary>怪物等级。</summary>
    public short MonsterLevel { get; init; }

    /// <summary>此位置击杀数。</summary>
    public int KillCount { get; init; }

    /// <summary>置信度 0~1 (样本越多越高，20 次击杀 = 满置信度)。</summary>
    public double Confidence { get; init; }

    /// <summary>首次观察到该模式的时间。</summary>
    public DateTime FirstObserved { get; init; }

    /// <summary>最近一次观察到该模式的时间。</summary>
    public DateTime LastObserved { get; init; }
}

/// <summary>
/// 药水使用模式 — 描述玩家购买 HP/MP 药水的行为特征。
/// 注意：购买事件不包含实际喝药的血量阈值信息（阈值需从 <see cref="PlayerBehaviorObserver.BehaviorRecords"/> 获取）。
/// </summary>
public record PotionPattern
{
    /// <summary>是否为 HP 药水（false 表示 MP 药水）。</summary>
    public bool IsHpPotion { get; init; }

    /// <summary>
    /// 喝药时的血量/法力百分比。
    /// 注意：PotionBought 事件不包含此信息，值为 0.0 表示无法从购买事件推断。
    /// 实际阈值可从 <see cref="BehaviorRecordExport.InferredHpPotionThreshold"/> 获取。
    /// </summary>
    public double ThresholdPercent { get; init; }

    /// <summary>购买样本数。</summary>
    public int SampleCount { get; init; }

    /// <summary>置信度 0~1。</summary>
    public double Confidence { get; init; }
}

/// <summary>
/// 技能使用模式 — 描述玩家使用特定技能的行为习惯。
/// 从击杀事件中提取关联的技能使用频率和偏好攻击距离。
/// </summary>
public record SkillPattern
{
    /// <summary>技能编号。</summary>
    public ushort SkillNumber { get; init; }

    /// <summary>技能名称。</summary>
    public string? SkillName { get; init; }

    /// <summary>使用次数。</summary>
    public int UseCount { get; init; }

    /// <summary>使用此技能时的平均攻击距离（0 表示未知）。</summary>
    public int PreferredDistance { get; init; }

    /// <summary>置信度 0~1 (10 次使用 = 满置信度)。</summary>
    public double Confidence { get; init; }
}

/// <summary>
/// 寻径模式 — 描述玩家在同一个地图中从一个 16x16 区域到另一个区域的移动偏好。
/// 通过连续两个击杀事件的位置推断路径段（玩家从 A 区域移动到 B 区域进行击杀）。
/// </summary>
public record NavigationPattern
{
    /// <summary>地图编号。</summary>
    public int MapNumber { get; init; }

    /// <summary>起点区域 X (坐标 / 16)。</summary>
    public byte FromX { get; init; }

    /// <summary>起点区域 Y (坐标 / 16)。</summary>
    public byte FromY { get; init; }

    /// <summary>终点区域 X (坐标 / 16)。</summary>
    public byte ToX { get; init; }

    /// <summary>终点区域 Y (坐标 / 16)。</summary>
    public byte ToY { get; init; }

    /// <summary>走此路线的次数。</summary>
    public int Frequency { get; init; }

    /// <summary>置信度 0~1 (5 次 = 满置信度)。</summary>
    public double Confidence { get; init; }
}

/// <summary>
/// 装备更换模式 — 描述玩家在特定等级更换特定装备的行为。
/// 注意：BehaviorEvent 不记录属性变化量，AttackChange 和 DefenseChange 为 0 占位。
/// </summary>
public record EquipmentPattern
{
    /// <summary>换装时的等级。</summary>
    public int Level { get; init; }

    /// <summary>物品组。</summary>
    public int ItemGroup { get; init; }

    /// <summary>物品编号。</summary>
    public int ItemNumber { get; init; }

    /// <summary>物品名称。</summary>
    public string? ItemName { get; init; }

    /// <summary>攻击力变化量（0 表示未知，BehaviorEvent 不记录此信息）。</summary>
    public double AttackChange { get; init; }

    /// <summary>防御力变化量（0 表示未知，BehaviorEvent 不记录此信息）。</summary>
    public double DefenseChange { get; init; }

    /// <summary>置信度 0~1 (固定值 0.5)。</summary>
    public double Confidence { get; init; }
}

/// <summary>
/// NPC 交互模式 — 描述玩家与特定 NPC 对话并选择选项的行为习惯。
/// 用于学习 NPC buff、修理、交易等交互。
/// </summary>
public record NpcInteractionPattern
{
    /// <summary>NPC 编号。</summary>
    public short NpcNumber { get; init; }

    /// <summary>NPC 名称。</summary>
    public string? NpcName { get; init; }

    /// <summary>交互类型：Talk / Choice / Buff / Buy。</summary>
    public NpcInteractionType InteractionType { get; init; }

    /// <summary>对话选项编号（如 1 = 获得 BUFF）。</summary>
    public int? DialogOption { get; init; }

    /// <summary>交互频率。</summary>
    public int Frequency { get; init; }

    /// <summary>置信度 0~1。</summary>
    public double Confidence { get; init; }
}

/// <summary>
/// 属性点分配模式 — 描述玩家在各属性上分配加点比例的行为习惯。
/// 聚合所有 StatAllocated 事件计算各属性分配占比。
/// </summary>
public record StatAllocationPattern
{
    /// <summary>观察到该模式时的玩家等级。</summary>
    public int Level { get; init; }

    /// <summary>力量加点占比。</summary>
    public double StrWeight { get; init; }

    /// <summary>敏捷加点占比。</summary>
    public double AgiWeight { get; init; }

    /// <summary>体力加点占比。</summary>
    public double VitWeight { get; init; }

    /// <summary>智力加点占比。</summary>
    public double EneWeight { get; init; }

    /// <summary>总分配点数（样本量）。</summary>
    public int SamplePoints { get; init; }
}

/// <summary>
/// 全部分析结果的聚合容器。
/// </summary>
public record AllPatternsResult
{
    /// <summary>狩猎模式列表。</summary>
    public List<HuntingPattern> Hunting { get; init; } = new();

    /// <summary>药水使用模式列表。</summary>
    public List<PotionPattern> Potions { get; init; } = new();

    /// <summary>技能使用模式列表。</summary>
    public List<SkillPattern> Skills { get; init; } = new();

    /// <summary>寻径模式列表。</summary>
    public List<NavigationPattern> Navigation { get; init; } = new();

    /// <summary>装备更换模式列表。</summary>
    public List<EquipmentPattern> Equipment { get; init; } = new();

    /// <summary>属性点分配模式列表。</summary>
    public List<StatAllocationPattern> StatAllocation { get; init; } = new();

    /// <summary>NPC 交互模式列表。</summary>
    public List<NpcInteractionPattern> NpcInteractions { get; set; } = new();
}

/// <summary>
/// 行为模式分析器 — 将原始 <see cref="BehaviorEvent"/> 流转换为带置信度的结构化行为模式。
///
/// 支持六种独立分析维度：
/// <list type="bullet">
///   <item><see cref="AnalyzeHuntingPatterns"/> — 狩猎位置偏好 (MonsterKilled)</item>
///   <item><see cref="AnalyzePotionPatterns"/> — 药水购买行为 (PotionBought)</item>
///   <item><see cref="AnalyzeSkillPatterns"/> — 技能使用习惯 (MonsterKilled + SkillNumber)</item>
///   <item><see cref="AnalyzeNavigationPatterns"/> — 地图内寻径路线 (MonsterKilled 位置序列)</item>
///   <item><see cref="AnalyzeEquipmentPatterns"/> — 装备更换记录 (ItemEquipped)</item>
///   <item><see cref="AnalyzeStatAllocationPatterns"/> — 属性点分配比例 (StatAllocated)</item>
/// </list>
///
/// 所有方法均为无副作用的纯分析函数，线程安全。
/// </summary>
public sealed class BehaviorPatternAnalyzer
{
    /// <summary>
    /// 从事件列表中提取狩猎模式。
    /// 按 (MapNumber, 16x16 区块, MonsterNumber) 分组 MonsterKilled 事件。
    /// </summary>
    /// <param name="events">原始行为事件流。</param>
    /// <returns>识别的狩猎模式列表，按击杀数降序排列。无数据时返回空列表。</returns>
    public List<HuntingPattern> AnalyzeHuntingPatterns(IEnumerable<BehaviorEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        return events
            .Where(e => e.EventType == BehaviorEventType.MonsterKilled && e.MonsterNumber.HasValue)
            .GroupBy(e => (
                MapNumber: e.MapNumber,
                ZoneX: e.X / 16,
                ZoneY: e.Y / 16,
                MonsterNumber: e.MonsterNumber!.Value))
            .Select(g =>
            {
                var count = g.Count();
                var firstObserved = g.Min(e => e.Timestamp);
                var lastObserved = g.Max(e => e.Timestamp);
                var firstEvent = g.First();

                return new HuntingPattern
                {
                    MapNumber = g.Key.MapNumber,
                    X = (byte)((g.Key.ZoneX * 16) + 8),
                    Y = (byte)((g.Key.ZoneY * 16) + 8),
                    MonsterNumber = g.Key.MonsterNumber,
                    MonsterName = firstEvent.MonsterName,
                    MonsterLevel = firstEvent.MonsterLevel ?? 0,
                    KillCount = count,
                    Confidence = Math.Min(1.0, count / 20.0),
                    FirstObserved = firstObserved,
                    LastObserved = lastObserved,
                };
            })
            .OrderByDescending(p => p.KillCount)
            .ToList();
    }

    /// <summary>
    /// 从事件列表中提取药水使用模式。
    /// 通过 ItemPrice 推断药水类型（HP 药水通常比 MP 便宜），
    /// 以 500 金为分界线：低于 500 或价格为 null 视为 HP，否则视为 MP。
    /// </summary>
    /// <param name="events">原始行为事件流。</param>
    /// <returns>识别的药水模式列表（最多 2 条：HP 和 MP）。无数据时返回空列表。</returns>
    public List<PotionPattern> AnalyzePotionPatterns(IEnumerable<BehaviorEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        const int priceThreshold = 500;

        var potionEvents = events
            .Where(e => e.EventType == BehaviorEventType.PotionBought)
            .ToList();

        if (potionEvents.Count == 0)
        {
            return new List<PotionPattern>(0);
        }

        var hpCount = potionEvents.Count(e => !e.ItemPrice.HasValue || e.ItemPrice.Value < priceThreshold);
        var mpCount = potionEvents.Count(e => e.ItemPrice.HasValue && e.ItemPrice.Value >= priceThreshold);

        var result = new List<PotionPattern>(2);

        if (hpCount > 0)
        {
            result.Add(new PotionPattern
            {
                IsHpPotion = true,
                ThresholdPercent = 0.0,
                SampleCount = hpCount,
                Confidence = Math.Min(1.0, hpCount / 5.0),
            });
        }

        if (mpCount > 0)
        {
            result.Add(new PotionPattern
            {
                IsHpPotion = false,
                ThresholdPercent = 0.0,
                SampleCount = mpCount,
                Confidence = Math.Min(1.0, mpCount / 5.0),
            });
        }

        return result;
    }

    /// <summary>
    /// 从事件列表中提取技能使用模式。
    /// 从关联了技能编号的 MonsterKilled 事件中按 SkillNumber 分组统计。
    /// 注意：不关联技能的击杀（普通攻击）不包含在内。
    /// </summary>
    /// <param name="events">原始行为事件流。</param>
    /// <returns>识别的技能模式列表，按使用次数降序排列。无数据时返回空列表。</returns>
    public List<SkillPattern> AnalyzeSkillPatterns(IEnumerable<BehaviorEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        return events
            .Where(e => e.EventType == BehaviorEventType.MonsterKilled && e.SkillNumber.HasValue)
            .GroupBy(e => e.SkillNumber!.Value)
            .Select(g =>
            {
                var count = g.Count();
                var firstEvent = g.First();

                return new SkillPattern
                {
                    SkillNumber = g.Key,
                    SkillName = firstEvent.SkillName,
                    UseCount = count,
                    PreferredDistance = 0,
                    Confidence = Math.Min(1.0, count / 10.0),
                };
            })
            .OrderByDescending(p => p.UseCount)
            .ToList();
    }

    /// <summary>
    /// 从事件列表中提取寻径模式。
    /// 将同地图上连续两次 MonsterKilled 事件的位置视为一条路径段，
    /// 按 (MapNumber, 起点区域, 终点区域) 分组统计。
    /// 区域 = 坐标 / 16 (16x16 区块)。
    /// </summary>
    /// <param name="events">原始行为事件流。</param>
    /// <returns>识别的寻径模式列表，按频率降序排列。无数据时返回空列表。</returns>
    public List<NavigationPattern> AnalyzeNavigationPatterns(IEnumerable<BehaviorEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var killEvents = events
            .Where(e => e.EventType == BehaviorEventType.MonsterKilled)
            .ToList();

        if (killEvents.Count < 2)
        {
            return new List<NavigationPattern>(0);
        }

        // 按地图分组，每个地图内按时间排序，然后构造连续路径段
        var segments = killEvents
            .GroupBy(e => e.MapNumber)
            .SelectMany(mapGroup =>
            {
                var sorted = mapGroup.OrderBy(e => e.Timestamp).ToList();
                var pairs = new List<(int MapNumber, byte FromX, byte FromY, byte ToX, byte ToY)>();

                for (int i = 0; i < sorted.Count - 1; i++)
                {
                    pairs.Add((
                        MapNumber: mapGroup.Key,
                        FromX: (byte)(sorted[i].X / 16),
                        FromY: (byte)(sorted[i].Y / 16),
                        ToX: (byte)(sorted[i + 1].X / 16),
                        ToY: (byte)(sorted[i + 1].Y / 16)));
                }

                return pairs;
            })
            .ToList();

        if (segments.Count == 0)
        {
            return new List<NavigationPattern>(0);
        }

        return segments
            .GroupBy(s => (s.MapNumber, s.FromX, s.FromY, s.ToX, s.ToY))
            .Select(g =>
            {
                var count = g.Count();
                var first = g.First();

                return new NavigationPattern
                {
                    MapNumber = first.MapNumber,
                    FromX = first.FromX,
                    FromY = first.FromY,
                    ToX = first.ToX,
                    ToY = first.ToY,
                    Frequency = count,
                    Confidence = Math.Min(1.0, count / 5.0),
                };
            })
            .OrderByDescending(p => p.Frequency)
            .ToList();
    }

    /// <summary>
    /// 从事件列表中提取装备更换模式。
    /// 按 (等级, ItemGroup, ItemNumber) 分组 ItemEquipped 事件。
    /// 置信度固定为 0.5，因为 BehaviorEvent 不记录属性变化量。
    /// </summary>
    /// <param name="events">原始行为事件流。</param>
    /// <returns>识别的装备更换模式列表，按等级降序排列。无数据时返回空列表。</returns>
    public List<EquipmentPattern> AnalyzeEquipmentPatterns(IEnumerable<BehaviorEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        return events
            .Where(e => e.EventType == BehaviorEventType.ItemEquipped && e.ItemGroup.HasValue && e.ItemNumber.HasValue)
            .GroupBy(e => (
                Level: e.Level,
                ItemGroup: e.ItemGroup!.Value,
                ItemNumber: e.ItemNumber!.Value))
            .Select(g =>
            {
                var firstEvent = g.First();

                return new EquipmentPattern
                {
                    Level = g.Key.Level,
                    ItemGroup = g.Key.ItemGroup,
                    ItemNumber = g.Key.ItemNumber,
                    ItemName = firstEvent.ItemName,
                    AttackChange = 0,
                    DefenseChange = 0,
                    Confidence = 0.5,
                };
            })
            .OrderByDescending(p => p.Level)
            .ToList();
    }

    /// <summary>
    /// 从事件列表中提取属性点分配模式。
    /// 聚合所有 StatAllocated 事件，计算各属性分配占比。
    /// 仅使用 Strength/Agility/Vitality/Energy 前后值均非空的事件。
    /// </summary>
    /// <param name="events">原始行为事件流。</param>
    /// <returns>识别到的加点模式列表（始终包含 0 或 1 条记录）。无数据时返回空列表。</returns>
    public List<StatAllocationPattern> AnalyzeStatAllocationPatterns(IEnumerable<BehaviorEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var allocEvents = events
            .Where(e => e.EventType == BehaviorEventType.StatAllocated
                && e.StrengthBefore.HasValue && e.StrengthAfter.HasValue
                && e.AgilityBefore.HasValue && e.AgilityAfter.HasValue
                && e.VitalityBefore.HasValue && e.VitalityAfter.HasValue
                && e.EnergyBefore.HasValue && e.EnergyAfter.HasValue)
            .ToList();

        if (allocEvents.Count == 0)
        {
            return new List<StatAllocationPattern>(0);
        }

        int totalStr = 0, totalAgi = 0, totalVit = 0, totalEne = 0;
        int maxLevel = 0;

        foreach (var evt in allocEvents)
        {
            totalStr += evt.StrengthAfter!.Value - evt.StrengthBefore!.Value;
            totalAgi += evt.AgilityAfter!.Value - evt.AgilityBefore!.Value;
            totalVit += evt.VitalityAfter!.Value - evt.VitalityBefore!.Value;
            totalEne += evt.EnergyAfter!.Value - evt.EnergyBefore!.Value;

            if (evt.Level > maxLevel)
            {
                maxLevel = evt.Level;
            }
        }

        int totalPoints = totalStr + totalAgi + totalVit + totalEne;

        if (totalPoints <= 0)
        {
            return new List<StatAllocationPattern>(0);
        }

        var pattern = new StatAllocationPattern
        {
            Level = maxLevel,
            StrWeight = (double)totalStr / totalPoints,
            AgiWeight = (double)totalAgi / totalPoints,
            VitWeight = (double)totalVit / totalPoints,
            EneWeight = (double)totalEne / totalPoints,
            SamplePoints = totalPoints,
        };

        return new List<StatAllocationPattern>(1) { pattern };
    }

    /// <summary>
    /// 从事件列表中提取 NPC 交互模式。
    /// 按 (NpcNumber, InteractionType) 分组 NpcTalk/NpcDialogChoice/BuffReceived 事件。
    /// </summary>
    /// <param name="events">原始行为事件流。</param>
    /// <returns>识别的 NPC 交互模式列表，按频率降序排列。无数据时返回空列表。</returns>
    public List<NpcInteractionPattern> AnalyzeNpcInteractionPatterns(IEnumerable<BehaviorEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var npcEvents = events
            .Where(e => e.EventType is BehaviorEventType.NpcTalk
                or BehaviorEventType.NpcDialogChoice
                or BehaviorEventType.BuffReceived
                && e.NpcNumber.HasValue)
            .ToList();

        if (npcEvents.Count == 0)
        {
            return new List<NpcInteractionPattern>(0);
        }

        return npcEvents
            .GroupBy(e => (
                NpcNumber: e.NpcNumber!.Value,
                Type: MapEventTypeToInteractionType(e.EventType),
                DialogOption: e.DialogChoice))
            .Select(g =>
            {
                var count = g.Count();
                var first = g.First();

                return new NpcInteractionPattern
                {
                    NpcNumber = g.Key.NpcNumber,
                    NpcName = first.NpcName,
                    InteractionType = g.Key.Type,
                    DialogOption = g.Key.DialogOption,
                    Frequency = count,
                    Confidence = Math.Min(1.0, count / 3.0),
                };
            })
            .OrderByDescending(p => p.Frequency)
            .ToList();
    }

    private static NpcInteractionType MapEventTypeToInteractionType(BehaviorEventType eventType)
    {
        return eventType switch
        {
            BehaviorEventType.NpcTalk => NpcInteractionType.Talk,
            BehaviorEventType.NpcDialogChoice => NpcInteractionType.Choice,
            BehaviorEventType.BuffReceived => NpcInteractionType.Buff,
            _ => NpcInteractionType.Talk,
        };
    }

    /// <summary>
    /// 全部分析一次调用。
    /// 依次执行所有七种模式分析，返回聚合结果。
    /// </summary>
    /// <param name="events">原始行为事件流。</param>
    /// <returns>包含所有模式分析结果的 <see cref="AllPatternsResult"/>。</returns>
    public AllPatternsResult AnalyzeAll(IEnumerable<BehaviorEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        // 将事件流转换为列表，避免多次枚举
        var eventList = events.ToList();

        return new AllPatternsResult
        {
            Hunting = this.AnalyzeHuntingPatterns(eventList),
            Potions = this.AnalyzePotionPatterns(eventList),
            Skills = this.AnalyzeSkillPatterns(eventList),
            Navigation = this.AnalyzeNavigationPatterns(eventList),
            Equipment = this.AnalyzeEquipmentPatterns(eventList),
            StatAllocation = this.AnalyzeStatAllocationPatterns(eventList),
            NpcInteractions = this.AnalyzeNpcInteractionPatterns(eventList),
        };
    }
}
