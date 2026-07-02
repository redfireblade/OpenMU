// <copyright file="GraphEdge.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge.KnowledgeGraph;

/// <summary>
/// Represents a directed edge (relationship) between two nodes in the knowledge graph.
/// </summary>
/// <param name="Type">The type of relationship this edge represents.</param>
/// <param name="Source">The source node identifier.</param>
/// <param name="Target">The target node identifier.</param>
/// <param name="Weight">The weight of this edge (default 1.0). Higher values indicate stronger or more relevant relationships.</param>
public sealed record GraphEdge(EdgeType Type, NodeId Source, NodeId Target, double Weight = 1.0)
{
    /// <summary>
    /// Gets an optional dictionary of additional properties attached to this edge.
    /// </summary>
    public Dictionary<string, object>? Properties { get; init; }
}
