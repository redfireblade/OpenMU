// <copyright file="NpcInfo.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge;

/// <summary>
/// Information about a non-player character, pre-loaded from <see cref="DataModel.Configuration.MonsterDefinition"/> and spawn areas.
/// </summary>
public record NpcInfo(
    int Number,
    string Name,
    string NpcWindow,
    int MapNumber,
    int SpawnX,
    int SpawnY,
    byte SpawnRadius);
