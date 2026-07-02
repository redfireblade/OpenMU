// <copyright file="CharacterBuildDna.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge;

/// <summary>
/// 角色攻击类型。
/// </summary>
public enum AttackType
{
    /// <summary>近战，需贴近到 2-3 格。</summary>
    Melee,

    /// <summary>远程，可在 6-8 格距离攻击。</summary>
    Ranged,
}

/// <summary>
/// AI 角色的完整「DNA」定义 — 决定角色的职业定位、加点策略、战斗方式和技能优先级。
/// 每个角色在创建时根据 classId 和 direction 选择一个 DNA，指导整个成长过程。
/// </summary>
/// <param name="ClassName">职业显示名，如「血牛战士」「智敏法师」。</param>
/// <param name="BaseClassNumber">基础职业编号 (0=DW, 4=DK, 8=Elf, 12=MG, 16=DL, 20=SUM, 24=RF)。</param>
/// <param name="Direction">发展方向枚举。</param>
/// <param name="AttackType">攻击类型：近战/远程。</param>
/// <param name="AttackRange">攻击距离（格），近战 2.5，远程 6.0。</param>
/// <param name="CoreSkillIds">核心技能编号列表（优先级由高到低），AI 按此顺序选择攻击技能。</param>
/// <param name="Description">一句话描述。</param>
/// <param name="Phases">各阶段加点方案（等级区间 + 四项权重）。</param>
public sealed record CharacterBuildDna(
    string ClassName,
    int BaseClassNumber,
    BuildDirection Direction,
    AttackType AttackType,
    float AttackRange,
    short[] CoreSkillIds,
    string Description,
    BuildPhase[] Phases)
{
    /// <summary>是否为远程职业。</summary>
    public bool IsRanged => this.AttackType == AttackType.Ranged;

    /// <summary>是否为近战职业。</summary>
    public bool IsMelee => this.AttackType == AttackType.Melee;
}

/// <summary>
/// 角色 DNA 仓库 — 所有职业的完整 DNA 定义。
/// </summary>
public static class CharacterBuildDnaRepository
{
    /// <summary>
    /// 获取指定基础职业的默认 DNA（第一个方向）。
    /// </summary>
    public static CharacterBuildDna GetDefault(int baseClassNumber)
    {
        var all = GetAll();
        return all.FirstOrDefault(d => d.BaseClassNumber == baseClassNumber)
               ?? all[0];
    }

    /// <summary>
    /// 获取所有职业的所有方向 DNA。
    /// </summary>
    public static CharacterBuildDna[] GetAll()
    {
        return new CharacterBuildDna[]
        {
            // ════════════════════════════════════════════════════════════════
            // 黑暗骑士 DK (4) — 战士
            // ════════════════════════════════════════════════════════════════
            new CharacterBuildDna(
                ClassName: "血牛战士",
                BaseClassNumber: 4,
                Direction: BuildDirection.BalancedKnight,
                AttackType: AttackType.Melee,
                AttackRange: 2.5f,
                CoreSkillIds: new short[] { 62, 63, 64 }, // 地裂斩, 升龙击, 生命之光
                Description: "新手首选，力量敏够穿装备，其余全体力，血厚防高不易死",
                Phases: new BuildPhase[]
                {
                    new BuildPhase(1, 40, 0.40f, 0.20f, 0.40f, 0.00f, "2力1敏2体"),
                    new BuildPhase(41, 70, 0.20f, 0.20f, 0.60f, 0.00f, "1力1敏3体"),
                    new BuildPhase(71, 400, 0.00f, 0.00f, 1.00f, 0.00f, "全加体力"),
                }),

            // ════════════════════════════════════════════════════════════════
            // 黑暗巫师 DW (0) — 法师
            // ════════════════════════════════════════════════════════════════
            new CharacterBuildDna(
                ClassName: "智敏法师",
                BaseClassNumber: 0,
                Direction: BuildDirection.IntWizard,
                AttackType: AttackType.Ranged,
                AttackRange: 6.0f,
                CoreSkillIds: new short[] { 74, 30, 56, 55, 57 }, // Fire Blast, 黑龙波, 陨石, 冰封, 毒
                Description: "绝对主流，智力加魔攻，敏捷加防御/施法速度，靠守护之魂保命",
                Phases: new BuildPhase[]
                {
                    new BuildPhase(1, 40, 0.00f, 0.40f, 0.00f, 0.60f, "3智2敏"),
                    new BuildPhase(41, 70, 0.00f, 0.20f, 0.00f, 0.80f, "4智1敏"),
                    new BuildPhase(71, 400, 0.00f, 0.20f, 0.00f, 0.80f, "4智1敏"),
                }),

            // ════════════════════════════════════════════════════════════════
            // 精灵 FE (8) — 弓箭手
            // ════════════════════════════════════════════════════════════════
            new CharacterBuildDna(
                ClassName: "敏弓",
                BaseClassNumber: 8,
                Direction: BuildDirection.AgilityElf,
                AttackType: AttackType.Ranged,
                AttackRange: 6.0f,
                CoreSkillIds: new short[] { 47, 48, 49, 50 }, // 多重箭, 冰封箭, 穿透箭, 召唤战宠
                Description: "敏捷是神(加攻击/防御/攻速)，力量仅够拿弓，单刷效率第一",
                Phases: new BuildPhase[]
                {
                    new BuildPhase(1, 40, 0.20f, 0.80f, 0.00f, 0.00f, "1力4敏"),
                    new BuildPhase(41, 70, 0.10f, 0.90f, 0.00f, 0.00f, "1力9敏"),
                    new BuildPhase(71, 400, 0.00f, 1.00f, 0.00f, 0.00f, "全敏"),
                }),
        };
    }
}
