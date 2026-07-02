// <copyright file="ShadowMapEntry.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

/// <summary>
/// 数字影子地图条目 — AI 角色个人地理信息系统的原子记录。
/// 每个条目记录一次有意义的游戏内事件，附带其在地图上的位置和上下文。
/// </summary>
public sealed class ShadowMapEntry
{
    /// <summary>条目唯一 ID（全局递增）。</summary>
    public long Id { get; set; }

    /// <summary>记录所属的 AI 角色名。</summary>
    public string CharacterName { get; set; } = string.Empty;

    /// <summary>条目类型。</summary>
    public ShadowEntryType EntryType { get; set; }

    /// <summary>地图编号。</summary>
    public ushort MapNumber { get; set; }

    /// <summary>坐标 X。</summary>
    public byte X { get; set; }

    /// <summary>坐标 Y。</summary>
    public byte Y { get; set; }

    /// <summary>关联的任务组号（如适用）。</summary>
    public short? QuestGroup { get; set; }

    /// <summary>关联的任务编号（如适用）。</summary>
    public short? QuestNumber { get; set; }

    /// <summary>记录的等级快照。</summary>
    public int Level { get; set; }

    /// <summary>平均物理攻击力。</summary>
    public double AvgPhysicalAttack { get; set; }

    /// <summary>平均魔法攻击力。</summary>
    public double AvgWizardryAttack { get; set; }

    /// <summary>防御力。</summary>
    public double Defense { get; set; }

    /// <summary>关联的怪物编号（如适用）。</summary>
    public short? MonsterNumber { get; set; }

    /// <summary>关联的怪物等级（如适用）。</summary>
    public short? MonsterLevel { get; set; }

    /// <summary>关联的怪物攻击力（如适用）。</summary>
    public double? MonsterAttack { get; set; }

    /// <summary>关联的怪物防御力（如适用）。</summary>
    public double? MonsterDefense { get; set; }

    /// <summary>关联的物品编号（掉落/拾取时）。</summary>
    public int? ItemGroup { get; set; }

    /// <summary>关联的物品子编号。</summary>
    public int? ItemNumber { get; set; }

    /// <summary>物品的 Excellent/Ancient/Luck 标记。</summary>
    public string? ItemFlags { get; set; }

    /// <summary>使用的技能编号列表（JSON数组）。</summary>
    public string? SkillNumbers { get; set; }

    /// <summary>自由文本说明。</summary>
    public string? Description { get; set; }

    /// <summary>记录时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>是否已被群体地图提取。</summary>
    public bool ExtractedToGlobal { get; set; }
}

/// <summary>
/// 影子地图条目类型。
/// </summary>
public enum ShadowEntryType
{
    /// <summary>死亡记录（含 3×3 区域坐标）。</summary>
    Death,

    /// <summary>掉落记录（含物品信息）。</summary>
    Drop,

    /// <summary>拾取记录。</summary>
    Pickup,

    /// <summary>任务活动记录。</summary>
    QuestActivity,

    /// <summary>升级记录。</summary>
    LevelUp,

    /// <summary>安全区/热点标记。</summary>
    Hotspot,

    /// <summary>危险区域标记。</summary>
    DangerZone,

    /// <summary>经济记录（物价/交易）。</summary>
    Economy,

    /// <summary>PK 记录。</summary>
    PkEvent,
}
