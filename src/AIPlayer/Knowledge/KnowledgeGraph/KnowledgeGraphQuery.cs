// <copyright file="KnowledgeGraphQuery.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge.KnowledgeGraph;

/// <summary>
/// Provides a clean query interface over the <see cref="KnowledgeGraph"/>,
/// encapsulating pathfinding, reachability analysis, and dependency resolution.
/// </summary>
public interface IKnowledgeGraphQuery
{
    /// <summary>
    /// Gets the underlying knowledge graph for direct access.
    /// </summary>
    KnowledgeGraph Graph { get; }
    /// <summary>
    /// Finds the unweighted shortest path between two nodes.
    /// </summary>
    /// <param name="from">The starting node identifier.</param>
    /// <param name="to">The target node identifier.</param>
    /// <param name="constraints">Optional query constraints.</param>
    /// <returns>A <see cref="PathResult"/> if a path exists; otherwise <c>null</c>.</returns>
    PathResult? FindShortestPath(NodeId from, NodeId to, QueryConstraints? constraints = null);

    /// <summary>
    /// Finds all nodes reachable from <paramref name="start"/> by traversing edges
    /// whose <see cref="GraphEdge.Type"/> is contained in <paramref name="edgeTypes"/>,
    /// within the specified depth limit.
    /// </summary>
    /// <param name="start">The starting node identifier.</param>
    /// <param name="edgeTypes">The set of edge types to follow.</param>
    /// <param name="maxDepth">The maximum traversal depth.</param>
    /// <returns>A list of path results for every reachable node.</returns>
    IReadOnlyList<PathResult> FindAllReachable(NodeId start, IReadOnlySet<EdgeType> edgeTypes, int maxDepth);

    /// <summary>
    /// Resolves the dependency chain for a target node by traversing the graph
    /// in the specified direction.
    /// </summary>
    /// <param name="target">The target node identifier.</param>
    /// <param name="direction">The direction of traversal (forward or backward).</param>
    /// <param name="maxDepth">The maximum traversal depth. Defaults to 20.</param>
    /// <returns>A <see cref="DependencyChain"/> describing the discovered dependencies.</returns>
    DependencyChain ResolveDependencies(NodeId target, DependencyDirection direction, int maxDepth = 20);

    /// <summary>
    /// Resolves the dependency chain with edge type filtering and an optional edge filter.
    /// Only edges whose type is in <paramref name="edgeTypes"/> (if not null) AND
    /// pass the <paramref name="edgeFilter"/> (if not null) are traversed.
    /// </summary>
    /// <param name="target">The target node identifier.</param>
    /// <param name="direction">The direction of traversal.</param>
    /// <param name="edgeTypes">Optional set of allowed edge types. When null, all types are allowed.</param>
    /// <param name="maxDepth">The maximum traversal depth. Defaults to 20.</param>
    /// <param name="edgeFilter">Optional additional edge filter predicate.</param>
    /// <returns>A <see cref="DependencyChain"/> describing the discovered dependencies.</returns>
    DependencyChain ResolveDependencies(NodeId target, DependencyDirection direction, IReadOnlySet<EdgeType>? edgeTypes, int maxDepth = 20, Func<GraphEdge, bool>? edgeFilter = null);

    /// <summary>
    /// Finds the nearest node satisfying the given predicate, reachable from <paramref name="start"/>.
    /// </summary>
    /// <param name="start">The starting node identifier.</param>
    /// <param name="predicate">A predicate that selects the target node.</param>
    /// <param name="constraints">Optional query constraints.</param>
    /// <returns>The nearest matching <see cref="NodeId"/>, or <c>null</c> if none is reachable.</returns>
    NodeId? FindNearest(NodeId start, Func<GraphNode, bool> predicate, QueryConstraints? constraints = null);
}

/// <summary>
/// Default implementation of <see cref="IKnowledgeGraphQuery"/> that delegates
/// to <see cref="KnowledgeGraphPathFinder"/> with sensible defaults.
/// </summary>
public sealed class KnowledgeGraphQuery : IKnowledgeGraphQuery
{
    private readonly KnowledgeGraph _graph;
    private readonly KnowledgeGraphPathFinder _pathFinder;

    /// <summary>
    /// Initializes a new instance of the <see cref="KnowledgeGraphQuery"/> class.
    /// </summary>
    /// <param name="graph">The knowledge graph to query.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="graph"/> is null.</exception>
    public KnowledgeGraphQuery(KnowledgeGraph graph)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _pathFinder = new KnowledgeGraphPathFinder(graph);
    }

    /// <inheritdoc />
    public PathResult? FindShortestPath(NodeId from, NodeId to, QueryConstraints? constraints = null)
    {
        return _pathFinder.Dijkstra(from, to, _ => 1.0, constraints);
    }

    /// <inheritdoc />
    public IReadOnlyList<PathResult> FindAllReachable(
        NodeId start,
        IReadOnlySet<EdgeType> edgeTypes,
        int maxDepth)
    {
        if (edgeTypes is null)
        {
            throw new ArgumentNullException(nameof(edgeTypes));
        }

        return _pathFinder.BfsReachable(start, edge => edgeTypes.Contains(edge.Type), maxDepth);
    }

    /// <inheritdoc />
    public KnowledgeGraph Graph => _graph;

    /// <inheritdoc />
    public DependencyChain ResolveDependencies(
        NodeId target,
        DependencyDirection direction,
        int maxDepth = 20)
    {
        return this.ResolveDependencies(target, direction, edgeTypes: null, maxDepth, edgeFilter: null);
    }

    /// <inheritdoc />
    public DependencyChain ResolveDependencies(
        NodeId target,
        DependencyDirection direction,
        IReadOnlySet<EdgeType>? edgeTypes,
        int maxDepth = 20,
        Func<GraphEdge, bool>? edgeFilter = null)
    {
        var chain = new DependencyChain
        {
            Target = target,
            Direction = direction,
            IsComplete = true,
        };

        var visited = new HashSet<NodeId> { target };
        var queue = new Queue<(NodeId Node, int Depth)>();
        queue.Enqueue((target, 0));

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();

            if (depth >= maxDepth)
            {
                chain.IsComplete = false;
                continue;
            }

            var edges = direction == DependencyDirection.Backward
                ? _graph.GetIncomingEdges(current)
                : _graph.GetOutgoingEdges(current);

            foreach (var edge in edges)
            {
                // Apply edge type filter
                if (edgeTypes is not null && !edgeTypes.Contains(edge.Type))
                {
                    continue;
                }

                // Apply additional edge filter
                if (edgeFilter is not null && !edgeFilter(edge))
                {
                    continue;
                }

                var neighbor = direction == DependencyDirection.Backward
                    ? edge.Source
                    : edge.Target;

                if (!visited.Add(neighbor))
                {
                    continue;
                }

                var label = _graph.TryGetNode(neighbor, out var node) ? node?.Label : null;

                chain.Steps.Add(new DependencyChain.DependencyNode
                {
                    NodeId = neighbor,
                    Edge = edge,
                    Depth = depth + 1,
                    Label = label,
                });

                queue.Enqueue((neighbor, depth + 1));
            }
        }

        return chain;
    }

    /// <inheritdoc />
    public NodeId? FindNearest(
        NodeId start,
        Func<GraphNode, bool> predicate,
        QueryConstraints? constraints = null)
    {
        return _pathFinder.FindNearest(start, predicate, _ => 1.0, constraints);
    }
}
