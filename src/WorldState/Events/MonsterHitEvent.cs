// <copyright file="MonsterHitEvent.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.WorldState.Events;

using MUnique.OpenMU.WorldState.Core;

/// <summary>
/// Event published when a monster receives damage.
/// </summary>
public class MonsterHitEvent : IWorldEvent
{
    /// <inheritdoc />
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the monster identifier.
    /// </summary>
    public uint MonsterId { get; set; }

    /// <summary>
    /// Gets or sets the attacker's identifier.
    /// </summary>
    public uint AttackerId { get; set; }

    /// <summary>
    /// Gets or sets the health damage dealt.
    /// </summary>
    public uint HealthDamage { get; set; }

    /// <summary>
    /// Gets or sets the shield damage dealt.
    /// </summary>
    public uint ShieldDamage { get; set; }
}
