// <copyright file="WorldState.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.WorldState.Runtime;

using System.Collections.Generic;
using MUnique.OpenMU.WorldState.Snapshots;

/// <summary>
/// Aggregate root of the digital world mirror.
/// Holds all snapshot collections indexed by map.
/// The single source of truth for the AI runtime's world perception.
/// </summary>
public class WorldState
{
    /// <summary>
    /// Gets or sets the UTC timestamp of the last update.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Gets or sets all monsters across all maps, indexed by monster id.
    /// </summary>
    public Dictionary<uint, MonsterSnapshot> Monsters { get; set; } = new();

    /// <summary>
    /// Gets or sets all players across all maps, indexed by player id.
    /// </summary>
    public Dictionary<uint, PlayerSnapshot> Players { get; set; } = new();

    /// <summary>
    /// Gets or sets all dropped items across all maps, indexed by item id.
    /// </summary>
    public Dictionary<uint, ItemSnapshot> Items { get; set; } = new();

    /// <summary>
    /// Gets or sets all NPCs across all maps, indexed by npc id.
    /// </summary>
    public Dictionary<uint, NPCSnapshot> NPCs { get; set; } = new();

    /// <summary>
    /// Gets or sets per-map runtime state, indexed by map id.
    /// </summary>
    public Dictionary<int, MapRuntimeState> Maps { get; set; } = new();
}
