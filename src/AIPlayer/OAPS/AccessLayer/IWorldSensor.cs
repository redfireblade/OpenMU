// <copyright file="IWorldSensor.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.AccessLayer;

using OAPS.World;

/// <summary>
/// 统一感知契约 — Layer 1 核心接口。
/// 无论底层是解包、读内存还是 CV 识别，必须输出标准 <see cref="WorldState"/>。
/// 上层模块只认识此接口，不关心实现。
/// 参考 OAPS v3.0 2.1 核心接口契约。
/// </summary>
public interface IWorldSensor
{
    /// <summary>
    /// 捕获当前世界状态快照。每 400ms 由 HeartbeatService 调用一次。
    /// </summary>
    ValueTask<WorldState> CaptureAsync(CancellationToken ct = default);

    /// <summary>
    /// 订阅底层事件（如封包到达、CV识别到怪物）。
    /// </summary>
    event Action<GameEvent>? OnGameEvent;

    /// <summary>
    /// 获取当前使用的接入模式。
    /// </summary>
    AccessMode GetCurrentMode();
}

/// <summary>
/// 游戏事件 — 底层触发的事件数据。
/// </summary>
public record GameEvent
{
    /// <summary>事件类型。</summary>
    public GameEventType Type { get; init; }

    /// <summary>事件关联的实体 ID。</summary>
    public uint EntityId { get; init; }

    /// <summary>事件关联的地图坐标 X。</summary>
    public byte X { get; init; }

    /// <summary>事件关联的地图坐标 Y。</summary>
    public byte Y { get; init; }

    /// <summary>事件关联的数值（伤害/经验等）。</summary>
    public int Value { get; init; }

    /// <summary>事件发生时间。</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// 游戏事件类型枚举。
/// </summary>
public enum GameEventType
{
    /// <summary>怪物被击杀。</summary>
    MonsterKilled,

    /// <summary>物品掉落。</summary>
    ItemDropped,

    /// <summary>物品被拾取。</summary>
    ItemPickedUp,

    /// <summary>玩家受到伤害。</summary>
    PlayerHit,

    /// <summary>玩家升级。</summary>
    PlayerLevelUp,

    /// <summary>玩家死亡。</summary>
    PlayerDeath,
}
