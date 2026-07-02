// <copyright file="PlayerSnapshot.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.WorldState.Snapshots;

/// <summary>
/// Snapshot of a player's state at a point in time.
/// </summary>
public class PlayerSnapshot
{
    /// <summary>
    /// Gets or sets the player identifier.
    /// </summary>
    public uint Id { get; set; }

    /// <summary>
    /// Gets or sets the player name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the map identifier where the player is located.
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
    /// Gets or sets the current hit points.
    /// </summary>
    public float HP { get; set; }

    /// <summary>
    /// Gets or sets the current mana points.
    /// </summary>
    public float MP { get; set; }

    /// <summary>
    /// Gets or sets the player level.
    /// </summary>
    public int Level { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the player is alive.
    /// </summary>
    public bool IsAlive { get; set; }
}
