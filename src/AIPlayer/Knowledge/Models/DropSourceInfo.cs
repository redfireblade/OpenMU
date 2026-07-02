// <copyright file="DropSourceInfo.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge;

/// <summary>
/// Information about a drop source: which monster on which map drops an item at a specific level.
/// </summary>
public record DropSourceInfo(
    int ItemGroup,
    int ItemNumber,
    short MonsterNumber,
    string MonsterName,
    ushort MapNumber,
    string MapName,
    byte ItemLevel,
    double DropRate);
