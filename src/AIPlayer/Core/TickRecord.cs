// <copyright file="TickRecord.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using MUnique.OpenMU.Pathfinding;

/// <summary>单个模块在单次 Tick 中的决策录制。</summary>
public sealed record ModuleDecision(string ModuleName, TimeSpan Duration);

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
    global::MUnique.OpenMU.AIPlayer.SurvivalManager.SurvivalLevel SurvivalLevel,
    bool HasTarget,
    bool EmergencyRetreat,
    IReadOnlyList<ModuleDecision> Decisions,
    TickTiming Timing);
