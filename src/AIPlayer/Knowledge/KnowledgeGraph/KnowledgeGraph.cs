// <copyright file="KnowledgeGraph.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge.KnowledgeGraph;

using System.Collections.Concurrent;

/// <summary>
/// A thread-safe, directed knowledge graph that stores nodes and edges representing
/// game-world entities and their relationships.
/// </summary>
public sealed class KnowledgeGraph
{
    private readonly ConcurrentDictionary<long, GraphNode> _nodes = new();
    private readonly ConcurrentDictionary<long, List<GraphEdge>> _outgoing = new();
    private readonly ConcurrentDictionary<long, List<GraphEdge>> _incoming = new();

    /// <summary>
    /// Gets the total number of nodes in the graph.
    /// </summary>
    public int NodeCount => _nodes.Count;

    /// <summary>
    /// Gets the total number of edges in the graph, computed as the sum of all outgoing edge lists.
    /// </summary>
    public int EdgeCount => _outgoing.Values.Sum(list => list.Count);

    /// <summary>
    /// Attempts to add a node to the graph.
    /// </summary>
    /// <param name="node">The node to add.</param>
    /// <returns><c>true</c> if the node was added; <c>false</c> if a node with the same <see cref="NodeId"/> already exists.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="node"/> is <c>null</c>.</exception>
    public bool AddNode(GraphNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return _nodes.TryAdd(node.Id.Value, node);
    }

    /// <summary>
    /// Attempts to add a directed edge to the graph.
    /// The edge is added to both the outgoing adjacency list of the source node
    /// and the incoming adjacency list of the target node.
    /// </summary>
    /// <param name="edge">The edge to add.</param>
    /// <returns>
    /// <c>true</c> if the edge was added successfully;
    /// <c>false</c> if either the source or target node does not exist in the graph.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="edge"/> is <c>null</c>.</exception>
    public bool AddEdge(GraphEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);

        if (!_nodes.ContainsKey(edge.Source.Value) || !_nodes.ContainsKey(edge.Target.Value))
        {
            return false;
        }

        AddEdgeToDictionary(_outgoing, edge.Source.Value, edge);
        AddEdgeToDictionary(_incoming, edge.Target.Value, edge);

        return true;
    }

    /// <summary>
    /// Attempts to retrieve a node by its identifier.
    /// </summary>
    /// <param name="id">The node identifier to look up.</param>
    /// <param name="node">When this method returns, contains the node if found; otherwise, <c>null</c>.</param>
    /// <returns><c>true</c> if the node was found; otherwise, <c>false</c>.</returns>
    public bool TryGetNode(NodeId id, out GraphNode? node)
    {
        return _nodes.TryGetValue(id.Value, out node);
    }

    /// <summary>
    /// Gets a node by its identifier.
    /// </summary>
    /// <param name="id">The node identifier to look up.</param>
    /// <returns>The matching <see cref="GraphNode"/>.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when no node exists with the specified <paramref name="id"/>.</exception>
    public GraphNode GetNode(NodeId id)
    {
        if (_nodes.TryGetValue(id.Value, out var node))
        {
            return node;
        }

        throw new KeyNotFoundException($"Node with id '{id}' was not found in the graph.");
    }

    /// <summary>
    /// Determines whether a node with the specified identifier exists in the graph.
    /// </summary>
    /// <param name="id">The node identifier to check.</param>
    /// <returns><c>true</c> if the node exists; otherwise, <c>false</c>.</returns>
    public bool HasNode(NodeId id)
    {
        return _nodes.ContainsKey(id.Value);
    }

    /// <summary>
    /// Returns all outgoing edges from the specified node.
    /// </summary>
    /// <param name="id">The source node identifier.</param>
    /// <returns>A read-only list of outgoing edges, or an empty list if the node has no outgoing edges or does not exist.</returns>
    public IReadOnlyList<GraphEdge> GetOutgoingEdges(NodeId id)
    {
        if (_outgoing.TryGetValue(id.Value, out var edges))
        {
            return edges.ToArray();
        }

        return Array.Empty<GraphEdge>();
    }

    /// <summary>
    /// Returns all incoming edges to the specified node.
    /// </summary>
    /// <param name="id">The target node identifier.</param>
    /// <returns>A read-only list of incoming edges, or an empty list if the node has no incoming edges or does not exist.</returns>
    public IReadOnlyList<GraphEdge> GetIncomingEdges(NodeId id)
    {
        if (_incoming.TryGetValue(id.Value, out var edges))
        {
            return edges.ToArray();
        }

        return Array.Empty<GraphEdge>();
    }

    /// <summary>
    /// Returns all outgoing edges from the specified node that match the given <see cref="EdgeType"/>.
    /// </summary>
    /// <param name="id">The source node identifier.</param>
    /// <param name="type">The edge type to filter by.</param>
    /// <returns>A read-only list of matching outgoing edges, or an empty list if none are found.</returns>
    public IReadOnlyList<GraphEdge> GetOutgoingEdges(NodeId id, EdgeType type)
    {
        if (_outgoing.TryGetValue(id.Value, out var edges))
        {
            return edges.Where(e => e.Type == type).ToArray();
        }

        return Array.Empty<GraphEdge>();
    }

    /// <summary>
    /// Returns all incoming edges to the specified node that match the given <see cref="EdgeType"/>.
    /// </summary>
    /// <param name="id">The target node identifier.</param>
    /// <param name="type">The edge type to filter by.</param>
    /// <returns>A read-only list of matching incoming edges, or an empty list if none are found.</returns>
    public IReadOnlyList<GraphEdge> GetIncomingEdges(NodeId id, EdgeType type)
    {
        if (_incoming.TryGetValue(id.Value, out var edges))
        {
            return edges.Where(e => e.Type == type).ToArray();
        }

        return Array.Empty<GraphEdge>();
    }

    /// <summary>
    /// Finds a single edge matching the given source, target, and type.
    /// </summary>
    /// <param name="source">The source node identifier.</param>
    /// <param name="target">The target node identifier.</param>
    /// <param name="type">The edge type.</param>
    /// <returns>The matching <see cref="GraphEdge"/> if found; otherwise, <c>null</c>.</returns>
    public GraphEdge? GetEdge(NodeId source, NodeId target, EdgeType type)
    {
        if (_outgoing.TryGetValue(source.Value, out var edges))
        {
            return edges.FirstOrDefault(e => e.Target.Equals(target) && e.Type == type);
        }

        return null;
    }

    /// <summary>
    /// Atomically updates the weight of an edge matching the given source, target, and type.
    /// The update is performed on the outgoing adjacency list. The incoming list is also
    /// updated best-effort.
    /// </summary>
    /// <param name="source">The source node identifier.</param>
    /// <param name="target">The target node identifier.</param>
    /// <param name="type">The edge type.</param>
    /// <param name="newWeight">The new weight value to assign to the edge.</param>
    /// <returns>
    /// <c>true</c> if the edge was found and its weight was updated;
    /// <c>false</c> if no matching edge exists.
    /// </returns>
    public bool UpdateEdgeWeight(NodeId source, NodeId target, EdgeType type, double newWeight)
    {
        if (!_outgoing.TryGetValue(source.Value, out var initialEdges))
        {
            return false;
        }

        // Try to find and update the outgoing edge atomically.
        var updatedOutgoing = TryUpdateEdgeInList(initialEdges, source, target, type, newWeight);
        if (updatedOutgoing is null)
        {
            return false; // Edge not found.
        }

        // CAS loop to replace the outgoing edge list.
        var currentEdges = initialEdges;
        while (!_outgoing.TryUpdate(source.Value, updatedOutgoing, currentEdges))
        {
            currentEdges = _outgoing[source.Value];
            updatedOutgoing = TryUpdateEdgeInList(currentEdges, source, target, type, newWeight);
            if (updatedOutgoing is null)
            {
                return false; // Edge was removed by another thread.
            }
        }

        // Best-effort update to the incoming adjacency list.
        TryUpdateIncomingEdgeWeight(source, target, type, newWeight);

        return true;
    }

    /// <summary>
    /// Gets all nodes currently stored in the graph.
    /// </summary>
    /// <returns>A read-only collection of all graph nodes.</returns>
    public IReadOnlyCollection<GraphNode> GetAllNodes()
    {
        return _nodes.Values.ToArray();
    }

    /// <summary>
    /// Removes all nodes and edges from the graph.
    /// </summary>
    public void Clear()
    {
        _nodes.Clear();
        _outgoing.Clear();
        _incoming.Clear();
    }

    /// <summary>
    /// Attempts to update the weight of an edge in the incoming adjacency list.
    /// This is a best-effort operation; failures are silently ignored.
    /// </summary>
    private void TryUpdateIncomingEdgeWeight(NodeId source, NodeId target, EdgeType type, double newWeight)
    {
        if (!_incoming.TryGetValue(target.Value, out var initialEdges))
        {
            return;
        }

        var updatedIncoming = TryUpdateEdgeInList(initialEdges, source, target, type, newWeight);
        if (updatedIncoming is null)
        {
            return;
        }

        // CAS loop for incoming as well.
        var currentEdges = initialEdges;
        while (!_incoming.TryUpdate(target.Value, updatedIncoming, currentEdges))
        {
            currentEdges = _incoming[target.Value];
            updatedIncoming = TryUpdateEdgeInList(currentEdges, source, target, type, newWeight);
            if (updatedIncoming is null)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Creates a new list with the edge weight updated, or returns <c>null</c> if the edge is not found.
    /// </summary>
    private static List<GraphEdge>? TryUpdateEdgeInList(List<GraphEdge> edges, NodeId source, NodeId target, EdgeType type, double newWeight)
    {
        var index = edges.FindIndex(e => e.Source.Equals(source) && e.Target.Equals(target) && e.Type == type);
        if (index < 0)
        {
            return null;
        }

        var newList = new List<GraphEdge>(edges);
        newList[index] = newList[index] with { Weight = newWeight };
        return newList;
    }

    /// <summary>
    /// Atomically appends an edge to the adjacency list for the given key using a CAS loop.
    /// </summary>
    private static void AddEdgeToDictionary(ConcurrentDictionary<long, List<GraphEdge>> dict, long key, GraphEdge edge)
    {
        while (true)
        {
            if (!dict.TryGetValue(key, out var currentList))
            {
                currentList = dict.GetOrAdd(key, _ => new List<GraphEdge>());
            }

            // Build a new list that contains the existing edges plus the new edge.
            var newList = currentList.Count == 0
                ? new List<GraphEdge> { edge }
                : new List<GraphEdge>(currentList) { edge };

            // Atomically replace the list if it hasn't changed since we read it.
            if (dict.TryUpdate(key, newList, currentList))
            {
                break;
            }
        }
    }
}
