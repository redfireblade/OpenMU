// <copyright file="IWorldEvent.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.WorldState.Core;

/// <summary>
/// Base interface for all world events flowing from the game server
/// to the WorldState Runtime via the event bus.
/// </summary>
public interface IWorldEvent
{
    /// <summary>
    /// Gets the UTC timestamp when this event was generated.
    /// </summary>
    DateTime Timestamp { get; }
}
