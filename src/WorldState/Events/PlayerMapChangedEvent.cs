// <copyright file="PlayerMapChangedEvent.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.WorldState.Events;

using MUnique.OpenMU.WorldState.Core;

/// <summary>
/// Event published when a player enters a map.
/// </summary>
public class PlayerMapChangedEvent : IWorldEvent
{
    /// <inheritdoc />
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the player identifier.
    /// </summary>
    public uint PlayerId { get; set; }

    /// <summary>
    /// Gets or sets the new map identifier.
    /// </summary>
    public int MapId { get; set; }

    /// <summary>
    /// Gets or sets the X position on the new map.
    /// </summary>
    public byte X { get; set; }

    /// <summary>
    /// Gets or sets the Y position on the new map.
    /// </summary>
    public byte Y { get; set; }
}
