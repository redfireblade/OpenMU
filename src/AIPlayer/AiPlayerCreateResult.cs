// <copyright file="AiPlayerCreateResult.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

/// <summary>
/// Result of an AI player creation attempt.
/// </summary>
/// <param name="Success">Whether the AI player was created successfully.</param>
/// <param name="PlayerId">The ID of the created AI player, if successful.</param>
/// <param name="ErrorMessage">An error message, if creation failed.</param>
public sealed record AiPlayerCreateResult(bool Success, Guid? PlayerId, string? ErrorMessage);
