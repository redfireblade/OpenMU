// <copyright file="WorkerProfileNode.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

#nullable disable warnings

namespace MUnique.OpenMU.AIPlayer.Knowledge.KnowledgeGraph;

using Microsoft.Extensions.Logging;

/// <summary>
/// F13: Fugu-style WorkerProfile node in the Knowledge Graph.
/// Each AI character has a WorkerProfile node storing empirical capability scores.
/// Maps to Fugu's per-worker empirical performance data for task assignment.
/// </summary>
public sealed class WorkerProfileNode
{
    private readonly KnowledgeGraph _kg;
    private readonly ILogger _logger;

    public WorkerProfileNode(KnowledgeGraph kg, ILogger<WorkerProfileNode> logger)
    {
        _kg = kg;
        _logger = logger;
    }

    /// <summary>Upserts a worker's capability score into the KG.</summary>
    public void UpsertProfile(
        string workerId, string capability, float score,
        int? preferredMapId = null, int? preferredMonsterId = null)
    {
        var nodeId = NodeId.ForWorkerProfile(workerId);

        if (!_kg.TryGetNode(nodeId, out var node) || node is null)
        {
            node = new GraphNode(nodeId, $"Worker:{workerId}", NodeType.WorkerProfile);
            node.Properties["workerId"] = workerId;
            _kg.AddNode(node);
        }

        node!.Properties[capability] = score;
        node!.Properties["lastUpdated"] = DateTime.UtcNow.ToString("o");

        if (preferredMapId.HasValue)
        {
            var mapNodeId = NodeId.ForMap(preferredMapId.Value);
            _kg.AddEdge(new GraphEdge(EdgeType.SpecializesIn, nodeId, mapNodeId, score));
        }

        if (preferredMonsterId.HasValue)
        {
            var monsterNodeId = NodeId.ForMonster((short)preferredMonsterId.Value);
            _kg.AddEdge(new GraphEdge(EdgeType.SpecializesIn, nodeId, monsterNodeId, score));
        }

        _logger.LogDebug("[KG:Worker] {Worker}.{Cap} = {Score:F2}", workerId, capability, score);
    }

    /// <summary>Queries the best workers for a given capability.</summary>
    public List<(string WorkerId, float Score)> QueryBestWorkers(
        string capability, float minScore = 0.3f, int topN = 5)
    {
        var results = new List<(string, float)>();

        foreach (var node in _kg.GetAllNodes())
        {
            if (node.Id.Type != NodeType.WorkerProfile) continue;

            if (node.Properties.TryGetValue(capability, out var scoreObj)
                && scoreObj is float score
                && score >= minScore)
            {
                var wid = (node.Properties.TryGetValue("workerId", out var w)
                    && w is string s) ? s : "?";
                results.Add((wid, score));
            }
        }

        return results.OrderByDescending(r => r.Item2).Take(topN).ToList();
    }

    /// <summary>Gets a worker's full capability profile from the KG.</summary>
    public WorkerCapabilityProfile? GetProfile(string workerId)
    {
        var nodeId = NodeId.ForWorkerProfile(workerId);
        if (!_kg.TryGetNode(nodeId, out var node) || node is null) return null;

        var profile = new WorkerCapabilityProfile { WorkerId = workerId };
        foreach (var (key, value) in node.Properties)
        {
            if (key is "workerId" or "lastUpdated") continue;
            if (value is float f) profile.Capabilities[key] = f;
        }

        return profile;
    }
}

public class WorkerCapabilityProfile
{
    public string WorkerId { get; set; } = string.Empty;
    public Dictionary<string, float> Capabilities { get; set; } = new();
}
