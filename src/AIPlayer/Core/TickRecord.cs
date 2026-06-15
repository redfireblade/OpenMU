// <copyright file="TickRecord.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using MUnique.OpenMU.Pathfinding;

/// <summary>单个模块在单次 Tick 中的决策录制。</summary>
public sealed record ModuleDecision(string ModuleName, TimeSpan Duration);

/// <summary>AI survival level enum (moved out of deleted SurvivalManager).</summary>
public enum SurvivalLevel
{
    /// <summary>Normal state — no immediate threats.</summary>
    Normal,

    /// <summary>Low health — potions or retreat may be needed.</summary>
    LowHealth,

    /// <summary>Danger — critical HP or surrounded.</summary>
    Danger,

    /// <summary>Critical — near death, emergency retreat.</summary>
    Critical,

    /// <summary>Emergency — immediate action required.</summary>
    Emergency,
}

/// <summary>单次 Tick 的完整快照（用于测试客户端调试）。</summary>
public sealed record TickSnapshot(
    int TickNumber,
    Point Position,
    uint Hp,
    uint MaxHp,
    uint Mp,
    uint MaxMp,
    int Level,
    ushort MapId,
    SurvivalLevel SurvivalLevel,
    bool HasTarget,
    bool EmergencyRetreat,
    IReadOnlyList<ModuleDecision> Decisions,
    TickTiming Timing);
