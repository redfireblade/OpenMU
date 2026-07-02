// <copyright file="BehaviorEvent.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using System.Text.Json.Serialization;

/// <summary>
/// 玩家行为事件类型 — 描述真实玩家在游戏中的一个离散动作。
/// 区别于 PBO 的周期性快照，BehaviorEvent 记录的是"发生了什么"而非"当前状态是什么"。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BehaviorEventType
{
    /// <summary>与 NPC 对话（打开对话框）。</summary>
    NpcTalk,

    /// <summary>选择了 NPC 对话框选项（如 "选1获得BUFF"）。</summary>
    NpcDialogChoice,

    /// <summary>从 NPC 商店购买了物品。</summary>
    ItemBought,

    /// <summary>从 NPC 商店购买了药水。</summary>
    PotionBought,

    /// <summary>从 NPC 商店购买了武器/装备。</summary>
    EquipmentBought,

    /// <summary>学习了技能（从技能书）。</summary>
    SkillLearned,

    /// <summary>装备了物品（背包→装备栏）。</summary>
    ItemEquipped,

    /// <summary>分配了属性点。</summary>
    StatAllocated,

    /// <summary>在地图某位置击杀怪物。</summary>
    MonsterKilled,

    /// <summary>拾取了物品。</summary>
    ItemPickedUp,

    /// <summary>到达新等级。</summary>
    LevelUp,

    /// <summary>访问了新地图。</summary>
    MapEntered,

    /// <summary>从 NPC 处接受了增益 BUFF。</summary>
    BuffReceived,
}

/// <summary>
/// 玩家行为事件 — 记录真实玩家的离散游戏动作。
/// 每个事件对应一次有意义的玩家操作，带完整的上下文信息。
/// 由 PlayerBehaviorObserver 在检测到行为时创建。
/// </summary>
public sealed class BehaviorEvent
{
    /// <summary>行为事件类型。</summary>
    public BehaviorEventType EventType { get; set; }

    /// <summary>角色名。</summary>
    public string CharacterName { get; set; } = string.Empty;

    /// <summary>角色职业编号 (0=DW, 4=DK, 8=ELF)。</summary>
    public int CharacterClass { get; set; }

    /// <summary>事件发生时的等级。</summary>
    public int Level { get; set; }

    /// <summary>地图编号。</summary>
    public int MapNumber { get; set; }

    /// <summary>地图名称。</summary>
    public string? MapName { get; set; }

    /// <summary>坐标 X。</summary>
    public byte X { get; set; }

    /// <summary>坐标 Y。</summary>
    public byte Y { get; set; }

    /// <summary>时间戳。</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    // ═══════════════════════════════════════════════
    // NPC 交互上下文
    // ═══════════════════════════════════════════════

    /// <summary>NPC 名称。</summary>
    public string? NpcName { get; set; }

    /// <summary>NPC 编号。</summary>
    public short? NpcNumber { get; set; }

    /// <summary>对话框选项编号（如 1 = 获得BUFF）。</summary>
    public int? DialogChoice { get; set; }

    /// <summary>对话框选项的描述。</summary>
    public string? DialogChoiceDescription { get; set; }

    // ═══════════════════════════════════════════════
    // 物品上下文
    // ═══════════════════════════════════════════════

    /// <summary>物品名称。</summary>
    public string? ItemName { get; set; }

    /// <summary>物品组 (Group)。</summary>
    public int? ItemGroup { get; set; }

    /// <summary>物品编号。</summary>
    public int? ItemNumber { get; set; }

    /// <summary>物品价格（购买时）。</summary>
    public int? ItemPrice { get; set; }

    /// <summary>购买/拾取数量。</summary>
    public int? Quantity { get; set; }

    /// <summary>是否为卓越物品。</summary>
    public bool IsExcellent { get; set; }

    // ═══════════════════════════════════════════════
    // 技能上下文
    // ═══════════════════════════════════════════════

    /// <summary>技能编号。</summary>
    public ushort? SkillNumber { get; set; }

    /// <summary>技能名称。</summary>
    public string? SkillName { get; set; }

    // ═══════════════════════════════════════════════
    // 属性点上下文
    // ═══════════════════════════════════════════════

    /// <summary>加点前力量。</summary>
    public short? StrengthBefore { get; set; }

    /// <summary>加点后力量。</summary>
    public short? StrengthAfter { get; set; }

    /// <summary>加点前敏捷。</summary>
    public short? AgilityBefore { get; set; }

    /// <summary>加点后敏捷。</summary>
    public short? AgilityAfter { get; set; }

    /// <summary>加点前体力。</summary>
    public short? VitalityBefore { get; set; }

    /// <summary>加点后体力。</summary>
    public short? VitalityAfter { get; set; }

    /// <summary>加点前智力。</summary>
    public short? EnergyBefore { get; set; }

    /// <summary>加点后智力。</summary>
    public short? EnergyAfter { get; set; }

    // ═══════════════════════════════════════════════
    // 怪物上下文
    // ═══════════════════════════════════════════════

    /// <summary>怪物编号。</summary>
    public short? MonsterNumber { get; set; }

    /// <summary>怪物名称。</summary>
    public string? MonsterName { get; set; }

    /// <summary>怪物等级。</summary>
    public short? MonsterLevel { get; set; }

    /// <summary>获得经验值。</summary>
    public long? ExperienceGained { get; set; }

    // ═══════════════════════════════════════════════
    // 辅助字段
    // ═══════════════════════════════════════════════

    /// <summary>事件摘要 — 人类可读的描述。</summary>
    public string? Summary { get; set; }

    /// <summary>生成人类可读的事件摘要。</summary>
    public override string ToString()
    {
        if (!string.IsNullOrEmpty(this.Summary))
            return this.Summary;

        return this.EventType switch
        {
            BehaviorEventType.NpcTalk => $"Lv{this.Level}: 与 {this.NpcName} 对话 @{this.MapName}({this.X},{this.Y})",
            BehaviorEventType.NpcDialogChoice => $"Lv{this.Level}: 对 {this.NpcName} 选择选项 {this.DialogChoice}({this.DialogChoiceDescription})",
            BehaviorEventType.ItemBought => $"Lv{this.Level}: 从 {this.NpcName} 购买 {this.ItemName} ×{this.Quantity}",
            BehaviorEventType.PotionBought => $"Lv{this.Level}: 从 {this.NpcName} 购买药水 ×{this.Quantity}",
            BehaviorEventType.EquipmentBought => $"Lv{this.Level}: 从 {this.NpcName} 购买装备 {this.ItemName}",
            BehaviorEventType.SkillLearned => $"Lv{this.Level}: 学习技能 [{this.SkillName}](#{this.SkillNumber})",
            BehaviorEventType.ItemEquipped => $"Lv{this.Level}: 装备 {this.ItemName}",
            BehaviorEventType.StatAllocated => $"Lv{this.Level}: 加点 (力{this.StrengthBefore}→{this.StrengthAfter} 敏{this.AgilityBefore}→{this.AgilityAfter} 体{this.VitalityBefore}→{this.VitalityAfter} 智{this.EnergyBefore}→{this.EnergyAfter})",
            BehaviorEventType.MonsterKilled => $"Lv{this.Level}: 击杀 {this.MonsterName}(Lv{this.MonsterLevel}) +{this.ExperienceGained}EXP",
            BehaviorEventType.LevelUp => $"Lv{this.Level}: 升级!",
            BehaviorEventType.MapEntered => $"Lv{this.Level}: 进入地图 [{this.MapName}](#{this.MapNumber})",
            BehaviorEventType.BuffReceived => $"Lv{this.Level}: 从 {this.NpcName} 获得BUFF",
            _ => $"Lv{this.Level}: {this.EventType}",
        };
    }
}
