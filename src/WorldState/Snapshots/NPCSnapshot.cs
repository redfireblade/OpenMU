// <copyright file="NPCSnapshot.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.WorldState.Snapshots;

/// <summary>
/// Snapshot of an NPC's state at a point in time.
/// </summary>
public class NPCSnapshot
{
    /// <summary>
    /// Gets or sets the NPC identifier.
    /// </summary>
    public uint Id { get; set; }

    /// <summary>
    /// Gets or sets the NPC definition number.
    /// </summary>
    public int NPCType { get; set; }

    /// <summary>
    /// Gets or sets the map identifier where the NPC is located.
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
    /// Gets or sets a value indicating whether the NPC is alive (destructible NPCs).
    /// </summary>
    public bool IsAlive { get; set; }
}
