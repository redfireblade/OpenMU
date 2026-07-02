// <copyright file="GraphSnapshot.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Orchestrator;

using System.Collections.Immutable;

/// <summary>
/// Immutable snapshot of a single execution cycle.
/// Captures per-node timing, success/failure, and the topological order used.
/// </summary>
public sealed record GraphSnapshot
{
    /// <summary>Cycle sequence number.</summary>
    public required long CycleNumber { get; init; }

    /// <summary>When the cycle executed.</summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>Total duration of all nodes combined.</summary>
    public required TimeSpan TotalDuration { get; init; }

    /// <summary>Per-node execution records.</summary>
    public required IReadOnlyList<INodeExecution> NodeExecutions { get; init; }

    /// <summary>Order in which nodes executed.</summary>
    public required IReadOnlyList<string> ExecutionOrder { get; init; }
}
