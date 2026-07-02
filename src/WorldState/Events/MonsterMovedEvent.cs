// <copyright file="MonsterMovedEvent.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.WorldState.Events;

using MUnique.OpenMU.WorldState.Core;

/// <summary>
/// Event published when a monster moves to a new position.
/// </summary>
public class MonsterMovedEvent : IWorldEvent
{
    /// <inheritdoc />
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the monster identifier.
    /// </summary>
    public uint MonsterId { get; set; }

    /// <summary>
    /// Gets or sets the map identifier.
    /// </summary>
    public int MapId { get; set; }

    /// <summary>
    /// Gets or sets the new X position.
    /// </summary>
    public byte X { get; set; }

    /// <summary>
    /// Gets or sets the new Y position.
    /// </summary>
    public byte Y { get; set; }
}
