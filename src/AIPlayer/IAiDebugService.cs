// <copyright file="IAiDebugService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// Debug-oriented extension to <see cref="IAiService"/>.
/// Provides per-player step control, state injection, and detailed snapshots
/// for the Blazor debug dashboard.
/// </summary>
public interface IAiDebugService
{
    /// <summary>
    /// Gets detailed debug data for a specific AI player.
    /// Includes TickSnapshot, position, survival level, and last module decisions.
    /// </summary>
    /// <param name="playerId">The AI player ID.</param>
    /// <returns>Debug data, or <c>null</c> if the player was not found.</returns>
    AiPlayerDebugData? GetDebugData(Guid playerId);

    /// <summary>
    /// Gets detailed debug data for all active AI players.
    /// </summary>
    IReadOnlyCollection<AiPlayerDebugData> GetAllDebugData();

    /// <summary>
    /// Executes one behavior tick on the specified AI player.
    /// Only valid for players created with step mode enabled.
    /// </summary>
    /// <param name="playerId">The AI player ID.</param>
    /// <returns>The resulting <see cref="TickSnapshot"/>, or <c>null</c> if the player was not found.</returns>
    ValueTask<TickSnapshot?> StepOnceAsync(Guid playerId);

    /// <summary>
    /// Sets the player's current HP.
    /// </summary>
    ValueTask SetHpAsync(Guid playerId, uint hp);

    /// <summary>
    /// Sets the player's current MP.
    /// </summary>
    ValueTask SetMpAsync(Guid playerId, uint mp);

    /// <summary>
    /// Sets the player's character level and adjusts level-up points.
    /// </summary>
    ValueTask SetLevelAsync(Guid playerId, int level);

    /// <summary>
    /// Sets the player's position on the current map.
    /// </summary>
    ValueTask SetPositionAsync(Guid playerId, byte x, byte y);

    /// <summary>
    /// Sets a stat attribute value for the specified player.
    /// Useful for adjusting base stats like vitality after leveling up.
    /// </summary>
    /// <param name="playerId">The AI player ID.</param>
    /// <param name="attributeDefinitionId">The attribute definition (Guid) to set, e.g. Stats.BaseVitality.</param>
    /// <param name="value">The new value.</param>
    ValueTask SetStatAttributeAsync(Guid playerId, Guid attributeDefinitionId, float value);

    /// <summary>
    /// Sets the player's current money (zen).
    /// </summary>
    /// <param name="playerId">The AI player ID.</param>
    /// <param name="money">The amount of money to set.</param>
    ValueTask SetMoneyAsync(Guid playerId, uint money);
}

/// <summary>
/// AI player execution mode.
/// </summary>
public enum ExecutionMode
{
    /// <summary>1D Script-driven mode: priority-chain executor replaces DAG modules.</summary>
    Mode1D,

    /// <summary>DAG module mode: default full-agent mode with MindEngine + DAG executor.</summary>
    DAG,

    /// <summary>Degraded mode: DAG executor threw exception, fallthrough to PC Pipeline.</summary>
    Degraded,
}

/// <summary>
/// Extended player state snapshot for the debug dashboard.
/// Contains all <see cref="AiPlayerState"/> fields plus live position,
/// survival status, and recent module decisions.
/// </summary>
public sealed record AiPlayerDebugData(
    Guid PlayerId,
    string CharacterName,
    ushort CurrentMapId,
    uint CurrentHealth,
    uint MaximumHealth,
    uint CurrentMana,
    uint MaximumMana,
    int Level,
    DateTime StartTimestamp,
    byte PositionX,
    byte PositionY,
    int TickNumber,
    global::MUnique.OpenMU.AIPlayer.SurvivalManager.SurvivalLevel SurvivalLevel,
    bool HasTarget,
    bool EmergencyRetreat,
    IReadOnlyList<ModuleDecision>? LastDecisions,
    string? CurrentBehavior,
    string? ScriptName,
    string? ScriptPosition,
    string[]? ScriptLines,
    int ScriptLineNumber,
    bool StateMachineRunning = false,
    string? StateMachineItem = null,
    string? StateMachineItemStatus = null,
    int StateMachineQueueLength = 0,
    int StateMachineCompleted = 0,
    int StateMachineFailed = 0,
    int StateMachineEventCount = 0,
    long StateMachineEventsProcessed = 0,
    bool StateMachineStuck = false,
    string? QuestProgress = null,
    ExecutionMode RunMode = ExecutionMode.DAG,
    long TickTotalUs = 0,
    long TickWorldRefreshUs = 0,
    long TickScriptExecutionUs = 0,
    long TickModuleExecutionUs = 0,
    long TickMindEngineUs = 0,
    double TicksPerSecond = 0,
    double AvgTickUs = 0,
    double P95TickUs = 0,

    /// <summary>今日看板待办列表（JSON 序列化后的数组）。</summary>
    string? DashboardTodos = null,
    /// <summary>当前活跃子模块 ID。</summary>
    string? DashboardActiveModule = null,
    /// <summary>心跳状态描述。</summary>
    string? DashboardState = null,
    /// <summary>心跳事件队列积压长度。</summary>
    int DashboardEventQueueLength = 0);
