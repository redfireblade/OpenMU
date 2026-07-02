// <copyright file="ScriptGenerator.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.Evolution;

using System;
using System.Collections.Generic;
using System.Linq;
using MUnique.OpenMU.AIPlayer.Scripting;

/// <summary>
/// 行为脚本生成器 — 从 <see cref="BehaviorPatternAnalyzer"/> 的分析结果
/// （<see cref="AllPatternsResult"/>）合成 <see cref="BehaviorScript"/> JSON 格式的
/// AI 行为脚本。
///
/// 支持三种生成维度：
/// <list type="bullet">
///   <item><see cref="GenerateHuntingScript"/> — 狩猎脚本（战斗、走位、拾取）</item>
///   <item><see cref="GenerateRestockScript"/> — 补给脚本（回城、买卖、修理）</item>
///   <item><see cref="GenerateFromPatterns"/> — 从完整分析结果自动选择最佳脚本</item>
/// </list>
///
/// 生成的脚本保存在内存中，不写入文件系统。
/// </summary>
public sealed class ScriptGenerator
{
    /// <summary>
    /// 默认狩猎搜索范围（格数）。
    /// </summary>
    private const int DefaultSearchRange = 20;

    /// <summary>
    /// 高击杀密度阈值 — 超过此 KillCount 的狩猎模式会触发搜索范围扩大。
    /// </summary>
    private const int HighDensityKillThreshold = 50;

    /// <summary>
    /// 高密度时使用的搜索范围（格数）。
    /// </summary>
    private const int HighDensitySearchRange = 25;

    /// <summary>
    /// 紧急喝药 HP 阈值。
    /// </summary>
    private const float EmergencyHealThreshold = 0.2f;

    /// <summary>
    /// 技能使用的高置信度阈值 — 只有超过此值的技能才会被选入脚本。
    /// </summary>
    private const double HighConfidenceSkillThreshold = 0.5;

    /// <summary>
    /// 已知的 BUFF NPC 编号列表 — 这些NPC对话后能提供增益效果。
    /// </summary>
    private static readonly Dictionary<short, string> BuffNpcs = new()
    {
        { 257, "Elf Soldier" },
        { 419, "Elf Soldier (Lorencia)" },
    };

    /// <summary>
    /// 从所有模式分析结果生成完整脚本。
    /// 依次生成狩猎脚本、补给脚本和角色增强脚本，再合并所有节点。
    /// </summary>
    /// <param name="patterns">行为模式分析结果，包含七种模式列表。</param>
    /// <param name="playerName">来源玩家角色名称，会记录到返回脚本的 <see cref="GeneratedScript.SourcePlayer"/>。</param>
    /// <param name="playerLevel">来源玩家等级。</param>
    /// <returns>
    /// 合并后的 <see cref="GeneratedScript"/>，包含所有已学习的行为节点。
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="patterns"/> 为 null 时抛出。</exception>
    public GeneratedScript GenerateFromPatterns(AllPatternsResult patterns, string playerName, int playerLevel)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        var huntingScript = this.GenerateHuntingScript(patterns.Hunting, patterns.Skills, patterns.NpcInteractions, playerLevel);

        GeneratedScript? restockScript = null;
        if (patterns.Potions.Count > 0)
        {
            restockScript = this.GenerateRestockScript(patterns.Potions);
        }

        var enhancementScript = this.GenerateEnhancementScript(patterns, playerLevel);

        // 计算所有模式样本数之和
        var totalEvents = patterns.Hunting.Sum(p => p.KillCount)
            + patterns.Potions.Sum(p => p.SampleCount)
            + patterns.Skills.Sum(p => p.UseCount)
            + patterns.Navigation.Sum(p => p.Frequency);

        // 计算综合置信度：狩猎模式置信度的平均值，无狩猎模式时使用 0.5
        var confidence = patterns.Hunting.Count > 0
            ? patterns.Hunting.Average(p => p.Confidence)
            : 0.5;

        // 合并所有脚本的节点，去重
        var mergedChain = new List<PriorityNode>();
        var seenNodeNames = new HashSet<string>();
        foreach (var candidate in new[] { huntingScript, restockScript, enhancementScript })
        {
            if (candidate?.Script?.PriorityChain is null) continue;
            foreach (var node in candidate.Script.PriorityChain)
            {
                if (seenNodeNames.Add(node.Name))
                {
                    mergedChain.Add(node);
                }
            }
        }

        var mergedScript = new BehaviorScript
        {
            Id = $"learned_{playerName}_{playerLevel}",
            Name = $"Auto-generated script (Lv{playerLevel})",
            Version = "1.0.0",
            Status = "draft",
            PriorityChain = mergedChain,
            Parameters = huntingScript.Script.Parameters,
        };

        return new GeneratedScript
        {
            Script = mergedScript,
            SourcePlayer = playerName,
            Confidence = confidence,
            TotalEvents = totalEvents,
            GeneratedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// 生成狩猎脚本（含战斗节点、走位、拾取、NPC BUFF）。
    ///
    /// 优先链节点按以下顺序排列：
    ///   get_npc_buff（如有NPC交互学习）→ emergency_heal → skillAttack → combat → pickup → patrol → relocate
    ///
    /// 如果狩猎模式中有 KillCount 最高的热点，会添加 relocate 节点和对应的 HotspotDef。
    /// </summary>
    /// <param name="hunting">狩猎模式列表，用于推断热点坐标和搜索范围。可为空。</param>
    /// <param name="skills">技能使用模式列表，用于推断高置信度技能。可为空。</param>
    /// <param name="npcInteractions">NPC 交互模式列表，用于推断 BUFF NPC。可为空。</param>
    /// <param name="playerLevel">玩家等级，用于命名脚本。</param>
    /// <returns>
    /// 生成的 <see cref="GeneratedScript"/>，包含完整的 <see cref="BehaviorScript"/>。
    /// 即使输入为空也返回一个带默认参数的脚本。
    /// </returns>
    public GeneratedScript GenerateHuntingScript(List<HuntingPattern> hunting, List<SkillPattern> skills, List<NpcInteractionPattern>? npcInteractions, int playerLevel)
    {
        // ── 搜索范围：若存在高击杀密度的地图则扩大 ──
        var searchRange = DefaultSearchRange;
        if (hunting is { Count: > 0 } && hunting.Any(p => p.KillCount > HighDensityKillThreshold))
        {
            searchRange = HighDensitySearchRange;
        }

        // ── 构建优先链 ──
        var chain = new List<PriorityNode>();

        // 0. get_npc_buff — 从 NPC 交互模式中检测玩家通常在狩猎前找哪个 NPC 加 BUFF
        var buffNpc = DetectBestBuffNpc(npcInteractions);
        if (buffNpc.HasValue)
        {
            chain.Add(new PriorityNode
            {
                Name = "get_npc_buff",
                Condition = "not_buffed",
                Action = "walk_to_quest_npc",
                Parameters = new ScriptParameters
                {
                    QuestNpcNumber = buffNpc.Value,
                },
            });
        }

        // 1. emergency_heal — HP 过低时紧急喝药
        chain.Add(new PriorityNode
        {
            Name = "emergency_heal",
            Condition = "hp_below_threshold",
            Action = "use_hp_potion",
            Parameters = new ScriptParameters
            {
                HpThreshold = EmergencyHealThreshold,
            },
        });

        // 2. skillAttack — 若有高置信度技能，优先使用技能攻击（置于普通 combat 之前）
        var bestSkill = skills?
            .Where(s => s.Confidence > HighConfidenceSkillThreshold)
            .OrderByDescending(s => s.UseCount)
            .FirstOrDefault();

        if (bestSkill is not null)
        {
            chain.Add(new PriorityNode
            {
                Name = "skillAttack",
                Condition = "has_target",
                Action = "attack_target",
                Parameters = new ScriptParameters
                {
                    SkillNumber = bestSkill.SkillNumber,
                },
            });
        }

        // 3. combat — 有目标时普通攻击
        chain.Add(new PriorityNode
        {
            Name = "combat",
            Condition = "has_target",
            Action = "attack_target",
        });

        // 4. pickup — 拾取附近物品
        chain.Add(new PriorityNode
        {
            Name = "pickup",
            Condition = "always",
            Action = "pickup_nearby",
        });

        // 5. patrol — 随机巡逻
        chain.Add(new PriorityNode
        {
            Name = "patrol",
            Condition = "always",
            Action = "random_patrol",
        });

        // ── 最佳狩猎热点 ──
        HotspotDef? hotspot = null;
        var bestSpot = hunting?
            .OrderByDescending(p => p.KillCount)
            .FirstOrDefault();

        if (bestSpot is not null)
        {
            hotspot = new HotspotDef
            {
                X = bestSpot.X,
                Y = bestSpot.Y,
                MapNumber = (ushort)bestSpot.MapNumber,
                Name = $"auto_hotspot_{bestSpot.MonsterName ?? $"m{bestSpot.MonsterNumber}"}",
            };

            // 6. relocate — 无怪时换点
            chain.Add(new PriorityNode
            {
                Name = "relocate",
                Condition = "no_monsters_recently",
                Action = "relocate_to_hotspot",
            });
        }

        // ── 构建 BehaviorScript ──
        var script = new BehaviorScript
        {
            Id = $"learned_hunt_{playerLevel}",
            Name = $"Auto-generated hunting script (Lv{playerLevel})",
            Version = "1.0.0",
            Status = "draft",
            PriorityChain = chain,
            Parameters = new ScriptParameters
            {
                SearchRange = searchRange,
                Hotspots = hotspot is not null ? new List<HotspotDef> { hotspot } : null,
            },
        };

        // ── 置信度 ──
        var confidence = hunting is { Count: > 0 }
            ? hunting.Average(p => p.Confidence)
            : 0.5;

        return new GeneratedScript
        {
            Script = script,
            SourcePlayer = string.Empty,
            Confidence = confidence,
            GeneratedAt = DateTime.UtcNow,
            TotalEvents = hunting?.Sum(p => p.KillCount) ?? 0,
        };
    }

    /// <summary>
    /// 生成补给脚本（含回城、买卖、修理）。
    ///
    /// 优先链节点按以下顺序排列：
    ///   restock → heal（如有 HP 药水阈值信息）
    /// </summary>
    /// <param name="potions">药水使用模式列表。可为空。</param>
    /// <returns>
    /// 生成的 <see cref="GeneratedScript"/>，包含回城售卖和药水补给逻辑。
    /// 输入为空时返回一个仅包含回城售卖的默认脚本。
    /// </returns>
    public GeneratedScript GenerateRestockScript(List<PotionPattern> potions)
    {
        // ── 构建优先链 ──
        var chain = new List<PriorityNode>();

        // 1. restock — 背包满时回城售卖
        chain.Add(new PriorityNode
        {
            Name = "restock",
            Condition = "inventory_full",
            Action = "return_and_sell",
        });

        // 2. heal — 若药水模式中有非零 HP 阈值，添加喝药节点
        var hpPattern = potions?
            .Where(p => p.IsHpPotion && p.ThresholdPercent > 0.001)
            .OrderByDescending(p => p.Confidence)
            .FirstOrDefault();

        if (hpPattern is not null)
        {
            var threshold = (float)Math.Clamp(hpPattern.ThresholdPercent, 0.05, 0.95);

            chain.Add(new PriorityNode
            {
                Name = "heal",
                Condition = "hp_below_threshold",
                Action = "use_hp_potion",
                Parameters = new ScriptParameters
                {
                    HpThreshold = threshold,
                },
            });
        }

        // ── 构建 BehaviorScript ──
        var script = new BehaviorScript
        {
            Id = "learned_restock",
            Name = "Auto-generated restock script",
            Version = "1.0.0",
            Status = "draft",
            PriorityChain = chain,
            Parameters = new ScriptParameters(),
        };

        // ── 置信度 ──
        var confidence = potions is { Count: > 0 }
            ? potions.Average(p => p.Confidence)
            : 0.5;

        return new GeneratedScript
        {
            Script = script,
            SourcePlayer = string.Empty,
            Confidence = confidence,
            GeneratedAt = DateTime.UtcNow,
            TotalEvents = potions?.Sum(p => p.SampleCount) ?? 0,
        };
    }

    /// <summary>
    /// 生成角色增强脚本（含装备穿戴、加点、学技能、买药水）。
    /// 节点按以下顺序排列：
    ///   allocate_stats → learn_skill → equip_best_item → buy_potions
    /// 这些节点优先于战斗节点执行，确保角色做好战斗准备。
    /// </summary>
    /// <param name="patterns">所有模式分析结果。</param>
    /// <param name="playerLevel">玩家等级。</param>
    /// <returns>角色增强脚本。</returns>
    public GeneratedScript GenerateEnhancementScript(AllPatternsResult patterns, int playerLevel)
    {
        var chain = new List<PriorityNode>();

        // 1. allocate_stats — 有属性点时自动加点
        if (patterns.StatAllocation.Count > 0)
        {
            var best = patterns.StatAllocation.OrderByDescending(s => s.SamplePoints).First();
            chain.Add(new PriorityNode
            {
                Name = "allocate_stats",
                Condition = "has_stat_points",
                Action = "allocate_attributes",
                Parameters = new ScriptParameters
                {
                    StrWeight = (float)best.StrWeight,
                    AgiWeight = (float)best.AgiWeight,
                    VitWeight = (float)best.VitWeight,
                    EneWeight = (float)best.EneWeight,
                },
            });
        }

        // 2. learn_skill — 有技能书时学习技能
        chain.Add(new PriorityNode
        {
            Name = "learn_skill",
            Condition = "has_skillbook",
            Action = "learn_skill_from_book",
        });

        // 3. equip_best_item — 有可装备物品时穿上
        if (patterns.Equipment.Count > 0)
        {
            chain.Add(new PriorityNode
            {
                Name = "equip_best_item",
                Condition = "has_item_in_inventory",
                Action = "equip_best_in_slot",
            });
        }

        // ── 构建 BehaviorScript ──
        var script = new BehaviorScript
        {
            Id = $"learned_enhance_{playerLevel}",
            Name = $"Auto-generated enhancement script (Lv{playerLevel})",
            Version = "1.0.0",
            Status = "draft",
            PriorityChain = chain,
            Parameters = new ScriptParameters(),
        };

        return new GeneratedScript
        {
            Script = script,
            SourcePlayer = string.Empty,
            Confidence = 0.5,
            GeneratedAt = DateTime.UtcNow,
            TotalEvents = patterns.StatAllocation.Sum(s => s.SamplePoints) + patterns.Equipment.Count,
        };
    }

    /// <summary>
    /// 从 NPC 交互模式中检测最佳 BUFF NPC。
    /// 优先选择有 BuffReceived 事件的高置信度 NPC，其次选择 KnownBuffNpcs。
    /// </summary>
    /// <param name="npcInteractions">NPC 交互模式列表。</param>
    /// <returns>检测到的 BUFF NPC 编号，无匹配时返回 null。</returns>
    private static short? DetectBestBuffNpc(List<NpcInteractionPattern>? npcInteractions)
    {
        if (npcInteractions is null || npcInteractions.Count == 0)
        {
            // 即使没有行为数据，也默认尝试找已知的 BUFF NPC
            return 257; // Elf Soldier
        }

        // 1. 优先选有 BuffReceived 交互的高置信度 NPC
        var buffPattern = npcInteractions
            .Where(n => n.InteractionType == NpcInteractionType.Buff && n.Confidence > 0.3)
            .OrderByDescending(n => n.Frequency)
            .FirstOrDefault();

        if (buffPattern is not null)
        {
            return buffPattern.NpcNumber;
        }

        // 2. 其次选已知 BUFF NPC 列表中有对话记录（Talk）的
        var talkPattern = npcInteractions
            .Where(n => BuffNpcs.ContainsKey(n.NpcNumber) && n.Confidence > 0.3)
            .OrderByDescending(n => n.Frequency)
            .FirstOrDefault();

        if (talkPattern is not null)
        {
            return talkPattern.NpcNumber;
        }

        // 3. 默认 Elf Soldier
        return 257;
    }
}

/// <summary>
/// 生成的脚本记录 — 包含 <see cref="BehaviorScript"/> 和元数据（来源玩家、置信度等）。
/// 由 <see cref="ScriptGenerator"/> 产生并返回，不写入文件系统。
/// </summary>
public record GeneratedScript
{
    /// <summary>可执行的 BehaviorScript。由 <see cref="ScriptGenerator"/> 根据模式分析结果填充节点和参数。</summary>
    public BehaviorScript Script { get; init; } = new();

    /// <summary>来源玩家角色名称（即从哪个真实玩家学到的行为模式）。空字符串表示非玩家来源。</summary>
    public string SourcePlayer { get; init; } = string.Empty;

    /// <summary>
    /// 脚本综合置信度 0~1。
    /// 狩猎脚本置信度 = hunting 模式置信度平均值，无狩猎模式时默认为 0.5。
    /// 由 <see cref="ScriptGenerator.GenerateFromPatterns"/> 在返回前覆盖设置。
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>脚本生成时间（UTC）。</summary>
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// 所有模式样本数之和（KillCount + SampleCount + UseCount + Frequency）。
    /// 反映分析结果的数据量大小。
    /// </summary>
    public int TotalEvents { get; init; }
}
