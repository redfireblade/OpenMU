// <copyright file="MapInfo.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge;

/// <summary>
/// Information about a game map, pre-loaded from <see cref="DataModel.Configuration.GameMapDefinition"/>.
/// </summary>
public record MapInfo(
    int Number,
    string Name,
    double ExpMultiplier,
    int SafeZoneMapNumber,
    string SafeZoneName,
    int MonsterCount);
