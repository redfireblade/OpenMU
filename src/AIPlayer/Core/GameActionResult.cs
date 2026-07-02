// <copyright file="GameActionResult.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using MUnique.OpenMU.Pathfinding;

#region Walk / Warp

/// <summary>行走结果。</summary>
public sealed record WalkResult(WalkStatusCode Status, Point Target, Point CurrentPosition, string? Reason = null);

/// <summary>行走状态码。</summary>
public enum WalkStatusCode { Success, PathNotFound, AlgorithmUnavailable, AlreadyAtTarget }

/// <summary>传送结果。</summary>
public sealed record WarpResult(WarpStatusCode Status, ushort TargetMapNumber, ushort? ActualMapNumber, string? Reason, Point PlayerPosition)
{
    /// <summary>兼容外部代码：CurrentMap = ActualMapNumber。</summary>
    public ushort? CurrentMap => this.ActualMapNumber;
}

/// <summary>传送状态码。</summary>
public enum WarpStatusCode { Success, AlreadyOnTarget, MapNotFound, NoSpawnGate, PlayerNullContext, WarpFailed, BlockedLevelRequirement, BlockedNoRoute, BlockedNoMoney, GateLevelTooLow, MultiHopStepCompleted, MultiHopRouteComplete, WarpMenuInsufficientGold }

/// <summary>组队结果。</summary>
public sealed record PartyJoinResult(PartyJoinStatusCode Status, string TargetPlayerName, string? Reason = null);

/// <summary>组队状态码。</summary>
public enum PartyJoinStatusCode { Success, TargetNotFound, AlreadyInParty, InternalError }

#endregion

#region Quest

/// <summary>任务操作结果。</summary>
public sealed record QuestActionResult(
    QuestStatusCode Status,
    short Group,
    short Number,
    string? Reason = null,
    long? MoneyBalance = null,
    int? CharacterLevel = null);

/// <summary>任务状态码。</summary>
public enum QuestStatusCode { Accepted, AlreadyCompleted, InsufficientMoney, PrerequisitesNotMet, InternalError }

#endregion

#region Script Execution (Tick / Node / Relocate / Action)

/// <summary>脚本任务状态。</summary>
public enum ScriptTaskState { Idle, Running, Suspended, Stuck, Completed }

/// <summary>单次脚本 Tick 的完整执行状态。</summary>
public sealed record TickResult(
    ScriptTaskState State,
    string? ScriptName,
    string? ParagraphLabel,
    int ProgramCounter,
    int TotalNodes,
    string? NodeName,
    string? NodeAction,
    bool ConditionMet,
    bool PcAdvanced,
    string? GotoPending,
    NodeExecutionResult? NodeResult,
    int TicksAtCurrentPc)
{
    /// <summary>兼容外部队本：PC = ProgramCounter。</summary>
    public int PC => this.ProgramCounter;

    /// <summary>
    /// Implicit conversion to bool. Returns true when any condition was met or
    /// an interrupt/goto action was handled during this tick.
    /// Supports existing test assertions like <c>Assert.True(result)</c> and <c>Assert.False(result)</c>.
    /// </summary>
    /// <param name="result">The tick result to evaluate.</param>
    public static implicit operator bool(TickResult result) =>
        result.NodeResult?.ConditionMet == true
        || result.NodeName?.StartsWith("interrupt:", StringComparison.Ordinal) == true
        || result.NodeAction?.StartsWith("goto ", StringComparison.Ordinal) == true;
}

/// <summary>节点执行状态码。</summary>
public enum NodeStatusCode { Success, Skipped, Failed, ConditionNotMet, ActionNotMet, NoTarget, Completed, InProgress, AlreadyDone }

/// <summary>节点执行结果（含全部状态）。</summary>
public sealed record NodeExecutionResult(
    NodeStatusCode Status,
    string? NodeName,
    string? Action,
    bool ConditionMet,
    bool PcAdvanced,
    bool HasMoreNodes,
    bool GotoTriggered,
    string? TargetLabel,
    ScriptTaskState? NewExecutionState,
    string? Reason) : IActionResult;

/// <summary>操作执行结果接口。</summary>
public interface IActionResult { }

/// <summary>热点重定位结果。</summary>
public sealed record RelocateResult(
    RelocateStatusCode Status,
    int HotspotIndex,
    Point? Target,
    Point PlayerPosition,
    int AttemptCount,
    string? Reason) : IActionResult;

/// <summary>热点重定位状态码。</summary>
public enum RelocateStatusCode
{
    NoHotspots,         // 无热点可用
    AlreadyThere,       // 已在目标热点
    Moving,             // 正在行走前往
    PathBlocked,        // 路径被阻挡
    Completed,          // 已完成重定位
    MapChanged,         // 重定位时地图切换
    Failed,             // 重定位失败
    AllOccupied,        // 所有热点都被占用
}

#endregion
