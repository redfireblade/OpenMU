// <copyright file="IEventBus.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Orchestrator;

/// <summary>Pub-sub event bus for decoupled node-to-node communication.</summary>
public interface IEventBus
{
    /// <summary>Subscribes a handler to an event type.</summary>
    void Subscribe(string eventType, Func<ValueTask> handler);

    /// <summary>Publishes an event, invoking all subscribed handlers.</summary>
    ValueTask PublishAsync(string eventType);
}
