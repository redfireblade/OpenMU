// <copyright file="ExecutionGraph.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Orchestrator;

using System.Collections.Immutable;

/// <summary>
/// Directed acyclic graph of execution nodes.
/// Supports topological sort via Kahn's algorithm and cycle detection.
/// </summary>
public sealed class ExecutionGraph
{
    private readonly List<IExecutionNode> _nodes = new();
    private IReadOnlyList<IExecutionNode>? _cachedOrder;

    /// <summary>Gets all registered nodes.</summary>
    public IReadOnlyList<IExecutionNode> Nodes => this._nodes;

    /// <summary>Registers a node. Duplicate names are replaced.</summary>
    public void AddNode(IExecutionNode node)
    {
        this._nodes.RemoveAll(n => n.Name == node.Name);
        this._nodes.Add(node);
        this._cachedOrder = null;
    }

    /// <summary>Registers multiple nodes at once.</summary>
    public void AddRange(IEnumerable<IExecutionNode> nodes)
    {
        foreach (var node in nodes)
        {
            this.AddNode(node);
        }
    }

    /// <summary>
    /// Returns nodes in topological order (dependencies first).
    /// Throws <see cref="InvalidOperationException"/> if a cycle is detected.
    /// Results are cached and invalidated when nodes change.
    /// </summary>
    public IReadOnlyList<IExecutionNode> GetExecutionOrder()
    {
        if (this._cachedOrder is not null)
        {
            return this._cachedOrder;
        }

        // Build adjacency: name → list of dependents (nodes that depend on it)
        var nameToNode = this._nodes.ToDictionary(n => n.Name, n => n);
        var adjacency = new Dictionary<string, List<string>>();
        var inDegree = new Dictionary<string, int>();

        foreach (var node in this._nodes)
        {
            inDegree.TryAdd(node.Name, 0);
            adjacency.TryAdd(node.Name, new List<string>());

            foreach (var dep in node.DependsOn)
            {
                // Edge: dep → node (dep must run before node)
                if (!adjacency.ContainsKey(dep))
                {
                    adjacency[dep] = new List<string>();
                }

                adjacency[dep].Add(node.Name);
                inDegree[node.Name] = inDegree.GetValueOrDefault(node.Name) + 1;
            }
        }

        // Kahn's algorithm
        var queue = new Queue<string>();
        foreach (var (name, degree) in inDegree)
        {
            if (degree == 0)
            {
                queue.Enqueue(name);
            }
        }

        var sorted = new List<IExecutionNode>(this._nodes.Count);
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (nameToNode.TryGetValue(name, out var node))
            {
                sorted.Add(node);
            }

            if (adjacency.TryGetValue(name, out var dependents))
            {
                foreach (var dep in dependents)
                {
                    inDegree[dep]--;
                    if (inDegree[dep] == 0)
                    {
                        queue.Enqueue(dep);
                    }
                }
            }
        }

        if (sorted.Count != this._nodes.Count)
        {
            var missing = this._nodes.Select(n => n.Name).Except(sorted.Select(n => n.Name)).ToList();
            throw new InvalidOperationException(
                $"Cycle detected in execution graph. Nodes involved: {string.Join(", ", missing)}");
        }

        this._cachedOrder = sorted.ToImmutableArray();
        return this._cachedOrder;
    }

    /// <summary>Validates the graph without throwing. Returns true if valid, false with error message if invalid.</summary>
    public bool TryValidate(out string? error)
    {
        try
        {
            this.GetExecutionOrder();
            error = null;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Clears the cached order so the next call to <see cref="GetExecutionOrder"/> recomputes.</summary>
    public void InvalidateCache()
    {
        this._cachedOrder = null;
    }
}
