// <copyright file="ExecutionContext.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Orchestrator;

/// <summary>Default implementation of <see cref="IExecutionContext"/> with typed state bag and built-in event bus + telemetry.</summary>
public class ExecutionContext : IExecutionContext
{
    private readonly Dictionary<Type, object> _state = new();

    /// <summary>Initializes a new instance of the <see cref="ExecutionContext"/> class.</summary>
    public ExecutionContext()
    {
        this.Events = new EventBus();
        this.Telemetry = new TelemetrySink();
    }

    /// <inheritdoc />
    public IEventBus Events { get; }

    /// <inheritdoc />
    public ITelemetrySink Telemetry { get; }

    /// <inheritdoc />
    public T? GetState<T>()
        where T : class
    {
        return this._state.TryGetValue(typeof(T), out var value) ? value as T : null;
    }

    /// <inheritdoc />
    public void SetState<T>(T value)
        where T : class
    {
        this._state[typeof(T)] = value;
    }
}
