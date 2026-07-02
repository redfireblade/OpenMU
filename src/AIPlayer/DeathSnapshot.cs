// <copyright file="DeathSnapshot.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// 死亡快照 — AI 角色死亡时的完整状态记录。
/// 每次 3 连死后触发记录，写入数字地图记忆。
/// 用于后续恢复时对比 20% 属性提升。
/// </summary>
public sealed class DeathSnapshot
{
    /// <summary>任务组号。</summary>
    public short QuestGroup { get; set; }

    /// <summary>任务编号。</summary>
    public short QuestNumber { get; set; }

    /// <summary>死亡坐标 X。</summary>
    public byte DeathX { get; set; }

    /// <summary>死亡坐标 Y。</summary>
    public byte DeathY { get; set; }

    /// <summary>周围 3 格区域坐标列表（含死亡点）。</summary>
    public List<string> SurroundingTiles { get; set; } = new();

    /// <summary>死亡时的角色等级。</summary>
    public int Level { get; set; }

    /// <summary>最大 HP。</summary>
    public int MaxHp { get; set; }

    /// <summary>最大 MP。</summary>
    public int MaxMp { get; set; }

    /// <summary>平均物理攻击力 (min+max)/2。</summary>
    public double AvgPhysicalAttack { get; set; }

    /// <summary>平均魔法攻击力 (min+max)/2。</summary>
    public double AvgWizardryAttack { get; set; }

    /// <summary>基础防御力。</summary>
    public double Defense { get; set; }

    /// <summary>已使用的技能编号列表。</summary>
    public List<ushort> SkillNumbers { get; set; } = new();

    /// <summary>死亡目标怪物编号（如果有）。</summary>
    public short? MonsterNumber { get; set; }

    /// <summary>死亡目标怪物等级（如果有）。</summary>
    public short? MonsterLevel { get; set; }

    /// <summary>记录时间戳。</summary>
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 从 AiPlayer 捕获当前状态并生成快照。
    /// </summary>
    public static DeathSnapshot Capture(AiPlayer player, short questGroup, short questNumber, Point deathPos, short? monsterNumber, short? monsterLevel)
    {
        var attrs = player.Attributes;
        var x = deathPos.X;
        var y = deathPos.Y;

        var snapshot = new DeathSnapshot
        {
            QuestGroup = questGroup,
            QuestNumber = questNumber,
            DeathX = x,
            DeathY = y,
            Level = player.Level,
            MaxHp = (int)(attrs?[Stats.MaximumHealth] ?? 0f),
            MaxMp = (int)(attrs?[Stats.MaximumMana] ?? 0f),
            AvgPhysicalAttack = ((attrs?[Stats.MinimumPhysBaseDmg] ?? 0f) + (attrs?[Stats.MaximumPhysBaseDmg] ?? 0f)) / 2.0,
            AvgWizardryAttack = ((attrs?[Stats.MinimumWizBaseDmg] ?? 0f) + (attrs?[Stats.MaximumWizBaseDmg] ?? 0f)) / 2.0,
            Defense = attrs?[Stats.DefenseBase] ?? 0f,
            MonsterNumber = monsterNumber,
            MonsterLevel = monsterLevel,
        };

        // 周围 3 格坐标
        for (var dx = -3; dx <= 3; dx++)
        {
            for (var dy = -3; dy <= 3; dy++)
            {
                var nx = (byte)Math.Clamp(x + dx, 0, 255);
                var ny = (byte)Math.Clamp(y + dy, 0, 255);
                snapshot.SurroundingTiles.Add($"{nx},{ny}");
            }
        }

        // 已学习的技能
        if (player.SkillList?.Skills is not null)
        {
            foreach (var skill in player.SkillList.Skills)
            {
                if (skill.Skill is not null)
                    snapshot.SkillNumbers.Add((ushort)skill.Skill.Number);
            }
        }

        return snapshot;
    }

    /// <summary>
    /// 计算当前属性相对快照的提升百分比。
    /// 返回综合提升率（多属性加权平均）：等级20% + HP20% + 攻击30% + 防御30%。
    /// </summary>
    public double CalcImprovementPercent(AiPlayer player)
    {
        var attrs = player.Attributes;
        if (attrs is null) return 0;

        var nowLevel = player.Level;
        var nowMaxHp = (int)attrs[Stats.MaximumHealth];
        var nowAvgPhysAtk = (attrs[Stats.MinimumPhysBaseDmg] + attrs[Stats.MaximumPhysBaseDmg]) / 2.0;
        var nowAvgWizAtk = (attrs[Stats.MinimumWizBaseDmg] + attrs[Stats.MaximumWizBaseDmg]) / 2.0;
        var nowDef = attrs[Stats.DefenseBase];

        // 计算各属性提升比例（避免除零）
        var levelRatio = this.Level > 0 ? (double)(nowLevel - this.Level) / this.Level : 0;
        var hpRatio = this.MaxHp > 0 ? (double)(nowMaxHp - this.MaxHp) / this.MaxHp : 0;
        var atkRatio = this.AvgPhysicalAttack > 0
            ? (nowAvgPhysAtk - this.AvgPhysicalAttack) / this.AvgPhysicalAttack
            : (this.AvgWizardryAttack > 0 ? (nowAvgWizAtk - this.AvgWizardryAttack) / this.AvgWizardryAttack : 0);
        var defRatio = this.Defense > 0 ? (nowDef - this.Defense) / this.Defense : 0;

        // 加权综合提升率
        var weighted = (levelRatio * 0.20) + (hpRatio * 0.20) + (atkRatio * 0.30) + (defRatio * 0.30);

        return weighted * 100.0;
    }
}
