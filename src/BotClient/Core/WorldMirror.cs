using System.Collections.Concurrent;

namespace MUnique.OpenMU.BotClient.Core;

/// <summary>每个 BOT 独立持有的世界镜像。由网络封包处理器写入，由 AI 决策引擎读取。</summary>
/// <remarks>
/// 线程安全模型：
/// - 网络线程（IOCP 回调）写入 Monsters/GroundItems/Players
/// - 调度器线程读取用于 AI 决策
/// - 使用 ConcurrentDictionary 保证无锁安全
/// - 标量字段（PosX/HP 等）允许 torn read，实际不影响决策正确性
/// </remarks>
public sealed class WorldMirror
{
    // ─── 自身状态 ───
    public byte PosX { get; set; }
    public byte PosY { get; set; }
    public ushort MapId { get; set; }
    public ushort CurrentHp { get; set; }
    public ushort MaximumHp { get; set; }
    public ushort CurrentMp { get; set; }
    public ushort MaximumMp { get; set; }
    public ushort? PlayerId { get; set; }
    public int Level { get; set; }

    // ─── 视野内的怪物 ───
    public ConcurrentDictionary<ushort, MonsterEntry> Monsters { get; } = new();

    // ─── 地上的物品 ───
    public ConcurrentDictionary<ushort, ItemDropEntry> GroundItems { get; } = new();

    // ─── 视野内的其他玩家 ───
    public ConcurrentDictionary<ushort, PlayerEntry> Players { get; } = new();
}

/// <summary>视野内的怪物或 NPC。</summary>
public sealed class MonsterEntry
{
    public ushort Id { get; init; }
    public ushort TypeNumber { get; set; }
    public byte PosX { get; set; }
    public byte PosY { get; set; }
}

/// <summary>地上的掉落物品。</summary>
public sealed class ItemDropEntry
{
    public ushort Id { get; init; }
    public byte PosX { get; set; }
    public byte PosY { get; set; }
    public ushort ItemId { get; set; }
}

/// <summary>视野内的其他玩家。</summary>
public sealed class PlayerEntry
{
    public ushort Id { get; init; }
    public byte PosX { get; set; }
    public byte PosY { get; set; }
    public string Name { get; set; } = string.Empty;
}
