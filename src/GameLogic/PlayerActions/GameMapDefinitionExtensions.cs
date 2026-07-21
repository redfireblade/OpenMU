// <copyright file="GameMapDefinitionExtensions.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions;

using System.Diagnostics.CodeAnalysis;

/// <summary>
/// Extension methods for <see cref="GameMapDefinition"/>.
/// </summary>
public static class GameMapDefinitionExtensions
{
    /// <summary>
    /// Checks the <see cref="GameMapDefinition.MapRequirements"/> for the specified player.
    /// </summary>
    /// <param name="gameMapDefinition">The game map definition.</param>
    /// <param name="player">The player.</param>
    /// <param name="errorMessage">The error message, which is available when this method returns <c>true</c>.</param>
    /// <returns><c>False</c>, if the requirements are fulfilled; Otherwise, <c>true</c>.</returns>
    public static bool TryGetRequirementError(this GameMapDefinition gameMapDefinition, Player player, [MaybeNullWhen(false)] out string errorMessage)
    {
        errorMessage = null;

        if (gameMapDefinition.MapRequirements is null || !gameMapDefinition.MapRequirements.Any())
        {
            return false;
        }

        foreach (var requirement in gameMapDefinition.MapRequirements)
        {
            if (player.Attributes is null || player.Attributes[requirement.Attribute] < requirement.MinimumValue)
            {
                errorMessage = player.GetLocalizedMessage(PlayerMessage.MissingMapRequirement, requirement.Attribute?.Description);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the safezone gate of a map.
    /// </summary>
    /// <param name="gameMapDefinition">The game map definition.</param>
    /// <param name="terrain">The terrain, if available.</param>
    /// <returns>The safezone gate of a map.</returns>
    public static ExitGate? GetSafezoneGate(this GameMapDefinition gameMapDefinition, GameMapTerrain? terrain = null)
    {
        // 优先从 spawn_gates.json 配置读取出生门区域
        var area = SpawnGateConfig.GetSpawnArea(gameMapDefinition.Number);
        if (area != null)
        {
            return new ExitGate
            {
                Map = gameMapDefinition,
                X1 = (byte)area.X1,
                Y1 = (byte)area.Y1,
                X2 = (byte)area.X2,
                Y2 = (byte)area.Y2,
            };
        }

        // 没有配置 → 从 ExitGate 找第一个 IsSpawnGate=true 的门
        return gameMapDefinition.ExitGates?.FirstOrDefault(g => g.IsSpawnGate);
    }
}