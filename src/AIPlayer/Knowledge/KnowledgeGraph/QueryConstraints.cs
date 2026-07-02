// <copyright file="QueryConstraints.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge.KnowledgeGraph;

/// <summary>
/// Constraints applied to knowledge graph queries to limit scope and filter results.
/// </summary>
public sealed class QueryConstraints
{
    /// <summary>
    /// Gets the maximum traversal depth (node count limit) for pathfinding.
    /// Defaults to 20 if not set.
    /// </summary>
    public int MaxDepth { get; init; } = 20;

    /// <summary>
    /// Gets the player level used for level-gated filtering.
    /// A value of 0 means no level constraint is applied.
    /// </summary>
    public int PlayerLevel { get; init; }

    /// <summary>
    /// Gets the character class number used for class-gated filtering.
    /// A null value means no class constraint is applied.
    /// </summary>
    public int? PlayerClassNumber { get; init; }

    /// <summary>
    /// Gets an optional edge filter predicate. When set, only edges matching
    /// this predicate are considered during graph traversal.
    /// </summary>
    public Func<GraphEdge, bool>? EdgeFilter { get; init; }
}
