// <copyright file="IExecutionContext.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Orchestrator;

/// <summary>Shared context passed to every node during an execution cycle.</summary>
public interface IExecutionContext
{
    /// <summary>Retrieves shared state by type. Returns null if no state of this type has been set.</summary>
    T? GetState<T>()
        where T : class;

    /// <summary>Stores shared state by type. Overwrites any existing state of the same type.</summary>
    void SetState<T>(T value)
        where T : class;

    /// <summary>Gets the event bus for pub-sub communication between nodes.</summary>
    IEventBus Events { get; }

    /// <summary>Gets the telemetry sink for recording execution metrics.</summary>
    ITelemetrySink Telemetry { get; }
}
