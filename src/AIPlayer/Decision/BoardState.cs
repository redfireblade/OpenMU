// <copyright file="BoardState.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using MUnique.OpenMU.Pathfinding;

/// <summary>
/// 看板状态 — AI 角色大脑中的"事实状态中心"。
/// 只存数据，不调任何函数或模块。
/// 由外部系统（初始化器/决策系统/事件系统）读写。
/// </summary>
public sealed class BoardState
{
    /// <summary>世界事件队列 (时间排序，如 "火龙王@Lorencia 剩余15min")。</summary>
    public List<WorldEventEntry> WorldEvents { get; set; } = new();

    /// <summary>活跃 / 可接任务列表 (按优先级排序)。</summary>
    public List<MissionItem> Missions { get; set; } = new();

    /// <summary>已完成任务 ID 记录 (含 Quest 和自定义任务)。</summary>
    public List<string> CompletedMissionIds { get; set; } = new();

    /// <summary>角色当前状态快照 (每 tick 同步)。</summary>
    public PlayerStateSnapshot PlayerState { get; set; } = new();

    /// <summary>材料需求知识 — 登录时扫描生成。只读，不参与决策循环。</summary>
    public List<MaterialKnowledgeEntry> MaterialNeeds { get; set; } = new();

    /// <summary>待执行的测试任务（由 test-run API 设置，下一心跳消费）。
    /// 键值对包含 type（如"auto_equip"、"inventory_cleanup"），
    /// 执行完成后会写入 result/completed 等字段，
    /// 由 test-result API 读取后自行清除。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, object>? PendingTest { get; set; }
}

/// <summary>
/// 世界事件条目 — 火龙王、战盟战、系统活动等。
/// </summary>
public sealed class WorldEventEntry
{
    /// <summary>事件唯一标识。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>事件标题 (如 "火龙王来袭")。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>事件坐标地图 (如 "Lorencia", "LostTower")。</summary>
    public string Location { get; set; } = string.Empty;

    /// <summary>事件持续分钟数。</summary>
    public int DurationMinutes { get; set; }

    /// <summary>事件过期时间 (UTC)。</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>事件类别。</summary>
    public WorldEventCategory Category { get; set; }

    /// <summary>事件是否仍有效。</summary>
    public bool IsActive => DateTime.UtcNow < ExpiresAt;
}

/// <summary>
/// 世界事件类别。
/// </summary>
public enum WorldEventCategory
{
    /// <summary>Boss 突袭 (火龙王、魔王等)。</summary>
    BossRaid,

    /// <summary>战盟战争。</summary>
    GuildWar,

    /// <summary>系统活动 (血色城堡、恶魔广场等)。</summary>
    SystemActivity,

    /// <summary>环境事件 (天气/昼夜变化等)。</summary>
    Environmental,
}

/// <summary>
/// 角色状态快照 — 每 tick 从 IGameAdapter 同步。
/// 供决策层判断"当前是否该中断"、"能否接任务"等。
/// </summary>
public sealed class PlayerStateSnapshot
{
    /// <summary>HP 百分比 (0~100)。</summary>
    public int HpPercent { get; set; }

    /// <summary>MP 百分比 (0~100)。</summary>
    public int MpPercent { get; set; }

    /// <summary>当前坐标。</summary>
    public Point Position { get; set; }

    /// <summary>当前地图编号。</summary>
    public ushort MapNumber { get; set; }

    /// <summary>背包占用率 (0.0~1.0)。</summary>
    public float InventoryFullness { get; set; }

    /// <summary>是否在安全区。</summary>
    public bool IsInSafeZone { get; set; }

    /// <summary>是否正在行走。</summary>
    public bool IsWalking { get; set; }

    /// <summary>当前正在做的活动描述 (如 "执行任务 主线G0N1", "生存巡逻")。</summary>
    public string? CurrentActivity { get; set; }

    /// <summary>角色等级。</summary>
    public int Level { get; set; }
}

/// <summary>材料需求知识条目 — 登录时填充，供看板展示和决策参考。</summary>
public sealed class MaterialKnowledgeEntry
{
    /// <summary>需要合成的目标物品描述（如"恶魔广场Lv.2门票"、"翅膀"）</summary>
    public string TargetDescription { get; set; } = "";

    /// <summary>目标物品 Group</summary>
    public int TargetGroup { get; set; }

    /// <summary>目标物品 Number</summary>
    public int TargetNumber { get; set; }

    /// <summary>目标材料等级（0=不区分等级）</summary>
    public byte TargetLevel { get; set; }

    /// <summary>材料列表</summary>
    public List<MaterialItemState> Materials { get; set; } = new();

    /// <summary>是否已满足(目标物品已合成或在背包)。</summary>
    public bool IsSatisfied { get; set; }
}

/// <summary>单个材料的状态</summary>
public sealed class MaterialItemState
{
    public int Group { get; set; }
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public byte RequiredLevel { get; set; }
    public int RequiredCount { get; set; }

    /// <summary>背包中有多少</summary>
    public int InInventory { get; set; }
    /// <summary>仓库中有多少（同等级匹配）</summary>
    public int InVault { get; set; }
    /// <summary>背包+仓库总计</summary>
    public int Total => this.InInventory + this.InVault;

    /// <summary>是否充足</summary>
    public bool IsSufficient => this.Total >= this.RequiredCount;

    /// <summary>状态枚举</summary>
    public MaterialStatus Status { get; set; }

    // 如果 Status == NeedFarm，以下字段可用
    public short? FarmMonsterNumber { get; set; }
    public string? FarmMonsterName { get; set; }
    public ushort? FarmMapNumber { get; set; }
    public string? FarmMapName { get; set; }
    public byte? FarmItemLevel { get; set; }
}

/// <summary>材料状态</summary>
public enum MaterialStatus
{
    /// <summary>充足</summary>
    Sufficient,

    /// <summary>仓库有（需取）</summary>
    NeedVault,

    /// <summary>需要打</summary>
    NeedFarm,

    /// <summary>需要先合成中间产物</summary>
    NeedCraftFirst,

    /// <summary>等级不够</summary>
    LevelTooLow,

    /// <summary>未知（无掉落/合成信息）</summary>
    Unknown,
}
