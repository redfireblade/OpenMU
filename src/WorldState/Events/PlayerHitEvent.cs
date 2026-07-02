// <copyright file="PlayerHitEvent.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.WorldState.Events;

using MUnique.OpenMU.WorldState.Core;

/// <summary>
/// Event published when a player receives damage.
/// </summary>
public class PlayerHitEvent : IWorldEvent
{
    /// <inheritdoc />
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the player identifier who was hit.
    /// </summary>
    public uint PlayerId { get; set; }

    /// <summary>
    /// Gets or sets the attacker's identifier.
    /// </summary>
    public uint AttackerId { get; set; }

    /// <summary>
    /// Gets or sets the health damage received.
    /// </summary>
    public uint HealthDamage { get; set; }
}
