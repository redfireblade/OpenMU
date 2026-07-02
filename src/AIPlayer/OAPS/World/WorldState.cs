// <copyright file="WorldState.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.World;

using OAPS.AccessLayer;

/// <summary>
/// 世界状态快照 — AI 决策的唯一环境输入源。每 400ms 刷新一次。
/// 参考 OAPS v3.0 3.3。
/// </summary>
public record WorldState
{
    /// <summary>玩家自身状态。</summary>
    public required PlayerSnapshot Self { get; init; }

    /// <summary>视野内的怪物字典。</summary>
    public IReadOnlyDictionary<uint, MonsterSnapshot> Monsters { get; init; }
        = new Dictionary<uint, MonsterSnapshot>();

    /// <summary>视野内的掉落物字典。</summary>
    public IReadOnlyDictionary<uint, ItemSnapshot> Items { get; init; }
        = new Dictionary<uint, ItemSnapshot>();

    /// <summary>当前任务状态。</summary>
    public QuestState? Quest { get; init; }

    /// <summary>当前接入模式。</summary>
    public AccessMode AccessMode { get; init; } = AccessMode.OfficialApi;

    /// <summary>快照时间戳。</summary>
    public DateTime LastUpdate { get; init; } = DateTime.UtcNow;
}

/// <summary>玩家快照。</summary>
public record PlayerSnapshot(
    uint Id,
    byte X,
    byte Y,
    int HP,
    int MaxHP,
    int MP,
    int MaxMP,
    int Level,
    int Zen
);

/// <summary>怪物快照。</summary>
public record MonsterSnapshot(
    uint Id,
    ushort TypeId,
    byte X,
    byte Y,
    int HP,
    int MaxHP,
    byte Level
);

/// <summary>掉落物快照。</summary>
public record ItemSnapshot(
    uint Id,
    ushort TypeId,
    byte X,
    byte Y,
    byte Level,
    ItemGrade Grade
);

/// <summary>任务状态。</summary>
public record QuestState
{
    /// <summary>任务组号。</summary>
    public short Group { get; init; }

    /// <summary>任务编号。</summary>
    public short Number { get; init; }

    /// <summary>是否活跃。</summary>
    public bool IsActive { get; init; }
}

/// <summary>物品等级（S/A/B/C/D/E/Junk），与 KnowledgeBase 价值评估体系对齐。</summary>
public enum ItemGrade
{
    /// <summary>垃圾，不值得拾取。</summary>
    Junk,

    /// <summary>基础材料/药水。</summary>
    E,

    /// <summary>普通装备。</summary>
    D,

    /// <summary>优秀装备。</summary>
    C,

    /// <summary>卓越/技能装备。</summary>
    B,

    /// <summary>远古/套装装备。</summary>
    A,

    /// <summary>宝石/极品，最高优先级。</summary>
    S,
}
