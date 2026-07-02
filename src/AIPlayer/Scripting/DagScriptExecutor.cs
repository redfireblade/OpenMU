// <copyright file="DagScriptExecutor.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Scripting;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.AIPlayer.Decision;
using OAPS.Mind;

/// <summary>
/// F9: DAG-parallel script execution — replaces the 1D priorityChain with
/// parallel candidate evaluation + SoftRouter aggregation (Fugu-style).
///
/// Flow per tick:
///   1. Extract StateVector from current world state
///   2. Enqueue candidates: CombatCandidate, PickupCandidate, PatrolCandidate, FleeCandidate
///   3. Execute them in parallel (they each evaluate their feasibility independently)
///   4. SoftRouter aggregates results into ActionDistribution
///   5. Sample or argmax → selected action
///   6. Execute the selected action
///   7. Record reward → feed back to SoftRouter and CMA-ES
/// </summary>
public sealed class DagScriptExecutor
{
    private readonly ILogger _logger;
    private readonly SoftRouter _router;
    private readonly FuguKanbanBoard _kanban;
    private readonly List<IDagCandidate> _candidates = new();

    private int _tickCount;
    private float _cumulativeReward;

    public DagScriptExecutor(
        SoftRouter router,
        FuguKanbanBoard kanban,
        ILogger<DagScriptExecutor> logger)
    {
        _router = router;
        _kanban = kanban;
        _logger = logger;
    }

    /// <summary>
    /// Register a DAG candidate module.
    /// </summary>
    public void RegisterCandidate(IDagCandidate candidate)
    {
        _candidates.Add(candidate);
    }

    /// <summary>
    /// Execute one tick using DAG-parallel evaluation + SoftRouter aggregation.
    /// </summary>
    public async Task<DagTickResult> TickAsync(StateVector state, CancellationToken ct = default)
    {
        _tickCount++;

        // 1. Parallel candidate evaluation
        var evaluations = new List<CandidateEval>();
        var tasks = new List<Task<CandidateEval>>();

        foreach (var candidate in _candidates)
        {
            if (candidate.IsApplicable(state))
            {
                tasks.Add(Task.Run(() => candidate.EvaluateAsync(state, ct), ct));
            }
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        evaluations.AddRange(results);

        if (evaluations.Count == 0)
        {
            return new DagTickResult { Action = ActionSpace.Names[ActionSpace.Wait], Confidence = 1.0f, Success = false };
        }

        // 2. Aggregate via SoftRouter
        var actionDist = _router.Evaluate(state);

        // 3. Weight candidates by router distribution AND their own confidence
        var bestEval = evaluations
            .OrderByDescending(e => e.Confidence * actionDist.Probabilities[e.ActionIndex])
            .First();

        // 4. Execute
        bestEval.Action?.Invoke();
        var success = bestEval.Success;

        // 5. Record reward
        var reward = bestEval.EstimatedReward;
        _router.RecordReward(bestEval.ActionIndex, reward);
        _cumulativeReward += reward;

        // 6. Select the best candidate deterministically (argmax for safety)
        var selectedAction = _router.SelectBestAction(actionDist);

        return new DagTickResult
        {
            Action = ActionSpace.Names[selectedAction],
            ActionIndex = selectedAction,
            Confidence = actionDist.Probabilities[selectedAction],
            Success = success,
            Reward = reward,
            CandidatesEvaluated = evaluations.Count,
            Distribution = actionDist,
        };
    }

    /// <summary>The cumulative reward since last evolution.</summary>
    public float CumulativeReward => _cumulativeReward;

    /// <summary>Reset cumulative reward (call after evolution).</summary>
    public void ResetReward() => _cumulativeReward = 0f;
}

/// <summary>
/// A DAG candidate — one possible action path evaluated in parallel.
/// </summary>
public interface IDagCandidate
{
    /// <summary>Action index this candidate maps to.</summary>
    int ActionIndex { get; }

    /// <summary>Whether this candidate is applicable in the current state.</summary>
    bool IsApplicable(StateVector state);

    /// <summary>Evaluate this candidate's feasibility + estimated reward.</summary>
    Task<CandidateEval> EvaluateAsync(StateVector state, CancellationToken ct);
}

/// <summary>
/// Evaluation result of one DAG candidate.
/// </summary>
public class CandidateEval
{
    public int ActionIndex { get; init; }
    public float Confidence { get; init; }
    public float EstimatedReward { get; init; }
    public bool Success { get; set; }
    public Action? Action { get; set; }
}

/// <summary>
/// Result of one DAG tick.
/// </summary>
public class DagTickResult
{
    public string Action { get; init; } = string.Empty;
    public int ActionIndex { get; init; }
    public float Confidence { get; init; }
    public bool Success { get; init; }
    public float Reward { get; init; }
    public int CandidatesEvaluated { get; init; }
    public ActionDistribution? Distribution { get; init; }
}
