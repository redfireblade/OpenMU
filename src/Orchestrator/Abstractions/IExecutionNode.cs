// <copyright file="IExecutionNode.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Orchestrator;

/// <summary>Core abstraction: a single executable unit in a DAG pipeline.</summary>
public interface IExecutionNode
{
    /// <summary>Unique node name, used for logging, config lookup, and dependency declarations.</summary>
    string Name { get; }

    /// <summary>Whether this node is enabled. Disabled nodes are skipped during execution.</summary>
    bool Enabled { get; }

    /// <summary>
    /// Names of nodes that must execute before this one.
    /// The graph executor uses this to compute topological order.
    /// Return an empty set when this node has no dependencies.
    /// </summary>
    IReadOnlySet<string> DependsOn { get; }

    /// <summary>Called once per execution cycle. Context provides shared state, events, and telemetry.</summary>
    ValueTask ExecuteAsync(IExecutionContext context);
}
