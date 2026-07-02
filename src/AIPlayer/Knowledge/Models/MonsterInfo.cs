// <copyright file="MonsterInfo.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge;

/// <summary>
/// Information about a monster definition, pre-loaded from <see cref="DataModel.Configuration.MonsterDefinition"/>.
/// </summary>
public record MonsterInfo(
    short Number,
    string Name,
    int Level,
    string ObjectKind,
    string NpcWindow,
    byte AttackRange,
    short ViewRange,
    int RespawnDelaySeconds,
    bool HasItemCraftings,
    bool HasQuests,
    List<int> SpawnMapNumbers);
