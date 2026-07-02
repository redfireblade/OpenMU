// <copyright file="TelemetrySink.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Orchestrator;

using System.Collections.Immutable;

/// <summary>Default implementation of <see cref="ITelemetrySink"/> with bounded history.</summary>
public sealed class TelemetrySink : ITelemetrySink
{
    private readonly List<NodeExecution> _currentCycle = new();
    private readonly List<CycleExecution> _history = new();
    private const int MaxHistory = 1024;

    /// <inheritdoc />
    public void OnNodeStarted(string nodeName)
    {
        // No-op in default implementation. Override for pre-execution hooks.
    }

    /// <inheritdoc />
    public void OnNodeCompleted(string nodeName, TimeSpan duration, Exception? error = null)
    {
        this._currentCycle.Add(new NodeExecution(nodeName, duration, error));
    }

    /// <inheritdoc />
    public IReadOnlyList<INodeExecution> GetCurrentCycle()
    {
        return this._currentCycle.ToImmutableArray();
    }

    /// <inheritdoc />
    public IReadOnlyList<ICycleExecution> GetHistory()
    {
        return this._history.ToImmutableArray();
    }

    /// <summary>Finalizes the current cycle and moves it to history.</summary>
    internal void CompleteCycle(long cycleNumber, DateTime startedAt)
    {
        var totalDuration = TimeSpan.Zero;
        foreach (var exec in this._currentCycle)
        {
            totalDuration += exec.Duration;
        }

        var cycle = new CycleExecution(cycleNumber, startedAt, totalDuration, this._currentCycle.ToImmutableArray());
        if (this._history.Count >= MaxHistory)
        {
            this._history.RemoveAt(0);
        }

        this._history.Add(cycle);
    }

    /// <summary>Returns the current cycle's records and clears it. Called by the coordinator after each execution cycle.</summary>
    public IReadOnlyList<INodeExecution> DrainCurrentCycle()
    {
        var result = this._currentCycle.ToImmutableArray();
        this._currentCycle.Clear();
        return result;
    }

    /// <summary>Internal record for a single node execution.</summary>
    private sealed record NodeExecution(string NodeName, TimeSpan Duration, Exception? Error) : INodeExecution;

    /// <summary>Internal record for a full execution cycle.</summary>
    private sealed record CycleExecution(long CycleNumber, DateTime StartedAt, TimeSpan TotalDuration, IReadOnlyList<INodeExecution> NodeExecutions) : ICycleExecution;
}
