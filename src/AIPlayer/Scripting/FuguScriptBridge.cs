// <copyright file="FuguScriptBridge.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Scripting;

using Microsoft.Extensions.Logging;
using OAPS.Mind;
using MUnique.OpenMU.AIPlayer.Knowledge;

/// <summary>
/// Bridge between the existing ScriptExecutor and the Fugu v4.0 routing system.
///
/// Replaces the static priorityChain evaluation in ScriptExecutor.TickAsync()
/// with Fugu-style per-step adaptive routing.
///
/// Lifecycle (per tick):
///   1. ScriptExecutor calls ShouldRoute() → returns true when Fugu mode is active
///   2. ScriptExecutor calls Route(state) → FuguOrchestrator.Decide() returns action
///   3. ScriptExecutor dispatches the action (instead of checking priorityChain)
///   4. After action execution, ScriptExecutor calls RecordOutcome() with reward
/// </summary>
public sealed class FuguScriptBridge
{
    private readonly FuguOrchestrator _orchestrator;
    private readonly ILogger _logger;
    private bool _enabled;

    public FuguScriptBridge(
        FuguOrchestrator orchestrator,
        ILogger logger,
        bool startEnabled = true)
    {
        _orchestrator = orchestrator;
        _logger = logger;
        _enabled = startEnabled;
    }

    /// <summary>Whether Fugu routing is currently active.</summary>
    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            _logger.LogInformation("[FuguBridge] Routing {State}", value ? "ENABLED" : "DISABLED");
        }
    }

    /// <summary>Gets the underlying orchestrator.</summary>
    public FuguOrchestrator Orchestrator => _orchestrator;

    /// <summary>
    /// Route the AI's next action using Fugu-style per-step adaptive routing.
    /// This replaces checking priorityChain conditions in ScriptExecutor.
    /// </summary>
    /// <returns>Action to execute, or null if Fugu is disabled.</returns>
    public FuguRouteResult? Route(FuguStateContext context)
    {
        if (!_enabled) return null;

        var state = _orchestrator.BuildStateVector(
            context.Hp, context.MaxHp, context.Mp, context.MaxMp,
            context.Level, context.IsAtSafeZone, context.FreeSlots, context.IsSurrounded,
            context.TargetCount, context.NearestMonsterLevel, context.NearbyItems,
            context.HotspotDistance, context.TimeSinceLastKill, context.IsDebuffed,
            context.PositionX, context.PositionY);

        var (actionIndex, actionName, confidence) =
            _orchestrator.Decide(state, context.PersonalityTemperature);

        return new FuguRouteResult(actionIndex, actionName, confidence, state);
    }

    /// <summary>
    /// Record the outcome of the routed action.
    /// </summary>
    public void RecordOutcome(
        bool success, float damageDealt, int kills, int deaths,
        int itemsPicked, int zenGained, int levelsGained, int skillsLearned)
    {
        _orchestrator.RecordOutcome(
            success, damageDealt, kills, deaths,
            itemsPicked, zenGained, levelsGained, skillsLearned);
    }

    /// <summary>
    /// Trigger a CMA-ES evolution generation.
    /// </summary>
    public Task<float> EvolveAsync(CancellationToken ct = default)
        => _orchestrator.EvolveGenerationAsync(ct);

    /// <summary>
    /// Save learned weights.
    /// </summary>
    public Task SaveAsync() => _orchestrator.SaveWeightsAsync();

    /// <summary>
    /// Get diagnostics for monitoring.
    /// </summary>
    public FuguDiagnostics GetDiagnostics() => _orchestrator.GetDiagnostics();
}

/// <summary>
/// Context data needed by FuguOrchestrator to build a StateVector.
/// Extracted from the current AiPlayer/WorldState at each tick.
/// </summary>
public struct FuguStateContext
{
    public int Hp, MaxHp, Mp, MaxMp;
    public int Level;
    public bool IsAtSafeZone;
    public int FreeSlots;
    public bool IsSurrounded;
    public int TargetCount;
    public int NearestMonsterLevel;
    public int NearbyItems;
    public float HotspotDistance;
    public float TimeSinceLastKill;
    public bool IsDebuffed;
    public int PositionX, PositionY;
    public float PersonalityTemperature;
}

/// <summary>
/// Result of a Fugu routing decision.
/// </summary>
public readonly record struct FuguRouteResult(
    int ActionIndex,
    string ActionName,
    float Confidence,
    StateVector StateVector);
