// <copyright file="IAiService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

/// <summary>
/// Public contract for the AI Player system.
/// Manages the lifecycle of AI-controlled player entities.
/// </summary>
public interface IAiService
{
    /// <summary>
    /// Creates and spawns an AI player with the specified configuration.
    /// </summary>
    /// <param name="config">The configuration for the AI player.</param>
    /// <returns>A result indicating success or failure, with the new player's ID on success.</returns>
    ValueTask<AiPlayerCreateResult> CreateAiPlayerAsync(AiPlayerCreateConfig config);

    /// <summary>
    /// Loads an existing AI player character from the database and spawns it.
    /// Restores execution context (saved goal, pending tasks) for session continuity.
    /// </summary>
    /// <param name="characterName">The name of the existing character to load.</param>
    /// <returns>A result indicating success or failure, with the player's ID on success.</returns>
    ValueTask<AiPlayerCreateResult> LoadAiPlayerAsync(string characterName);

    /// <summary>
    /// Stops and removes an AI player.
    /// </summary>
    /// <param name="playerId">The ID of the AI player to stop.</param>
    /// <returns><c>true</c> if the player was found and stopped; otherwise <c>false</c>.</returns>
    ValueTask<bool> StopAiPlayerAsync(Guid playerId);

    /// <summary>
    /// Sets a target map for the AI player to navigate to.
    /// </summary>
    /// <param name="playerId">The ID of the AI player.</param>
    /// <param name="mapNumber">The target map number.</param>
    /// <returns><c>true</c> if the player was found; otherwise <c>false</c>.</returns>
    ValueTask<bool> SetAiPlayerTargetMapAsync(Guid playerId, ushort mapNumber);

    /// <summary>
    /// Gets a snapshot of all active AI players.
    /// </summary>
    /// <returns>A read-only collection of active AI player states.</returns>
    ValueTask<IReadOnlyCollection<AiPlayerState>> GetAiPlayersAsync();

    /// <summary>
    /// Gets the state of a specific AI player.
    /// </summary>
    /// <param name="playerId">The ID of the AI player.</param>
    /// <returns>The AI player state, or <c>null</c> if not found.</returns>
    ValueTask<AiPlayerState?> GetAiPlayerStateAsync(Guid playerId);
}
