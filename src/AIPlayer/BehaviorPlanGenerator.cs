// <copyright file="BehaviorPlanGenerator.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using Microsoft.Extensions.Logging;

/// <summary>
/// 行为计划生成器 — 将原始 <see cref="BehaviorEvent"/> 事件流分析合成为
/// 结构化的 <see cref="LevelingPlan"/> 升级计划。
///
/// 分析算法：
/// 1. 按等级范围聚类事件
/// 2. 检测 NPC交互模式（对话 + 选择）
/// 3. 检测物品购买链（什么等级买了什么）
/// 4. 检测技能学习时机
/// 5. 检测装备替换时机
/// 6. 检测加点优先级（先加哪个属性 = 最高优先级）
/// 7. 生成有序步骤列表
/// </summary>
public static class BehaviorPlanGenerator
{
    /// <summary>
    /// 从行为事件生成升级计划。
    /// </summary>
    /// <param name="characterName">角色名。</param>
    /// <param name="characterClass">职业编号。</param>
    /// <param name="events">该角色的所有行为事件。</param>
    /// <param name="logger">日志器。</param>
    /// <returns>结构化升级计划。</returns>
    public static LevelingPlan GeneratePlan(
        string characterName,
        int characterClass,
        IReadOnlyList<BehaviorEvent> events,
        ILogger? logger)
    {
        var plan = new LevelingPlan
        {
            CharacterName = characterName,
            CharacterClass = characterClass,
            ClassName = GetClassName(characterClass),
            MaxObservedLevel = events.Count > 0 ? events.Max(e => e.Level) : 0,
            GeneratedAt = DateTime.UtcNow,
        };

        if (events.Count == 0)
        {
            logger?.LogWarning("[PlanGen] 角色 {Name} 无行为事件，无法生成计划", characterName);
            return plan;
        }

        // Step 1: 检测等级分割点 — 找到事件密集的等级区间
        var phaseBoundaries = DetectPhaseBoundaries(events, characterClass, logger);
        logger?.LogDebug("[PlanGen] {Name} 检测到 {N} 个阶段边界: {Boundaries}",
            characterName, phaseBoundaries.Count,
            string.Join(" → ", phaseBoundaries.Select(b => $"Lv{b.MinLevel}-{b.MaxLevel}")));

        // Step 2: 每个阶段生成步骤
        LevelingPhase? lastPhase = null;
        foreach (var boundary in phaseBoundaries)
        {
            var phaseEvents = events
                .Where(e => e.Level >= boundary.MinLevel && e.Level <= boundary.MaxLevel)
                .OrderBy(e => e.Timestamp)
                .ToList();

            var phase = BuildPhase(boundary, phaseEvents, events, characterClass, logger);

            // 合并空阶段（只有加点没有其他行为）到上一个阶段
            var onlyStatSteps = phase.Steps.All(s => s.Type == StepType.AllocateStat);
            var noNpcItems = phase.Steps.Count == 0 && phase.TargetMonsters.Count == 0;

            if (lastPhase is not null && (noNpcItems || onlyStatSteps))
            {
                // 合并到上一个阶段
                if (phase.Steps.Count > 0)
                    lastPhase.Steps.AddRange(phase.Steps);
                lastPhase.MaxLevel = phase.MaxLevel;
                if (phase.TargetMonsters.Count > 0)
                    lastPhase.TargetMonsters.AddRange(phase.TargetMonsters);
                continue;
            }

            if (phase.Steps.Count > 0 || phase.TargetMonsters.Count > 0)
            {
                plan.Phases.Add(phase);
                lastPhase = phase;
            }
        }

        // Step 3: 推断全局加点策略
        var statPriorities = InferStatPriorities(events, logger);
        plan.StatPriorities = statPriorities;

        if (statPriorities.Count > 0)
        {
            var descParts = statPriorities
                .Select(s => $"{s.StatName}→{(s.TargetValue.HasValue ? $"{s.TargetValue}值" : "满")}(优先级{s.Priority})");
            plan.StatAllocationStrategy = string.Join(" → ", descParts);

            if (!string.IsNullOrEmpty(plan.StatAllocationStrategy))
            {
                logger?.LogInformation("[PlanGen] {Name} 加点策略: {Strategy}",
                    characterName, plan.StatAllocationStrategy);
            }
        }

        logger?.LogInformation("[PlanGen] ✅ {Name} 升级计划生成完成: {Phases} 个阶段, {Steps} 总步骤",
            characterName, plan.Phases.Count, plan.Phases.Sum(p => p.Steps.Count));

        return plan;
    }

    /// <summary>
    /// 检测等级阶段边界。
    /// 算法：找关键事件（学习技能、换装、换地图）的等级作为阶段分割点。
    /// </summary>
    private static List<PhaseBoundary> DetectPhaseBoundaries(
        IReadOnlyList<BehaviorEvent> events,
        int characterClass,
        ILogger? logger)
    {
        var boundaries = new List<PhaseBoundary>();

        // 收集关键事件等级（技能学习、换装、BUFF获取、换地图）
        var keyLevels = new HashSet<int>();

        foreach (var evt in events)
        {
            if (evt.EventType == BehaviorEventType.SkillLearned)
                keyLevels.Add(evt.Level);

            if (evt.EventType == BehaviorEventType.BuffReceived)
                keyLevels.Add(evt.Level);

            if (evt.EventType == BehaviorEventType.LevelUp && evt.Level <= 10)
                keyLevels.Add(evt.Level);

            if (evt.EventType == BehaviorEventType.EquipmentBought)
                keyLevels.Add(evt.Level);

            if (evt.EventType == BehaviorEventType.StatAllocated && evt.Level <= 15)
                keyLevels.Add(evt.Level);
        }

        // 找出所有有事件的等级
        var levelWithEvents = events
            .Select(e => e.Level)
            .Distinct()
            .OrderBy(l => l)
            .ToList();

        if (levelWithEvents.Count == 0)
        {
            boundaries.Add(new PhaseBoundary { MinLevel = 1, MaxLevel = 1 });
            return boundaries;
        }

        var maxLevel = levelWithEvents.Max();

        // 经典的阶段分割点（结合硬编码阈值和检测到的关键等级）
        var splitPoints = new SortedSet<int> { 1 };

        // 添加检测到的关键等级
        foreach (var kl in keyLevels.Where(kl => kl > 1 && kl <= maxLevel))
            splitPoints.Add(kl);

        // 每 5 级至少一个阶段（确保粒度合理）
        for (int lv = 5; lv <= maxLevel; lv += 5)
            splitPoints.Add(lv);

        // 添加最后一个等级
        if (maxLevel > 1)
            splitPoints.Add(maxLevel);

        // 转成阶段
        var sortedLevels = splitPoints.ToList();
        for (int i = 0; i < sortedLevels.Count - 1; i++)
        {
            var minLv = sortedLevels[i];
            var maxLv = sortedLevels[i + 1];

            // 合并等级差距 ≤ 2 且无关键事件的相邻阶段
            if (boundaries.Count > 0 && maxLv - minLv <= 2)
            {
                var prev = boundaries[^1];
                if (!keyLevels.Contains(minLv))
                {
                    prev.MaxLevel = maxLv;
                    continue;
                }
            }

            boundaries.Add(new PhaseBoundary
            {
                MinLevel = minLv,
                MaxLevel = maxLv,
            });
        }

        // 确保至少一个阶段
        if (boundaries.Count == 0)
        {
            boundaries.Add(new PhaseBoundary { MinLevel = 1, MaxLevel = Math.Max(1, maxLevel) });
        }

        return boundaries;
    }

    /// <summary>
    /// 从一个等级范围的事件列表构建阶段。
    /// </summary>
    private static LevelingPhase BuildPhase(
        PhaseBoundary boundary,
        List<BehaviorEvent> phaseEvents,
        IReadOnlyList<BehaviorEvent> allEvents,
        int characterClass,
        ILogger? logger)
    {
        var phase = new LevelingPhase
        {
            MinLevel = boundary.MinLevel,
            MaxLevel = boundary.MaxLevel,
            Name = GeneratePhaseName(boundary, phaseEvents, characterClass),
        };

        // 1. 检测 NPC 交互模式
        DetectNpcPatterns(phase, phaseEvents, allEvents);

        // 2. 检测购买行为
        DetectPurchasePatterns(phase, phaseEvents);

        // 3. 检测技能学习
        DetectSkillLearning(phase, phaseEvents);

        // 4. 检测装备行为
        DetectEquipmentPatterns(phase, phaseEvents);

        // 5. 检测加点模式
        DetectStatAllocation(phase, phaseEvents);

        // 6. 检测狩猎怪物
        DetectHuntingTargets(phase, phaseEvents);

        // 7. 生成阶段描述
        phase.Description = GeneratePhaseDescription(phase);

        return phase;
    }

    private static void DetectNpcPatterns(LevelingPhase phase, List<BehaviorEvent> phaseEvents, IReadOnlyList<BehaviorEvent> allEvents)
    {
        // 找出所有 NPC 对话 + 对话选项事件
        var npcTalks = phaseEvents
            .Where(e => e.EventType == BehaviorEventType.NpcTalk)
            .GroupBy(e => e.NpcName ?? $"NPC#{e.NpcNumber}")
            .ToList();

        var npcChoices = phaseEvents
            .Where(e => e.EventType == BehaviorEventType.NpcDialogChoice)
            .GroupBy(e => e.NpcName ?? $"NPC#{e.NpcNumber}")
            .ToList();

        // 按出现频率排序
        foreach (var group in npcTalks.OrderByDescending(g => g.Count()))
        {
            var npcEvent = group.First();

            // 检查是否有对应的对话选项
            var choiceGroup = npcChoices.FirstOrDefault(g => g.Key == group.Key);
            if (choiceGroup is not null)
            {
                foreach (var choice in choiceGroup.OrderBy(c => c.Timestamp))
                {
                    phase.Steps.Add(new PhaseStep
                    {
                        Type = StepType.SelectDialogOption,
                        Description = $"与 {npcEvent.NpcName} 对话选择选项{choice.DialogChoice}",
                        NpcName = npcEvent.NpcName,
                        NpcNumber = npcEvent.NpcNumber,
                        DialogChoice = choice.DialogChoice,
                        DialogChoiceDescription = choice.DialogChoiceDescription,
                        MapNumber = choice.MapNumber,
                        MapName = choice.MapName,
                    });
                }
            }
            else
            {
                phase.Steps.Add(new PhaseStep
                {
                    Type = StepType.TalkToNpc,
                    Description = $"与 {npcEvent.NpcName} 对话",
                    NpcName = npcEvent.NpcName,
                    NpcNumber = npcEvent.NpcNumber,
                    MapNumber = npcEvent.MapNumber,
                    MapName = npcEvent.MapName,
                });
            }
        }

        // 检测 BUFF 获取
        var buffEvents = phaseEvents.Where(e => e.EventType == BehaviorEventType.BuffReceived).ToList();
        foreach (var buff in buffEvents)
        {
            phase.Steps.Add(new PhaseStep
            {
                Type = StepType.SelectDialogOption,
                Description = $"从 {buff.NpcName} 获得增益BUFF",
                NpcName = buff.NpcName,
                NpcNumber = buff.NpcNumber,
                DialogChoice = buff.DialogChoice,
            });
        }
    }

    private static void DetectPurchasePatterns(LevelingPhase phase, List<BehaviorEvent> phaseEvents)
    {
        // 药水购买
        var potionBuys = phaseEvents.Where(e => e.EventType == BehaviorEventType.PotionBought).ToList();
        foreach (var buy in potionBuys)
        {
            if (phase.Steps.Any(s => s.Type == StepType.BuyFromNpc && s.NpcNumber == buy.NpcNumber
                && s.ItemName?.Contains("药水") == true))
                continue;

            phase.Steps.Add(new PhaseStep
            {
                Type = StepType.BuyFromNpc,
                Description = $"从 {buy.NpcName} 购买药水 ×{(buy.Quantity ?? 5)}",
                NpcName = buy.NpcName,
                NpcNumber = buy.NpcNumber,
                ItemName = "HP/MP药水",
                Quantity = buy.Quantity ?? 5,
                MapNumber = buy.MapNumber,
                MapName = buy.MapName,
            });
        }

        // 装备购买
        var equipBuys = phaseEvents.Where(e => e.EventType == BehaviorEventType.EquipmentBought).ToList();
        foreach (var buy in equipBuys)
        {
            phase.Steps.Add(new PhaseStep
            {
                Type = StepType.BuyFromNpc,
                Description = $"从 {buy.NpcName} 购买 {buy.ItemName}",
                NpcName = buy.NpcName,
                NpcNumber = buy.NpcNumber,
                ItemName = buy.ItemName,
                ItemId = buy.ItemGroup.HasValue && buy.ItemNumber.HasValue
                    ? (buy.ItemGroup.Value, buy.ItemNumber.Value) : null,
                MapNumber = buy.MapNumber,
                MapName = buy.MapName,
            });
        }

        // 其他物品购买
        var itemBuys = phaseEvents.Where(e => e.EventType == BehaviorEventType.ItemBought).ToList();
        foreach (var buy in itemBuys)
        {
            if (phase.Steps.Any(s => s.Type == StepType.BuyFromNpc && s.ItemName == buy.ItemName
                && s.NpcNumber == buy.NpcNumber))
                continue;

            phase.Steps.Add(new PhaseStep
            {
                Type = StepType.BuyFromNpc,
                Description = $"从 {buy.NpcName} 购买 {buy.ItemName} ×{(buy.Quantity ?? 1)}",
                NpcName = buy.NpcName,
                NpcNumber = buy.NpcNumber,
                ItemName = buy.ItemName,
                Quantity = buy.Quantity ?? 1,
                MapNumber = buy.MapNumber,
                MapName = buy.MapName,
            });
        }
    }

    private static void DetectSkillLearning(LevelingPhase phase, List<BehaviorEvent> phaseEvents)
    {
        var skillEvents = phaseEvents.Where(e => e.EventType == BehaviorEventType.SkillLearned).ToList();
        foreach (var skill in skillEvents)
        {
            phase.Steps.Add(new PhaseStep
            {
                Type = StepType.LearnSkill,
                Description = $"学习技能 {skill.SkillName}",
                SkillName = skill.SkillName,
                SkillNumber = skill.SkillNumber,
            });
        }
    }

    private static void DetectEquipmentPatterns(LevelingPhase phase, List<BehaviorEvent> phaseEvents)
    {
        var equipEvents = phaseEvents.Where(e => e.EventType == BehaviorEventType.ItemEquipped).ToList();
        foreach (var equip in equipEvents)
        {
            phase.Steps.Add(new PhaseStep
            {
                Type = StepType.EquipItem,
                Description = $"装备 {equip.ItemName}",
                ItemName = equip.ItemName,
            });
        }
    }

    private static void DetectStatAllocation(LevelingPhase phase, List<BehaviorEvent> phaseEvents)
    {
        var statEvents = phaseEvents
            .Where(e => e.EventType == BehaviorEventType.StatAllocated)
            .OrderBy(e => e.Timestamp)
            .ToList();

        if (statEvents.Count == 0) return;

        // 计算该阶段各属性的增量
        int strDelta = 0, agiDelta = 0, vitDelta = 0, eneDelta = 0;

        foreach (var evt in statEvents)
        {
            if (evt.StrengthAfter.HasValue && evt.StrengthBefore.HasValue)
                strDelta += evt.StrengthAfter.Value - evt.StrengthBefore.Value;
            if (evt.AgilityAfter.HasValue && evt.AgilityBefore.HasValue)
                agiDelta += evt.AgilityAfter.Value - evt.AgilityBefore.Value;
            if (evt.VitalityAfter.HasValue && evt.VitalityBefore.HasValue)
                vitDelta += evt.VitalityAfter.Value - evt.VitalityBefore.Value;
            if (evt.EnergyAfter.HasValue && evt.EnergyBefore.HasValue)
                eneDelta += evt.EnergyAfter.Value - evt.EnergyBefore.Value;
        }

        // 按增量排序，增量最大的先加
        var statDeltas = new List<(string Name, int Delta)>
        {
            ("力量", strDelta),
            ("敏捷", agiDelta),
            ("体力", vitDelta),
            ("智力", eneDelta),
        }.Where(s => s.Delta > 0).OrderByDescending(s => s.Delta).ToList();

        if (statDeltas.Count == 0) return;

        // 获取各项属性的最终值（取最后一次事件的值）
        var lastStat = statEvents[^1];
        var finalValues = new Dictionary<string, int>();
        if (lastStat.StrengthAfter.HasValue) finalValues["力量"] = lastStat.StrengthAfter.Value;
        if (lastStat.AgilityAfter.HasValue) finalValues["敏捷"] = lastStat.AgilityAfter.Value;
        if (lastStat.VitalityAfter.HasValue) finalValues["体力"] = lastStat.VitalityAfter.Value;
        if (lastStat.EnergyAfter.HasValue) finalValues["智力"] = lastStat.EnergyAfter.Value;

        var desc = string.Join(" → ",
            statDeltas.Select(s => $"{s.Name}+{s.Delta}"));

        phase.Steps.Add(new PhaseStep
        {
            Type = StepType.AllocateStat,
            Description = $"分配属性点: {desc}",
        });
    }

    private static void DetectHuntingTargets(LevelingPhase phase, List<BehaviorEvent> phaseEvents)
    {
        var killEvents = phaseEvents.Where(e => e.EventType == BehaviorEventType.MonsterKilled).ToList();
        if (killEvents.Count == 0) return;

        // 按怪物分组统计
        var monsterGroups = killEvents
            .GroupBy(e => (e.MonsterNumber, e.MonsterName))
            .Select(g => new RecommendedMonster
            {
                MonsterNumber = g.Key.MonsterNumber ?? 0,
                MonsterName = g.Key.MonsterName ?? $"#{g.Key.MonsterNumber}",
                MonsterLevel = g.First().MonsterLevel ?? 0,
                KillCount = g.Count(),
            })
            .OrderByDescending(m => m.KillCount)
            .ToList();

        phase.TargetMonsters = monsterGroups;

        // 获取狩猎地图
        var mapGroups = killEvents
            .GroupBy(e => (e.MapNumber, e.MapName))
            .OrderByDescending(g => g.Count())
            .ToList();

        if (mapGroups.Count > 0)
        {
            var mainMap = mapGroups.First();
            phase.HuntingMapNumber = mainMap.Key.MapNumber;
            phase.HuntingMapName = mainMap.Key.MapName;

            // 确保"狩猎怪物"步骤在购买/准备步骤之后
            var descParts = monsterGroups.Take(3).Select(m => $"{m.MonsterName}(Lv{m.MonsterLevel})");
            phase.Steps.Add(new PhaseStep
            {
                Type = StepType.KillMonster,
                Description = $"在 {mainMap.Key.MapName} 击杀: {string.Join(", ", descParts)}",
                MapNumber = mainMap.Key.MapNumber,
                MapName = mainMap.Key.MapName,
            });
        }
    }

    /// <summary>
    /// 推断全局加点策略。
    /// 分析：哪些属性在低等级时优先增加，以及每个属性的目标值。
    /// 算法：按"首次加点等级"排序（先点的属性优先级高）。
    /// </summary>
    private static List<StatPriority> InferStatPriorities(IReadOnlyList<BehaviorEvent> events, ILogger? logger)
    {
        var result = new List<StatPriority>();

        var statEvents = events
            .Where(e => e.EventType == BehaviorEventType.StatAllocated)
            .OrderBy(e => e.Timestamp)
            .ToList();

        if (statEvents.Count == 0) return result;

        // 法师初始属性：力18 敏18 体15 智30
        int baseStr = 18, baseAgi = 18, baseVit = 15, baseEne = 30;

        // 记录每个属性的首次变化等级和最终值
        int? firstLevelStr = null, firstLevelAgi = null, firstLevelVit = null, firstLevelEne = null;
        int? finalStr = null, finalAgi = null, finalVit = null, finalEne = null;

        foreach (var evt in statEvents)
        {
            // 检测力量首次变化
            if (evt.StrengthAfter.HasValue && evt.StrengthBefore.HasValue
                && evt.StrengthAfter > evt.StrengthBefore && !firstLevelStr.HasValue)
            {
                firstLevelStr = evt.Level;
            }
            // 同样检测敏捷、体力、智力
            if (evt.AgilityAfter.HasValue && evt.AgilityBefore.HasValue
                && evt.AgilityAfter > evt.AgilityBefore && !firstLevelAgi.HasValue)
                firstLevelAgi = evt.Level;
            if (evt.VitalityAfter.HasValue && evt.VitalityBefore.HasValue
                && evt.VitalityAfter > evt.VitalityBefore && !firstLevelVit.HasValue)
                firstLevelVit = evt.Level;
            if (evt.EnergyAfter.HasValue && evt.EnergyBefore.HasValue
                && evt.EnergyAfter > evt.EnergyBefore && !firstLevelEne.HasValue)
                firstLevelEne = evt.Level;

            // 记录最终值
            if (evt.StrengthAfter.HasValue) finalStr = evt.StrengthAfter.Value;
            if (evt.AgilityAfter.HasValue) finalAgi = evt.AgilityAfter.Value;
            if (evt.VitalityAfter.HasValue) finalVit = evt.VitalityAfter.Value;
            if (evt.EnergyAfter.HasValue) finalEne = evt.EnergyAfter.Value;
        }

        // 检测每个属性的稳定目标值
        var statTargets = new Dictionary<string, (string Name, string EnglishName, int BaseValue, int? FirstLevel, int? FinalValue, List<(int Level, int Value)> Snapshots)>();

        // 收集每个属性的等级-值快照序列
        var strSnapshots = new List<(int Level, int Value)>();
        var agiSnapshots = new List<(int Level, int Value)>();
        var vitSnapshots = new List<(int Level, int Value)>();
        var eneSnapshots = new List<(int Level, int Value)>();

        foreach (var evt in statEvents)
        {
            if (evt.StrengthAfter.HasValue) strSnapshots.Add((evt.Level, evt.StrengthAfter.Value));
            if (evt.AgilityAfter.HasValue) agiSnapshots.Add((evt.Level, evt.AgilityAfter.Value));
            if (evt.VitalityAfter.HasValue) vitSnapshots.Add((evt.Level, evt.VitalityAfter.Value));
            if (evt.EnergyAfter.HasValue) eneSnapshots.Add((evt.Level, evt.EnergyAfter.Value));
        }

        // 推断目标值：看属性是否在某个值上稳定了至少2级
        int InferTargetValue(List<(int Level, int Value)> snapshots, int baseValue)
        {
            if (snapshots.Count == 0) return baseValue;
            var maxVal = snapshots.Max(s => s.Value);
            if (maxVal <= baseValue) return baseValue;

            // 从最后一次出现找稳定值：如果最后N次都是同一个值，那就是目标
            var lastVal = snapshots[^1].Value;
            var stableCount = 0;
            for (int i = snapshots.Count - 1; i >= 0; i--)
            {
                if (snapshots[i].Value == lastVal)
                    stableCount++;
                else
                    break;
            }
            if (stableCount >= 2 && lastVal > baseValue)
                return lastVal;

            return maxVal;
        }

        // 构建优先级列表：按首次加点等级排序（先加的优先级高）
        var statsWithFirstLevel = new List<(string Name, string EnglishName, int? FirstLevel, int? FinalValue, int TargetValue)>();

        if (firstLevelStr.HasValue && finalStr > baseStr)
            statsWithFirstLevel.Add(("力量", "Strength", firstLevelStr, finalStr, InferTargetValue(strSnapshots, baseStr)));
        if (firstLevelAgi.HasValue && finalAgi > baseAgi)
            statsWithFirstLevel.Add(("敏捷", "Agility", firstLevelAgi, finalAgi, InferTargetValue(agiSnapshots, baseAgi)));
        if (firstLevelVit.HasValue && finalVit > baseVit)
            statsWithFirstLevel.Add(("体力", "Vitality", firstLevelVit, finalVit, InferTargetValue(vitSnapshots, baseVit)));
        if (firstLevelEne.HasValue && finalEne > baseEne)
            statsWithFirstLevel.Add(("智力", "Energy", firstLevelEne, finalEne, InferTargetValue(eneSnapshots, baseEne)));

        // 按首次加点等级升序排列
        var ranked = statsWithFirstLevel
            .OrderBy(s => s.FirstLevel ?? 999)
            .ToList();

        for (int i = 0; i < ranked.Count; i++)
        {
            var reason = ranked[i].TargetValue > ranked[i].FinalValue.GetValueOrDefault()
                ? $"至少升到 {ranked[i].FinalValue} 点（观察所得，持续增加中）"
                : $"升到 {ranked[i].TargetValue} 点（观察所得）";

            if (ranked[i].FirstLevel.HasValue)
            {
                reason += $"（从 Lv{ranked[i].FirstLevel} 开始加点）";
            }

            result.Add(new StatPriority
            {
                StatName = ranked[i].EnglishName,
                Priority = i + 1,
                TargetValue = ranked[i].TargetValue,
                Reason = reason,
            });
        }

        return result;
    }

    private static string GeneratePhaseName(PhaseBoundary boundary, List<BehaviorEvent> phaseEvents, int characterClass)
    {
        var minLv = boundary.MinLevel;
        var maxLv = boundary.MaxLevel;

        // 根据等级范围和事件类型生成阶段名
        if (maxLv <= 5)
            return $"入门期 (Lv{minLv}-{maxLv})";

        if (maxLv <= 10)
            return $"成长初期 (Lv{minLv}-{maxLv})";

        if (maxLv <= 15)
            return $"成长期 (Lv{minLv}-{maxLv})";

        if (phaseEvents.Any(e => e.EventType == BehaviorEventType.SkillLearned && e.Level >= minLv && e.Level <= maxLv))
            return $"技能发展期 (Lv{minLv}-{maxLv})";

        if (phaseEvents.Any(e => e.EventType == BehaviorEventType.EquipmentBought))
            return $"装备提升期 (Lv{minLv}-{maxLv})";

        return $"狩猎期 (Lv{minLv}-{maxLv})";
    }

    private static string GeneratePhaseDescription(LevelingPhase phase)
    {
        var parts = new List<string>();

        if (phase.TargetMonsters.Count > 0)
        {
            var topMonsters = phase.TargetMonsters.Take(3)
                .Select(m => $"{m.MonsterName}(Lv{m.MonsterLevel})×{m.KillCount}");
            parts.Add($"击杀: {string.Join(", ", topMonsters)}");
        }

        if (phase.Steps.Any(s => s.Type == StepType.AllocateStat))
        {
            var allocDesc = phase.Steps
                .Where(s => s.Type == StepType.AllocateStat)
                .Select(s => s.Description);
            parts.Add(string.Join(" ", allocDesc));
        }

        if (phase.HuntingMapName is not null)
        {
            parts.Add($"狩猎地图: {phase.HuntingMapName}");
        }

        return parts.Count > 0 ? string.Join(" | ", parts) : $"等级 {phase.MinLevel}-{phase.MaxLevel}";
    }

    private static string GetClassName(int classNum) => classNum switch
    {
        0 => "黑暗巫师 (Dark Wizard)",
        4 => "黑暗骑士 (Dark Knight)",
        8 => "精灵 (Elf)ちゃん",
        _ => $"职业#{classNum}",
    };

    /// <summary>
    /// 阶段边界 — 描述一个等级区间。
    /// </summary>
    private sealed class PhaseBoundary
    {
        public int MinLevel { get; set; }
        public int MaxLevel { get; set; }
    }
}
