// <copyright file="GraphNode.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge.KnowledgeGraph;

/// <summary>
/// Represents a node (vertex) in the knowledge graph.
/// </summary>
/// <param name="Id">The unique node identifier.</param>
/// <param name="Label">A human-readable label for this node.</param>
/// <param name="Type">The type of this node.</param>
public sealed record GraphNode(NodeId Id, string Label, NodeType Type)
{
    /// <summary>
    /// Gets an optional dictionary of additional properties attached to this node.
    /// </summary>
    public Dictionary<string, object>? Properties { get; init; }
}
