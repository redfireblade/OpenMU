// <copyright file="MapRuntimeState.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.WorldState.Runtime;

using System.Collections.Generic;
using MUnique.OpenMU.WorldState.Snapshots;

/// <summary>
/// Runtime state for a single map, holding all active snapshots within it.
/// </summary>
public class MapRuntimeState
{
    /// <summary>
    /// Gets or sets the map identifier.
    /// </summary>
    public int MapId { get; set; }

    /// <summary>
    /// Gets or sets the monsters currently on this map.
    /// </summary>
    public Dictionary<uint, MonsterSnapshot> Monsters { get; set; } = new();

    /// <summary>
    /// Gets or sets the players currently on this map.
    /// </summary>
    public Dictionary<uint, PlayerSnapshot> Players { get; set; } = new();

    /// <summary>
    /// Gets or sets the items currently on this map.
    /// </summary>
    public Dictionary<uint, ItemSnapshot> Items { get; set; } = new();

    /// <summary>
    /// Gets or sets the NPCs currently on this map.
    /// </summary>
    public Dictionary<uint, NPCSnapshot> NPCs { get; set; } = new();
}
