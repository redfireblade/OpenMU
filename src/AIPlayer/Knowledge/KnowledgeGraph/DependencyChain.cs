// <copyright file="DependencyChain.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge.KnowledgeGraph;

/// <summary>
/// Defines the direction of dependency resolution.
/// Forward follows outgoing edges (what a node produces or enables).
/// Backward follows incoming edges (what a node requires as prerequisites).
/// </summary>
public enum DependencyDirection
{
    /// <summary>
    /// Traverse from the target outward via outgoing edges,
    /// discovering what the target node produces or enables.
    /// </summary>
    Forward,

    /// <summary>
    /// Traverse from the target inward via incoming edges,
    /// discovering what the target node requires as prerequisites.
    /// </summary>
    Backward,
}

/// <summary>
/// Represents a chain of dependencies discovered by traversing the knowledge graph
/// from a target node in a specified direction.
/// </summary>
public sealed class DependencyChain
{
    /// <summary>
    /// Gets the target node from which the dependency resolution started.
    /// </summary>
    public required NodeId Target { get; init; }

    /// <summary>
    /// Gets the direction of dependency traversal.
    /// </summary>
    public DependencyDirection Direction { get; init; }

    /// <summary>
    /// Gets the list of dependency steps discovered during traversal.
    /// Each step represents a node reachable within the dependency chain.
    /// </summary>
    public List<DependencyNode> Steps { get; init; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether the full dependency chain was resolved
    /// within the depth limit. Set to <c>false</c> if the traversal exceeded the
    /// maximum depth.
    /// </summary>
    public bool IsComplete { get; set; }

    /// <summary>
    /// Represents a single step (node) in a dependency chain, including the edge
    /// connecting it to its parent in the chain and its depth from the target.
    /// </summary>
    public sealed class DependencyNode
    {
        /// <summary>
        /// Gets the node identifier for this dependency step.
        /// </summary>
        public required NodeId NodeId { get; init; }

        /// <summary>
        /// Gets the graph edge that connects this node to its parent in the chain.
        /// </summary>
        public required GraphEdge Edge { get; init; }

        /// <summary>
        /// Gets or sets the depth of this node from the target node.
        /// The target itself is at depth 0; direct dependencies are at depth 1, and so on.
        /// </summary>
        public int Depth { get; set; }

        /// <summary>
        /// Gets or sets the human-readable label of this node, if available.
        /// </summary>
        public string? Label { get; set; }
    }
}
