// <copyright file="ItemDroppedEvent.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.WorldState.Events;

using MUnique.OpenMU.WorldState.Core;

/// <summary>
/// Event published when an item or money is dropped on the ground.
/// </summary>
public class ItemDroppedEvent : IWorldEvent
{
    /// <inheritdoc />
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the drop identifier on the map.
    /// </summary>
    public uint DropId { get; set; }

    /// <summary>
    /// Gets or sets the item type (definition number).
    /// -1 when it's a money drop.
    /// </summary>
    public int ItemType { get; set; }

    /// <summary>
    /// Gets or sets the item level. For money drops, this is the amount.
    /// </summary>
    public int ItemLevel { get; set; }

    /// <summary>
    /// Gets or sets the map identifier where the item was dropped.
    /// </summary>
    public int MapId { get; set; }

    /// <summary>
    /// Gets or sets the X position.
    /// </summary>
    public byte X { get; set; }

    /// <summary>
    /// Gets or sets the Y position.
    /// </summary>
    public byte Y { get; set; }

    /// <summary>
    /// Gets or sets the player identifier who dropped the item, if any.
    /// </summary>
    public uint? DropperId { get; set; }
}
