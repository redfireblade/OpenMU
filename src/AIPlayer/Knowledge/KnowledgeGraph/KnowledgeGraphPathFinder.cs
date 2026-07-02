// <copyright file="KnowledgeGraphPathFinder.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge.KnowledgeGraph;

/// <summary>
/// Provides graph pathfinding algorithms (Dijkstra, BFS, nearest-neighbor, multi-source)
/// over an <see cref="KnowledgeGraph"/>.
/// </summary>
public sealed class KnowledgeGraphPathFinder
{
    private readonly KnowledgeGraph _graph;

    /// <summary>
    /// Initializes a new instance of the <see cref="KnowledgeGraphPathFinder"/> class.
    /// </summary>
    /// <param name="graph">The knowledge graph to operate on.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="graph"/> is null.</exception>
    public KnowledgeGraphPathFinder(KnowledgeGraph graph)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
    }

    /// <summary>
    /// Finds the shortest path between <paramref name="from"/> and <paramref name="to"/>
    /// using Dijkstra's algorithm with a custom weight function.
    /// </summary>
    /// <param name="from">The starting node identifier.</param>
    /// <param name="to">The target node identifier.</param>
    /// <param name="weightFunc">A function that returns the traversal weight of an edge.</param>
    /// <param name="constraints">Optional query constraints (max depth, edge filter, etc.).</param>
    /// <returns>A <see cref="PathResult"/> if a path is found; otherwise <c>null</c>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="weightFunc"/> is null.</exception>
    public PathResult? Dijkstra(
        NodeId from,
        NodeId to,
        Func<GraphEdge, double> weightFunc,
        QueryConstraints? constraints = null)
    {
        if (weightFunc is null)
        {
            throw new ArgumentNullException(nameof(weightFunc));
        }

        constraints ??= new QueryConstraints();

        if (!_graph.HasNode(from) || !_graph.HasNode(to))
        {
            return null;
        }

        var distances = new Dictionary<NodeId, double>();
        var predecessors = new Dictionary<NodeId, (GraphEdge Edge, NodeId Previous)>();
        var nodeDepths = new Dictionary<NodeId, int>();

        // SortedSet with a unique counter as tiebreaker to avoid ordering issues with NodeId.
        var queue = new SortedSet<(double Distance, long TieBreaker, NodeId Node)>();
        var counter = 0L;

        distances[from] = 0;
        nodeDepths[from] = 1;
        queue.Add((0, counter++, from));

        while (queue.Count > 0)
        {
            var min = queue.Min;
            queue.Remove(min);
            var (currentDist, _, current) = min;

            // Skip stale queue entries (a better distance was found and added later).
            if (distances.TryGetValue(current, out var bestDist) && currentDist > bestDist)
            {
                continue;
            }

            if (current.Equals(to))
            {
                return ReconstructPath(predecessors, distances, to);
            }

            var outgoingEdges = constraints.EdgeFilter is null
                ? _graph.GetOutgoingEdges(current)
                : _graph.GetOutgoingEdges(current).Where(constraints.EdgeFilter);

            foreach (var edge in outgoingEdges)
            {
                var neighbor = edge.Target;
                var edgeWeight = weightFunc(edge);
                var newDist = currentDist + edgeWeight;
                var newDepth = nodeDepths[current] + 1;

                if (newDepth > constraints.MaxDepth)
                {
                    continue;
                }

                if (!distances.ContainsKey(neighbor) || newDist < distances[neighbor])
                {
                    distances[neighbor] = newDist;
                    nodeDepths[neighbor] = newDepth;
                    predecessors[neighbor] = (edge, current);
                    queue.Add((newDist, counter++, neighbor));
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Performs a BFS (breadth-first search) from <paramref name="start"/> to discover all
    /// reachable nodes within <paramref name="maxDepth"/> whose connecting edges match
    /// the specified <paramref name="edgeFilter"/>.
    /// </summary>
    /// <param name="start">The starting node identifier.</param>
    /// <param name="edgeFilter">A predicate that filters which edges to follow.</param>
    /// <param name="maxDepth">The maximum traversal depth (node count limit).</param>
    /// <returns>A list of <see cref="PathResult"/> for every reachable node.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="edgeFilter"/> is null.</exception>
    public List<PathResult> BfsReachable(
        NodeId start,
        Func<GraphEdge, bool> edgeFilter,
        int maxDepth)
    {
        if (edgeFilter is null)
        {
            throw new ArgumentNullException(nameof(edgeFilter));
        }

        var results = new List<PathResult>();
        var visited = new HashSet<NodeId> { start };
        var predecessors = new Dictionary<NodeId, (GraphEdge Edge, NodeId Previous)>();
        var distances = new Dictionary<NodeId, double> { [start] = 0 };
        var queue = new Queue<(NodeId Node, int Depth)>();
        queue.Enqueue((start, 1));

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();

            if (depth > maxDepth)
            {
                continue;
            }

            var edges = _graph.GetOutgoingEdges(current).Where(edgeFilter);

            foreach (var edge in edges)
            {
                var neighbor = edge.Target;

                if (!visited.Add(neighbor))
                {
                    continue;
                }

                predecessors[neighbor] = (edge, current);
                distances[neighbor] = distances[current] + 1;
                queue.Enqueue((neighbor, depth + 1));

                if (depth + 1 <= maxDepth)
                {
                    var path = ReconstructPath(predecessors, distances, neighbor);
                    results.Add(path);
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Finds the nearest node satisfying the given <paramref name="predicate"/>,
    /// reachable from <paramref name="start"/> within the specified constraints.
    /// Uses Dijkstra's algorithm and stops as soon as a matching node is found.
    /// </summary>
    /// <param name="start">The starting node identifier.</param>
    /// <param name="predicate">A predicate that selects the target node.</param>
    /// <param name="weightFunc">A function that returns the traversal weight of an edge.</param>
    /// <param name="constraints">Optional query constraints.</param>
    /// <returns>The nearest <see cref="NodeId"/> matching the predicate, or <c>null</c> if none is reachable.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="predicate"/> or <paramref name="weightFunc"/> is null.</exception>
    public NodeId? FindNearest(
        NodeId start,
        Func<GraphNode, bool> predicate,
        Func<GraphEdge, double> weightFunc,
        QueryConstraints? constraints = null)
    {
        if (predicate is null)
        {
            throw new ArgumentNullException(nameof(predicate));
        }

        if (weightFunc is null)
        {
            throw new ArgumentNullException(nameof(weightFunc));
        }

        constraints ??= new QueryConstraints();

        if (!_graph.HasNode(start))
        {
            return null;
        }

        // Check the starting node first.
        if (_graph.TryGetNode(start, out var startNode) && startNode is not null && predicate(startNode))
        {
            return start;
        }

        var distances = new Dictionary<NodeId, double>();
        var nodeDepths = new Dictionary<NodeId, int>();
        var queue = new SortedSet<(double Distance, long TieBreaker, NodeId Node)>();
        var counter = 0L;

        distances[start] = 0;
        nodeDepths[start] = 1;
        queue.Add((0, counter++, start));

        while (queue.Count > 0)
        {
            var min = queue.Min;
            queue.Remove(min);
            var (currentDist, _, current) = min;

            if (distances.TryGetValue(current, out var bestDist) && currentDist > bestDist)
            {
                continue;
            }

            var outgoingEdges = constraints.EdgeFilter is null
                ? _graph.GetOutgoingEdges(current)
                : _graph.GetOutgoingEdges(current).Where(constraints.EdgeFilter);

            foreach (var edge in outgoingEdges)
            {
                var neighbor = edge.Target;
                var edgeWeight = weightFunc(edge);
                var newDist = currentDist + edgeWeight;
                var newDepth = nodeDepths[current] + 1;

                if (newDepth > constraints.MaxDepth)
                {
                    continue;
                }

                if (!distances.ContainsKey(neighbor) || newDist < distances[neighbor])
                {
                    distances[neighbor] = newDist;
                    nodeDepths[neighbor] = newDepth;
                    queue.Add((newDist, counter++, neighbor));

                    if (_graph.TryGetNode(neighbor, out var node) && node is not null && predicate(node))
                    {
                        return neighbor;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the shortest path from any of the provided <paramref name="sources"/>
    /// to the specified <paramref name="target"/> using multi-source Dijkstra.
    /// </summary>
    /// <param name="sources">An enumeration of possible starting nodes.</param>
    /// <param name="target">The target node identifier.</param>
    /// <param name="weightFunc">A function that returns the traversal weight of an edge.</param>
    /// <param name="constraints">Optional query constraints.</param>
    /// <returns>A <see cref="PathResult"/> representing the best path, or <c>null</c> if no path exists.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="sources"/> or <paramref name="weightFunc"/> is null.</exception>
    public PathResult? MultiSourceShortestPath(
        IEnumerable<NodeId> sources,
        NodeId target,
        Func<GraphEdge, double> weightFunc,
        QueryConstraints? constraints = null)
    {
        if (sources is null)
        {
            throw new ArgumentNullException(nameof(sources));
        }

        if (weightFunc is null)
        {
            throw new ArgumentNullException(nameof(weightFunc));
        }

        constraints ??= new QueryConstraints();

        if (!_graph.HasNode(target))
        {
            return null;
        }

        var distances = new Dictionary<NodeId, double>();
        var predecessors = new Dictionary<NodeId, (GraphEdge Edge, NodeId Previous)>();
        var nodeDepths = new Dictionary<NodeId, int>();
        var queue = new SortedSet<(double Distance, long TieBreaker, NodeId Node)>();
        var counter = 0L;

        var hasValidSource = false;
        foreach (var source in sources)
        {
            if (!_graph.HasNode(source))
            {
                continue;
            }

            hasValidSource = true;
            distances[source] = 0;
            nodeDepths[source] = 1;
            queue.Add((0, counter++, source));
        }

        if (!hasValidSource)
        {
            return null;
        }

        while (queue.Count > 0)
        {
            var min = queue.Min;
            queue.Remove(min);
            var (currentDist, _, current) = min;

            if (distances.TryGetValue(current, out var bestDist) && currentDist > bestDist)
            {
                continue;
            }

            if (current.Equals(target))
            {
                return ReconstructPath(predecessors, distances, target);
            }

            var outgoingEdges = constraints.EdgeFilter is null
                ? _graph.GetOutgoingEdges(current)
                : _graph.GetOutgoingEdges(current).Where(constraints.EdgeFilter);

            foreach (var edge in outgoingEdges)
            {
                var neighbor = edge.Target;
                var edgeWeight = weightFunc(edge);
                var newDist = currentDist + edgeWeight;
                var newDepth = nodeDepths[current] + 1;

                if (newDepth > constraints.MaxDepth)
                {
                    continue;
                }

                if (!distances.ContainsKey(neighbor) || newDist < distances[neighbor])
                {
                    distances[neighbor] = newDist;
                    nodeDepths[neighbor] = newDepth;
                    predecessors[neighbor] = (edge, current);
                    queue.Add((newDist, counter++, neighbor));
                }
            }
        }

        return null;
    }

    private static PathResult ReconstructPath(
        Dictionary<NodeId, (GraphEdge Edge, NodeId Previous)> predecessors,
        Dictionary<NodeId, double> distances,
        NodeId to)
    {
        var edges = new List<GraphEdge>();
        var current = to;

        while (predecessors.TryGetValue(current, out var pred))
        {
            edges.Add(pred.Edge);
            current = pred.Previous;
        }

        edges.Reverse();

        var from = edges.Count > 0 ? edges[0].Source : to;

        return new PathResult
        {
            From = from,
            To = to,
            Edges = edges.AsReadOnly(),
            TotalWeight = distances.GetValueOrDefault(to, 0),
        };
    }
}
