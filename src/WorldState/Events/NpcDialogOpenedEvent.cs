// <copyright file="NpcDialogOpenedEvent.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.WorldState.Events;

using MUnique.OpenMU.WorldState.Core;

/// <summary>
/// Event published when a player opens a dialog with an NPC.
/// </summary>
public class NpcDialogOpenedEvent : IWorldEvent
{
    /// <inheritdoc />
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the player identifier.
    /// </summary>
    public uint PlayerId { get; set; }

    /// <summary>
    /// Gets or sets the NPC identifier.
    /// </summary>
    public uint NpcId { get; set; }

    /// <summary>
    /// Gets or sets the NPC type (definition number).
    /// </summary>
    public int NpcType { get; set; }
}
