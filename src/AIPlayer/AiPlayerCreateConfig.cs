// <copyright file="AiPlayerCreateConfig.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using MUnique.OpenMU.AIPlayer.Knowledge;

/// <summary>
/// Configuration for creating a new AI player.
/// </summary>
/// <param name="CharacterName">The desired character name.</param>
/// <param name="CharacterClassNumber">The character class number (0=Dark Wizard, 4=Dark Knight, etc.).</param>
/// <param name="MapId">The map number to spawn on (0=Lorencia, etc.).</param>
/// <param name="ScriptPath">Optional path to a behavior script JSON file for 1D script-driven mode.</param>
/// <param name="Direction">Optional build direction for stat allocation and equipment scoring. When null, uses <see cref="StatAllocationStrategy.GetDefaultDirection"/> based on class number.</param>
public sealed record AiPlayerCreateConfig(
    string CharacterName,
    int CharacterClassNumber,
    ushort MapId,
    string? ScriptPath = null,
    BuildDirection? Direction = null,
    int Level = 1);
