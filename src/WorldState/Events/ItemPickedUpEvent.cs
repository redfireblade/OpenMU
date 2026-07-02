// <copyright file="ItemPickedUpEvent.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.WorldState.Events;

using MUnique.OpenMU.WorldState.Core;

/// <summary>
/// Event published when an item or money is picked up from the ground.
/// </summary>
public class ItemPickedUpEvent : IWorldEvent
{
    /// <inheritdoc />
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the drop identifier of the picked-up object.
    /// </summary>
    public uint DropId { get; set; }

    /// <summary>
    /// Gets or sets the player identifier who picked up the item.
    /// </summary>
    public uint PlayerId { get; set; }

    /// <summary>
    /// Gets or sets the map identifier.
    /// </summary>
    public int MapId { get; set; }
}
