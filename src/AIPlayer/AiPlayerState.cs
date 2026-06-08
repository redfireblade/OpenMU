// <copyright file="AiPlayerState.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

/// <summary>
/// Snapshot of an AI player's current state.
/// </summary>
/// <param name="PlayerId">The unique ID of this AI player.</param>
/// <param name="CharacterName">The character name.</param>
/// <param name="CurrentMapId">The current map number.</param>
/// <param name="CurrentHealth">The current health points.</param>
/// <param name="MaximumHealth">The maximum health points.</param>
/// <param name="Level">The character level.</param>
/// <param name="StartTimestamp">When the AI player was started.</param>
public sealed record AiPlayerState(
    Guid PlayerId,
    string CharacterName,
    ushort CurrentMapId,
    uint CurrentHealth,
    uint MaximumHealth,
    int Level,
    DateTime StartTimestamp);
