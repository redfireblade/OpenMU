// <copyright file="ITelemetrySink.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Orchestrator;

/// <summary>Telemetry sink for recording per-node execution metrics.</summary>
public interface ITelemetrySink
{
    /// <summary>Called by the executor before each node's ExecuteAsync.</summary>
    void OnNodeStarted(string nodeName);

    /// <summary>Called by the executor after each node's ExecuteAsync completes (success or failure).</summary>
    void OnNodeCompleted(string nodeName, TimeSpan duration, Exception? error = null);

    /// <summary>Returns a snapshot of all recorded node executions in the current cycle.</summary>
    IReadOnlyList<INodeExecution> GetCurrentCycle();

    /// <summary>Returns execution history across multiple cycles.</summary>
    IReadOnlyList<ICycleExecution> GetHistory();
}

/// <summary>Record of a single node execution.</summary>
public interface INodeExecution
{
    /// <summary>Node name.</summary>
    string NodeName { get; }

    /// <summary>Wall-clock duration.</summary>
    TimeSpan Duration { get; }

    /// <summary>Exception if the node failed, null on success.</summary>
    Exception? Error { get; }
}

/// <summary>Record of a single execution cycle (all nodes run).</summary>
public interface ICycleExecution
{
    /// <summary>Cycle sequence number.</summary>
    long CycleNumber { get; }

    /// <summary>When the cycle started.</summary>
    DateTime StartedAt { get; }

    /// <summary>Total duration of all nodes combined.</summary>
    TimeSpan TotalDuration { get; }

    /// <summary>Per-node records.</summary>
    IReadOnlyList<INodeExecution> NodeExecutions { get; }
}
