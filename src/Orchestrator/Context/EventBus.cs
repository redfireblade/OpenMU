// <copyright file="EventBus.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Orchestrator;

/// <summary>Default implementation of <see cref="IEventBus"/> with concurrent-safe handler invocation.</summary>
public sealed class EventBus : IEventBus
{
    private readonly Dictionary<string, List<Func<ValueTask>>> _handlers = new();

    /// <inheritdoc />
    public void Subscribe(string eventType, Func<ValueTask> handler)
    {
        lock (this._handlers)
        {
            if (!this._handlers.TryGetValue(eventType, out var list))
            {
                list = new List<Func<ValueTask>>();
                this._handlers[eventType] = list;
            }

            list.Add(handler);
        }
    }

    /// <inheritdoc />
    public async ValueTask PublishAsync(string eventType)
    {
        List<Func<ValueTask>>? handlers;
        lock (this._handlers)
        {
            if (!this._handlers.TryGetValue(eventType, out handlers))
            {
                return;
            }
        }

        foreach (var handler in handlers)
        {
            await handler().ConfigureAwait(false);
        }
    }
}
