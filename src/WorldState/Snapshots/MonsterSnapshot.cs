// <copyright file="MonsterSnapshot.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.WorldState.Snapshots;

/// <summary>
/// Immutable snapshot of a monster's state at a point in time.
/// This is the only representation of a monster visible to the AI runtime —
/// never a direct reference to a game object.
/// </summary>
public class MonsterSnapshot
{
    /// <summary>
    /// Gets or sets the unique identifier of the monster.
    /// </summary>
    public uint Id { get; set; }

    /// <summary>
    /// Gets or sets the monster definition number.
    /// </summary>
    public int MonsterType { get; set; }

    /// <summary>
    /// Gets or sets the map identifier where the monster is located.
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
    /// Gets or sets the maximum hit points.
    /// </summary>
    public float MaxHP { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the monster is alive.
    /// </summary>
    public bool IsAlive { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the monster is in aggressive state.
    /// </summary>
    public bool IsAggressive { get; set; }

    /// <summary>
    /// Gets or sets the target player or monster the monster is currently attacking.
    /// </summary>
    public uint? TargetId { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when this snapshot was last updated.
    /// </summary>
    public DateTime LastSeen { get; set; }
}
