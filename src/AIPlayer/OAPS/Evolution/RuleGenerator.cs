// <copyright file="RuleGenerator.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.Evolution;

using System;
using System.Collections.Generic;
using System.Linq;
using MUnique.OpenMU.AIPlayer.Decision;

/// <summary>
/// 规则生成器 — 从 BehaviorPattern 的分析结果生成 scripts-rules.json 兼容的规则条目。
///
/// 输入：<see cref="AllPatternsResult"/>（由 <see cref="BehaviorPatternAnalyzer"/> 产出）。
/// 输出：<see cref="GeneratedRule"/> 列表，可按置信度排序，可通过 <see cref="GeneratedRule.ToRuleDef"/>
/// 转换为 <see cref="RuleDef"/> 加载到 <see cref="RuleEngine"/>。
///
/// 三种规则生成维度：
/// <list type="bullet">
///   <item><see cref="GeneratePotionRules"/> — 药水阈值 → 生存规则</item>
///   <item><see cref="GenerateSkillRules"/> — 技能偏好 → 技能选择规则</item>
///   <item><see cref="GenerateHuntingRules"/> — 狩猎热点 → 狩猎推荐规则</item>
/// </list>
/// </summary>
public sealed class RuleGenerator
{
    /// <summary>
    /// 当 PotionPattern 无法提供有效阈值时的默认 HP 百分比阈值。
    /// 来自 BehaviorPatternAnalyzer 的 PotionBought 事件不包含血量信息，
    /// 因此当所有模式阈值为 0 时使用此默认值。
    /// </summary>
    private const double DefaultHpThreshold = 0.4;

    /// <summary>
    /// 从所有模式分析结果生成规则列表。
    /// 依次执行药水、技能、狩猎三种维度的规则生成，合并后按置信度降序排列，
    /// 并在每条规则上记录来源玩家名称。
    /// </summary>
    /// <param name="patterns">行为模式分析结果，包含六种模式列表。</param>
    /// <param name="playerName">来源玩家角色名称，会记录到每条规则的 <see cref="GeneratedRule.SourcePlayer"/>。</param>
    /// <param name="playerLevel">来源玩家等级（当前可用于狩猎规则等级门控）。</param>
    /// <returns>
    /// 生成的规则列表，按置信度降序排列。
    /// 如果所有模式列表均为空，返回空列表。
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="patterns"/> 为 null 时抛出。</exception>
    public List<GeneratedRule> GenerateFromPatterns(AllPatternsResult patterns, string playerName, int playerLevel)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        var potionRules = this.GeneratePotionRules(patterns.Potions);
        var skillRules = this.GenerateSkillRules(patterns.Skills);
        var huntingRules = this.GenerateHuntingRules(patterns.Hunting);

        var allRules = potionRules
            .Concat(skillRules)
            .Concat(huntingRules)
            .Select(r => r with
            {
                SourcePlayer = playerName,

                // Potion 规则的 ID 唯一标识需要包含玩家名（同一玩家产生的多批规则可覆盖）
                Id = r.Id.StartsWith("learned_heal", StringComparison.Ordinal) && !r.Id.Contains(playerName, StringComparison.Ordinal)
                    ? $"learned_heal_{playerName}"
                    : r.Id,
            })
            .OrderByDescending(r => r.Confidence)
            .ToList();

        return allRules;
    }

    /// <summary>
    /// 从狩猎模式生成战斗相关规则。
    /// 按 <see cref="HuntingPattern.KillCount"/> 降序取前 5 条，
    /// 每条生成一条推荐狩猎点规则。
    /// </summary>
    /// <param name="hunting">狩猎模式列表，通常来自 <see cref="AllPatternsResult.Hunting"/>。</param>
    /// <returns>
    /// 生成的狩猎推荐规则列表，最多 5 条。输入为 null 或空时返回空列表。
    /// 规则优先级固定为 50（高于兜底生存但低于主动技能）。
    /// </returns>
    public List<GeneratedRule> GenerateHuntingRules(List<HuntingPattern>? hunting)
    {
        if (hunting is null || hunting.Count == 0)
        {
            return new List<GeneratedRule>(0);
        }

        return hunting
            .OrderByDescending(p => p.KillCount)
            .Take(5)
            .Select(p =>
            {
                var monsterLabel = !string.IsNullOrEmpty(p.MonsterName)
                    ? p.MonsterName
                    : $"monster{p.MonsterNumber}";
                var mapLabel = p.MapNumber;

                return new GeneratedRule
                {
                    Id = $"learned_hunt_{monsterLabel}_{mapLabel}",
                    Condition = $"on_map_{p.MapNumber} AND level >= {p.MonsterLevel}",
                    Priority = 50,
                    ScriptId = $"hunt_spot_{p.MapNumber}_{p.X}_{p.Y}",
                    Description = $"Hunt {monsterLabel} at map {mapLabel} area ({p.X}, {p.Y})",
                    Confidence = p.Confidence,
                };
            })
            .ToList();
    }

    /// <summary>
    /// 从药水模式生成生存规则。
    /// 从 <see cref="PotionPattern"/> 列表中筛选 HP 药水模式，
    /// 取其阈值（<see cref="PotionPattern.ThresholdPercent"/>）的平均值作为规则条件。
    ///
    /// 注意：<see cref="BehaviorPatternAnalyzer"/> 从 <see cref="BehaviorEventType.PotionBought"/>
    /// 事件提取的模式不包含实际喝药时的血量阈值（该事件不记录 HP 信息），
    /// 因此当所有模式阈值为 0 时，使用 <see cref="DefaultHpThreshold"/> (0.4) 作为默认值。
    /// 调用方可通过 <see cref="BehaviorRecordExport.InferredHpPotionThreshold"/> 获取更精确的阈值。
    /// </summary>
    /// <param name="potions">药水使用模式列表，通常来自 <see cref="AllPatternsResult.Potions"/>。</param>
    /// <returns>
    /// 生成的生存规则列表，最多 1 条（HP 药水规则）。
    /// 如果未找到 HP 药水模式，返回空列表。
    /// 规则优先级固定为 2（仅次于内置 survival_hp 规则的优先级 1）。
    /// </returns>
    public List<GeneratedRule> GeneratePotionRules(List<PotionPattern>? potions)
    {
        if (potions is null || potions.Count == 0)
        {
            return new List<GeneratedRule>(0);
        }

        var hpPatterns = potions.Where(p => p.IsHpPotion).ToList();
        if (hpPatterns.Count == 0)
        {
            return new List<GeneratedRule>(0);
        }

        // 优先使用非零阈值的模式计算平均值
        var nonZeroThresholds = hpPatterns
            .Where(p => p.ThresholdPercent > 0.001)
            .ToList();

        double avgThreshold;
        double avgConfidence;

        if (nonZeroThresholds.Count > 0)
        {
            avgThreshold = nonZeroThresholds.Average(p => p.ThresholdPercent);
            avgConfidence = nonZeroThresholds.Average(p => p.Confidence);
        }
        else
        {
            // 所有阈值均为 0，使用默认值
            avgThreshold = DefaultHpThreshold;
            avgConfidence = hpPatterns.Average(p => p.Confidence);
        }

        // 钳制到有效范围
        avgThreshold = Math.Clamp(avgThreshold, 0.05, 0.95);

        return new List<GeneratedRule>(1)
        {
            new GeneratedRule
            {
                Id = "learned_heal",
                Condition = $"HP_PCT < {avgThreshold:F2}",
                Priority = 2,
                ScriptId = "use_hp_potion",
                Description = $"Use HP potion when HP drops below {avgThreshold:P0}",
                Confidence = avgConfidence,
            },
        };
    }

    /// <summary>
    /// 从技能模式生成技能选择规则。
    /// 按 <see cref="SkillPattern.UseCount"/> 降序取前 3 个最常用技能，
    /// 每条生成一条技能选择规则。
    /// </summary>
    /// <param name="skills">技能使用模式列表，通常来自 <see cref="AllPatternsResult.Skills"/>。</param>
    /// <returns>
    /// 生成的技能选择规则列表，最多 3 条。
    /// 输入为 null 或空时返回空列表。
    /// 规则优先级固定为 10。
    /// </returns>
    public List<GeneratedRule> GenerateSkillRules(List<SkillPattern>? skills)
    {
        if (skills is null || skills.Count == 0)
        {
            return new List<GeneratedRule>(0);
        }

        return skills
            .OrderByDescending(p => p.UseCount)
            .Take(3)
            .Select(p => new GeneratedRule
            {
                Id = $"learned_skill_{p.SkillNumber}",
                Condition = "has_target",
                Priority = 10,
                ScriptId = $"use_skill_{p.SkillNumber}",
                Description = !string.IsNullOrEmpty(p.SkillName)
                    ? $"Use skill #{p.SkillNumber} ({p.SkillName}) when target is present"
                    : $"Use skill #{p.SkillNumber} when target is present",
                Confidence = p.Confidence,
            })
            .ToList();
    }
}

/// <summary>
/// 生成的规则条目 — 可直接序列化为 JSON 或通过 <see cref="ToRuleDef"/> 加载到 <see cref="RuleEngine"/>。
///
/// 与 <see cref="RuleDef"/> 的区别：
/// <list type="bullet">
///   <item>使用更简化的字段集（不含 MinLevel/MaxLevel/RequiredClass 等基础设施门控）</item>
///   <item>包含 <see cref="SourcePlayer"/> 标记知识来源</item>
///   <item>Confidence 使用 double 而非 float（与模式分析精度一致）</item>
/// </list>
/// </summary>
public record GeneratedRule
{
    /// <summary>
    /// 规则唯一标识，如 "learned_heal_Player1", "learned_skill_18"。
    /// 同一 ID 可被后续更高置信度的规则覆盖（去重机制参考 <see cref="RuleEngine.TryLoadExperienceRules"/>）。
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// 条件表达式 — 由 <see cref="RuleEngine"/> 的条件评估器解析。
    /// 当前支持的条件关键字见 <see cref="RuleEngine"/> 的 MeetsCondition 方法。
    /// 生成规则可能使用扩展条件格式（如 "HP_PCT &lt; 0.3"），由后续版本支持。
    /// </summary>
    public string Condition { get; init; } = string.Empty;

    /// <summary>
    /// 优先级 — 越小越优先。
    /// 参考优先级区间：
    /// <list type="bullet">
    ///   <item>1：生存（内置 hp_low）</item>
    ///   <item>2：学习药水规则</item>
    ///   <item>10：学习技能规则</item>
    ///   <item>50：学习狩猎推荐</item>
    ///   <item>90：兜底生存（内置 survival_farm）</item>
    /// </list>
    /// </summary>
    public int Priority { get; init; }

    /// <summary>
    /// 条件满足时要调用的脚本 ID。
    /// 例如 "use_hp_potion", "use_skill_18", "hunt_spot_0_100_100"。
    /// </summary>
    public string ScriptId { get; init; } = string.Empty;

    /// <summary>
    /// 规则的可读说明。
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// 置信度 0~1，来源于生成该规则的行为模式置信度。
    /// 用于规则排序和低置信度过滤（参考 <see cref="RuleEngine.TryLoadExperienceRules"/> 的阈值检查）。
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// 从哪个真实玩家角色学到的此规则。
    /// 用于溯源和跨角色知识共享审计。
    /// </summary>
    public string SourcePlayer { get; init; } = string.Empty;

    /// <summary>
    /// 转换为 <see cref="RuleDef"/> 以便加载到 <see cref="RuleEngine"/>。
    /// 自动设置 <see cref="RuleDef.AutoGenerated"/> = true，
    /// 并根据 ScriptId 推断规则分类（Category）。
    /// </summary>
    /// <returns>与当前规则等价的 <see cref="RuleDef"/> 实例。</returns>
    public RuleDef ToRuleDef()
    {
        var category = InferCategory();
        return new RuleDef(
            RuleId: this.Id,
            Priority: this.Priority,
            Category: category,
            Condition: this.Condition,
            ScriptId: this.ScriptId,
            MinLevel: 0,
            MaxLevel: 0,
            RequiredClass: null,
            ItemRequired: null,
            Description: this.Description ?? string.Empty,
            Parameters: null,
            AutoGenerated: true,
            Confidence: (float)this.Confidence);
    }

    /// <summary>
    /// 根据 ScriptId 推断规则分类。
    /// </summary>
    private string InferCategory()
    {
        if (this.ScriptId.StartsWith("use_hp_potion", StringComparison.Ordinal))
        {
            return "survival";
        }

        if (this.ScriptId.StartsWith("use_skill_", StringComparison.Ordinal))
        {
            return "skill";
        }

        if (this.ScriptId.StartsWith("hunt_spot_", StringComparison.Ordinal))
        {
            return "survival";
        }

        return "survival";
    }
}
