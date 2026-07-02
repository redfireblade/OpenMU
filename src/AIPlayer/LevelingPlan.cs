// <copyright file="LevelingPlan.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

/// <summary>
/// 结构化升级计划 — 从真实玩家的行为事件中分析生成的升级策略。
/// 包含分阶段的完整升级路线，AI 角色可按此计划自主执行。
///
/// 示例计划（法师 Lv1→21）：
///   Phase: 入门期 (Lv1-5)
///     Step: 离开安全区 → 到下方出口找 soldier NPC 对话选1获得BUFF
///     Step: 到大陆下方击杀蜘蛛和幼龙升级到5级
///   Phase: 准备期 (Lv5)
///     Step: 回城 → 左上教堂NPC购买火球术技能书
///     Step: 在背包中使用技能书学习
///     Step: 左边出口小女孩NPC购买红药×5 蓝药×3
///     Step: 钱够则在教堂NPC购买最低级法杖和装备
///     Step: 装备武器和防具
///     Step: 加点优先：智力到40→力量够装备→10级加防御
///   Phase: 发展期 (Lv5-21)
///     Step: 回到地图下方用火球术打怪
/// </summary>
public sealed class LevelingPlan
{
    /// <summary>计划针对的角色名。</summary>
    public string CharacterName { get; set; } = string.Empty;

    /// <summary>职业编号。</summary>
    public int CharacterClass { get; set; }

    /// <summary>职业名称。</summary>
    public string? ClassName { get; set; }

    /// <summary>观察到的最高等级。</summary>
    public int MaxObservedLevel { get; set; }

    /// <summary>升级阶段列表。</summary>
    public List<LevelingPhase> Phases { get; set; } = new();

    /// <summary>全局加点策略（描述性）。</summary>
    public string? StatAllocationStrategy { get; set; }

    /// <summary>加点优先级列表（有序）。</summary>
    public List<StatPriority> StatPriorities { get; set; } = new();

    /// <summary>计划生成时间。</summary>
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 升级阶段 — 一个连续的等级范围+一组有序行为步骤。
/// </summary>
public sealed class LevelingPhase
{
    /// <summary>阶段名称（如"入门期"、"准备期"）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>起始等级。</summary>
    public int MinLevel { get; set; }

    /// <summary>结束等级。</summary>
    public int MaxLevel { get; set; }

    /// <summary>阶段描述。</summary>
    public string? Description { get; set; }

    /// <summary>有序步骤列表。</summary>
    public List<PhaseStep> Steps { get; set; } = new();

    /// <summary>本阶段推荐击杀的怪物。</summary>
    public List<RecommendedMonster> TargetMonsters { get; set; } = new();

    /// <summary>推荐狩猎地图编号。</summary>
    public int? HuntingMapNumber { get; set; }

    /// <summary>推荐狩猎地图名称。</summary>
    public string? HuntingMapName { get; set; }
}

/// <summary>
/// 阶段步骤 — 一个具体的可执行动作。
/// </summary>
public sealed class PhaseStep
{
    /// <summary>步骤类型。</summary>
    public StepType Type { get; set; }

    /// <summary>步骤描述。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>NPC名称（如涉及NPC交互）。</summary>
    public string? NpcName { get; set; }

    /// <summary>NPC编号。</summary>
    public short? NpcNumber { get; set; }

    /// <summary>对话框选项编号。</summary>
    public int? DialogChoice { get; set; }

    /// <summary>对话框选项描述。</summary>
    public string? DialogChoiceDescription { get; set; }

    /// <summary>物品名称。</summary>
    public string? ItemName { get; set; }

    /// <summary>物品编号 (Group, Number)。</summary>
    public (int Group, int Number)? ItemId { get; set; }

    /// <summary>购买数量。</summary>
    public int? Quantity { get; set; }

    /// <summary>技能名称。</summary>
    public string? SkillName { get; set; }

    /// <summary>技能编号。</summary>
    public ushort? SkillNumber { get; set; }

    /// <summary>目标地图编号。</summary>
    public int? MapNumber { get; set; }

    /// <summary>目标地图名称。</summary>
    public string? MapName { get; set; }

    /// <summary>目标坐标 X。</summary>
    public byte? TargetX { get; set; }

    /// <summary>目标坐标 Y。</summary>
    public byte? TargetY { get; set; }

    /// <summary>属性目标值（加点用）。</summary>
    public int? TargetStatValue { get; set; }
}

/// <summary>
/// 步骤类型枚举。
/// </summary>
public enum StepType
{
    /// <summary>前往某地图。</summary>
    GoToMap,

    /// <summary>移动到坐标。</summary>
    MoveTo,

    /// <summary>与NPC对话。</summary>
    TalkToNpc,

    /// <summary>选择对话框选项。</summary>
    SelectDialogOption,

    /// <summary>从NPC购买物品。</summary>
    BuyFromNpc,

    /// <summary>学习技能（使用技能书）。</summary>
    LearnSkill,

    /// <summary>装备物品。</summary>
    EquipItem,

    /// <summary>分配属性点。</summary>
    AllocateStat,

    /// <summary>击杀怪物。</summary>
    KillMonster,

    /// <summary>喝药水。</summary>
    UsePotion,

    /// <summary>修理装备。</summary>
    RepairEquipment,

    /// <summary>卖垃圾物品。</summary>
    SellJunk,
}

/// <summary>
/// 加点优先级。
/// </summary>
public sealed class StatPriority
{
    /// <summary>属性名（Strength/Agility/Vitality/Energy）。</summary>
    public string StatName { get; set; } = string.Empty;

    /// <summary>优先级（1=最高）。</summary>
    public int Priority { get; set; }

    /// <summary>目标值（达到此值后停止加此属性）。</summary>
    public int? TargetValue { get; set; }

    /// <summary>加此属性的原因。</summary>
    public string? Reason { get; set; }
}

/// <summary>
/// 推荐击杀的怪物。
/// </summary>
public sealed class RecommendedMonster
{
    /// <summary>怪物编号。</summary>
    public short MonsterNumber { get; set; }

    /// <summary>怪物名称。</summary>
    public string? MonsterName { get; set; }

    /// <summary>怪物等级。</summary>
    public short MonsterLevel { get; set; }

    /// <summary>击杀数。</summary>
    public long KillCount { get; set; }
}
