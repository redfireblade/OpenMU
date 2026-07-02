// <copyright file="ItemSnapshot.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.WorldState.Snapshots;

/// <summary>
/// Snapshot of a dropped item's state at a point in time.
/// </summary>
public class ItemSnapshot
{
    /// <summary>
    /// Gets or sets the item identifier.
    /// </summary>
    public uint Id { get; set; }

    /// <summary>
    /// Gets or sets the item definition number.
    /// </summary>
    public int ItemType { get; set; }

    /// <summary>
    /// Gets or sets the map identifier where the item is located.
    /// </summary>
    public int MapId { get; set; }

    /// <summary>
    /// Gets or sets the X position.
    /// </summary>
    public int X { get; set; }

    /// <summary>
    /// Gets or sets the Y position.
    /// </summary>
    public int Y { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the item has been picked up.
    /// </summary>
    public bool IsPicked { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when the item was dropped.
    /// </summary>
    public DateTime DropTime { get; set; }
}
