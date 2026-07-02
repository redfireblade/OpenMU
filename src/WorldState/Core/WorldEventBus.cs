// <copyright file="WorldEventBus.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.WorldState.Core;

using System.Collections.Generic;

/// <summary>
/// Static event bus for world events flowing from the game server
/// to the WorldState Runtime. Thread-safe for concurrent publish/subscribe.
/// </summary>
public static class WorldEventBus
{
    private static readonly List<Action<IWorldEvent>> Handlers = new();
    private static readonly object Lock = new();

    /// <summary>
    /// Subscribes a handler to receive all world events.
    /// </summary>
    /// <param name="handler">The handler to invoke for each published event.</param>
    public static void Subscribe(Action<IWorldEvent> handler)
    {
        lock (Lock)
        {
            Handlers.Add(handler);
        }
    }

    /// <summary>
    /// Publishes a world event to all subscribed handlers.
    /// </summary>
    /// <param name="worldEvent">The event to publish.</param>
    public static void Publish(IWorldEvent worldEvent)
    {
        Action<IWorldEvent>[] handlers;
        lock (Lock)
        {
            handlers = Handlers.ToArray();
        }

        foreach (var handler in handlers)
        {
            handler(worldEvent);
        }
    }
}
