// <copyright file="KnowledgeGraphRuntimeLearner.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge.KnowledgeGraph;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

/// <summary>
/// Observes runtime gameplay events (monster kills, item drops) and updates
/// <see cref="KnowledgeGraph"/> edge weights to reflect empirical data.
/// Learned drop rates are blended with configured prior weights using a
/// configurable confidence threshold and weighting factors.
/// </summary>
public sealed class KnowledgeGraphRuntimeLearner
{
    private const double InverseWeightDivisor = 1.0;
    private const int ItemDomainIdGroupShift = 32;
    private const long ItemDomainIdNumberMask = 0xFFFFFFFF;

    private static readonly int MinConfidenceKills = 50;
    private static readonly double EmpiricalWeight = 0.7;
    private static readonly double PriorWeight = 0.3;
    private static readonly int ApplyInterval = 250;
    private static readonly int MaxRecentAdjustments = 100;
    private static readonly double BlendedRateEpsilon = 0.0;

    private readonly KnowledgeGraph _graph;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<(NodeId Monster, NodeId Item), DropObservation> _observedDrops;
    private readonly ConcurrentDictionary<long, long> _monsterKillCounts;
    private readonly List<string> _recentAdjustments;
    private readonly object _adjustmentLock = new();
    private int _totalObservations;

    /// <summary>
    /// Initializes a new instance of the <see cref="KnowledgeGraphRuntimeLearner"/> class.
    /// </summary>
    /// <param name="graph">The knowledge graph instance to update.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="graph"/> is <c>null</c>.</exception>
    public KnowledgeGraphRuntimeLearner(KnowledgeGraph graph, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        this._graph = graph;
        this._logger = logger;
        this._observedDrops = new ConcurrentDictionary<(NodeId, NodeId), DropObservation>();
        this._monsterKillCounts = new ConcurrentDictionary<long, long>();
        this._recentAdjustments = new List<string>();
    }

    /// <summary>
    /// Gets the interval in ticks between automatic weight update applications.
    /// </summary>
    public static int GetApplyInterval() => ApplyInterval;

    /// <summary>
    /// Gets the minimum number of kills required before empirical data is considered confident.
    /// </summary>
    public static int GetMinConfidenceKills() => MinConfidenceKills;

    /// <summary>
    /// Records a monster kill, incrementing the total kill counter for the specified monster.
    /// </summary>
    /// <param name="monsterNumber">The monster number that was killed.</param>
    public void RecordMonsterKill(short monsterNumber)
    {
        this._monsterKillCounts.AddOrUpdate(monsterNumber, 1, (_, count) => count + 1);
        this._logger?.LogTrace("Recorded kill for monster {MonsterNumber}. Total: {TotalKills}", monsterNumber, this._monsterKillCounts[monsterNumber]);
    }

    /// <summary>
    /// Records an observed item drop from a specific monster.
    /// Increments the observed count for the monster-item pair and syncs
    /// the total kill count from the global kill counter.
    /// </summary>
    /// <param name="monsterNumber">The monster number that dropped the item.</param>
    /// <param name="itemGroup">The item group (category) of the dropped item.</param>
    /// <param name="itemNumber">The item number within its group.</param>
    public void RecordObservedDrop(short monsterNumber, int itemGroup, int itemNumber)
    {
        var monsterNodeId = NodeId.ForMonster(monsterNumber);
        var itemNodeId = NodeId.ForItem(itemGroup, itemNumber);
        var key = (monsterNodeId, itemNodeId);

        var totalKills = this._monsterKillCounts.GetOrAdd(monsterNumber, 0);

        this._observedDrops.AddOrUpdate(
            key,
            _ =>
            {
                Interlocked.Increment(ref this._totalObservations);
                return new DropObservation
                {
                    ObservedCount = 1,
                    TotalKills = totalKills,
                };
            },
            (_, existing) =>
            {
                Interlocked.Increment(ref this._totalObservations);
                existing.ObservedCount++;
                existing.TotalKills = this._monsterKillCounts.GetOrAdd(monsterNumber, 0);
                return existing;
            });

        this._logger?.LogTrace(
            "Recorded drop: Monster {MonsterNumber} -> Item({Group}, {Number}). Observed: {ObservedCount}, TotalKills: {TotalKills}",
            monsterNumber,
            itemGroup,
            itemNumber,
            this._observedDrops[key].ObservedCount,
            totalKills);
    }

    /// <summary>
    /// Applies learned drop rates to graph edges by blending empirical observations
    /// with configured prior weights. Only processes observations with
    /// <see cref="DropObservation.TotalKills"/> >= <see cref="MinConfidenceKills"/>.
    /// </summary>
    /// <returns>The number of edges that were adjusted.</returns>
    public int ApplyLearnedDropRates()
    {
        var adjustedCount = 0;
        var tempAdjustments = new List<string>();

        foreach (var kvp in this._observedDrops)
        {
            if (this.TryApplyObservation(kvp, out var description))
            {
                adjustedCount++;
                tempAdjustments.Add(description);
            }
        }

        this.StoreRecentAdjustments(tempAdjustments);

        return adjustedCount;
    }

    /// <summary>
    /// Gets diagnostic statistics about the current learning state.
    /// </summary>
    /// <returns>A <see cref="RuntimeLearnerStats"/> instance with current counts and recent adjustments.</returns>
    public RuntimeLearnerStats GetStats()
    {
        lock (this._adjustmentLock)
        {
            return new RuntimeLearnerStats
            {
                TotalObservations = this._totalObservations,
                MonstersTracked = this._monsterKillCounts.Count,
                ObservedPairs = this._observedDrops.Count,
                RecentAdjustments = new List<string>(this._recentAdjustments),
            };
        }
    }

    /// <summary>
    /// Attempts to blend empirical data for a single observation into the graph edge weight.
    /// Returns <c>true</c> and a description string if the edge was updated.
    /// </summary>
    private bool TryApplyObservation(KeyValuePair<(NodeId Monster, NodeId Item), DropObservation> kvp, out string description)
    {
        description = string.Empty;

        var (monsterNodeId, itemNodeId) = kvp.Key;
        var observation = kvp.Value;

        // Sync total kills from the global kill counter.
        observation.TotalKills = this._monsterKillCounts.GetOrAdd(monsterNodeId.DomainId, 0);

        if (observation.TotalKills < MinConfidenceKills)
        {
            return false;
        }

        var empiricalRate = observation.EmpiricalRate;

        // Get the existing edge to read the configured (prior) weight.
        var existingEdge = this._graph.GetEdge(monsterNodeId, itemNodeId, EdgeType.DropsAt);
        if (existingEdge is null)
        {
            return false;
        }

        var configuredRate = existingEdge.Weight;

        // Blend empirical and configured rates, then invert to get edge weight.
        var blendedRate = (EmpiricalWeight * empiricalRate) + (PriorWeight * configuredRate);
        var newWeight = blendedRate > BlendedRateEpsilon
            ? InverseWeightDivisor / blendedRate
            : double.MaxValue;

        if (!this._graph.UpdateEdgeWeight(monsterNodeId, itemNodeId, EdgeType.DropsAt, newWeight))
        {
            return false;
        }

        description =
            $"Monster #{monsterNodeId.DomainId} -> Item #{itemNodeId.DomainId >> ItemDomainIdGroupShift}.{itemNodeId.DomainId & ItemDomainIdNumberMask}: " +
            $"empirical={empiricalRate:F4}, configured={configuredRate:F4}, " +
            $"blended={blendedRate:F4}, newWeight={newWeight:F4}";

        this._logger?.LogDebug(
            "Adjusted DropsAt edge: Monster {MonsterNumber} -> Item({ItemGroup},{ItemNumber}). Rate={EmpiricalRate:F4}, Conf={ConfiguredRate:F4}, Weight={NewWeight:F4}",
            monsterNodeId.DomainId,
            itemNodeId.DomainId >> ItemDomainIdGroupShift,
            itemNodeId.DomainId & ItemDomainIdNumberMask,
            empiricalRate,
            configuredRate,
            newWeight);

        return true;
    }

    /// <summary>
    /// Stores recent adjustment descriptions in the thread-safe adjustment log.
    /// Trims the log to prevent unbounded growth.
    /// </summary>
    private void StoreRecentAdjustments(List<string> adjustments)
    {
        if (adjustments.Count == 0)
        {
            return;
        }

        lock (this._adjustmentLock)
        {
            this._recentAdjustments.AddRange(adjustments);

            if (this._recentAdjustments.Count > MaxRecentAdjustments)
            {
                this._recentAdjustments.RemoveRange(0, this._recentAdjustments.Count - MaxRecentAdjustments);
            }
        }
    }
}
