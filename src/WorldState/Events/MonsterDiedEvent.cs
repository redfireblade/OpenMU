// <copyright file="MonsterDiedEvent.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.WorldState.Events;

using MUnique.OpenMU.WorldState.Core;

/// <summary>
/// Event published when a monster dies.
/// </summary>
public class MonsterDiedEvent : IWorldEvent
{
    /// <inheritdoc />
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the monster identifier.
    /// </summary>
    public uint MonsterId { get; set; }

    /// <summary>
    /// Gets or sets the map identifier where the monster died.
    /// </summary>
    public int MapId { get; set; }

    /// <summary>
    /// Gets or sets the killer's identifier, if known.
    /// </summary>
    public uint? KillerId { get; set; }
}
