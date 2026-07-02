// <copyright file="PathResult.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge.KnowledgeGraph;

/// <summary>
/// Represents the result of a pathfinding operation between two nodes in the knowledge graph.
/// </summary>
public sealed class PathResult
{
    /// <summary>
    /// Gets the starting node of the path.
    /// </summary>
    public required NodeId From { get; init; }

    /// <summary>
    /// Gets the target node of the path.
    /// </summary>
    public required NodeId To { get; init; }

    /// <summary>
    /// Gets the sequence of edges that form the path from <see cref="From"/> to <see cref="To"/>.
    /// </summary>
    public required IReadOnlyList<GraphEdge> Edges { get; init; }

    /// <summary>
    /// Gets or sets the total accumulated weight of the path.
    /// The weight semantics depend on the weight function used during pathfinding.
    /// </summary>
    public double TotalWeight { get; set; }

    /// <summary>
    /// Gets a value indicating whether a valid path was found.
    /// Returns <c>true</c> when the edge list is non-empty.
    /// </summary>
    public bool IsFound => Edges.Count > 0;

    /// <summary>
    /// Gets the ordered sequence of nodes along the path, derived from walking
    /// <see cref="Edges"/> from source through each successive target.
    /// </summary>
    public IReadOnlyList<NodeId> Nodes
    {
        get
        {
            if (Edges.Count == 0)
            {
                return Array.Empty<NodeId>();
            }

            var nodes = new List<NodeId>(Edges.Count + 1)
            {
                Edges[0].Source,
            };

            nodes.AddRange(Edges.Select(e => e.Target));
            return nodes.AsReadOnly();
        }
    }
}
