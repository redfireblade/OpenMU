// <copyright file="MonsterSpawnedEvent.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.WorldState.Events;

using MUnique.OpenMU.WorldState.Core;

/// <summary>
/// Event published when a monster spawns into the game world.
/// </summary>
public class MonsterSpawnedEvent : IWorldEvent
{
    /// <inheritdoc />
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the unique identifier of the spawned monster.
    /// </summary>
    public uint MonsterId { get; set; }

    /// <summary>
    /// Gets or sets the monster definition number.
    /// </summary>
    public int MonsterType { get; set; }

    /// <summary>
    /// Gets or sets the map identifier where the monster spawned.
    /// </summary>
    public int MapId { get; set; }

    /// <summary>
    /// Gets or sets the X position of the spawn location.
    /// </summary>
    public byte X { get; set; }

    /// <summary>
    /// Gets or sets the Y position of the spawn location.
    /// </summary>
    public byte Y { get; set; }
}
