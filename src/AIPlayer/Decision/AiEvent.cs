// <copyright file="AiEvent.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// 所有 AI 事件的基础记录。
/// 不可变 (record)，可序列化（未来支持存盘/重放）。
/// 优先级 0=最高，数字越小优先级越高。
/// </summary>
public abstract record AiEvent
{
    /// <summary>事件发生时间戳。</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>优先级（0=最高，数值越小优先级越高）。</summary>
    public virtual int Priority { get; init; } = 10;
}

/// <summary>
/// P0 紧急：角色血量过低，需要立即恢复。
/// </summary>
public sealed record HpLowEvent(int Hp, int MaxHp) : AiEvent
{
    /// <inheritdoc />
    public override int Priority { get; init; } = 0;
}

/// <summary>
/// P0 紧急：角色死亡。
/// </summary>
public sealed record DeathEvent(IAttacker? Killer) : AiEvent
{
    /// <inheritdoc />
    public override int Priority { get; init; } = 0;

    /// <summary>血量百分比（应为 0）。</summary>
    public float HpPercent => 0f;
}

/// <summary>
/// P0 紧急：角色复活。
/// </summary>
public sealed record RespawnEvent(Point Position) : AiEvent
{
    /// <inheritdoc />
    public override int Priority { get; init; } = 0;
}

/// <summary>
/// P1 中断：角色受到伤害。
/// </summary>
public sealed record DamagedEvent(int Damage, IAttacker Attacker) : AiEvent
{
    /// <inheritdoc />
    public override int Priority { get; init; } = 1;
}

/// <summary>
/// P1 中断：角色卡住 / 死锁检测触发。
/// </summary>
public sealed record StuckEvent : AiEvent
{
    /// <inheritdoc />
    public override int Priority { get; init; } = 1;
}

/// <summary>
/// P2 任务：怪物被击杀。
/// TotalKills/RequiredKills 为对应活跃任务的需求进度。
/// 如果怪物击杀不属于任何活跃任务则两者均为 0。
/// </summary>
public sealed record MonsterKilledEvent(short MonsterNumber, string MonsterName) : AiEvent
{
    /// <inheritdoc />
    public override int Priority { get; init; } = 2;
}

/// <summary>
/// P2 任务：任务状态变更。
/// </summary>
public sealed record QuestStateChangedEvent(short Group, short Number) : AiEvent
{
    /// <inheritdoc />
    public override int Priority { get; init; } = 2;
}

/// <summary>
/// P2 任务：任务所需道具掉落。
/// </summary>
public sealed record QuestItemDroppedEvent(string ItemName, short ItemGroup, short ItemNumber) : AiEvent
{
    /// <inheritdoc />
    public override int Priority { get; init; } = 2;
}

/// <summary>
/// P2 任务：目标 NPC 进入感知范围。
/// </summary>
public sealed record NpcInRangeEvent(short NpcNumber, string NpcName) : AiEvent
{
    /// <inheritdoc />
    public override int Priority { get; init; } = 2;
}

/// <summary>
/// P3 状态：角色升级。
/// </summary>
public sealed record LevelUpEvent(int NewLevel) : AiEvent
{
    /// <inheritdoc />
    public override int Priority { get; init; } = 3;
}

/// <summary>
/// P3: 世界频道聊天消息收到(含交易信息)。
/// 由 GameAdapter 在排出 AiViewPlugInContainer 的聊天缓存时发布。
/// </summary>
public sealed record ChatMessageReceivedEvent(string SenderName, string Message, ChatMessageType MessageType) : AiEvent
{
    /// <inheritdoc />
    public override int Priority { get; init; } = 3;
}

/// <summary>
/// P1 事件: 活动入场窗口打开 — AI 应该考虑参加。
/// </summary>
public sealed record EventOpenEvent(MiniGameType Type, int GameLevel, string Name, int EntranceFee) : AiEvent
{
    /// <inheritdoc />
    public override int Priority { get; init; } = 1;
}

/// <summary>
/// P2 事件: 活动进行中提醒（定期广播）。
/// MinutesLeft=0 表示刚开放(状态从 Prepared→Started)，-1 表示定时提醒。
/// </summary>
public sealed record EventReminderEvent(MiniGameType Type, int GameLevel, string Name, int MinutesLeft) : AiEvent
{
    /// <inheritdoc />
    public override int Priority { get; init; } = 2;
}

/// <summary>
/// P3 事件: 活动已结束。
/// </summary>
public sealed record EventClosedEvent(MiniGameType Type, int GameLevel, string Name) : AiEvent
{
    /// <inheritdoc />
    public override int Priority { get; init; } = 3;
}
