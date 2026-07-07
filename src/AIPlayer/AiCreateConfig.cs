// <copyright file="AiCreateConfig.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

/// <summary>
/// AI 角色创建配置。
/// </summary>
/// <param name="Name">角色名称。</param>
/// <param name="ClassNumber">职业编号 0=DW, 4=DK, 8=Elf。</param>
/// <param name="Level">初始等级。</param>
/// <param name="MapNumber">地图编号。</param>
/// <param name="InitialPosition">初始坐标。</param>
public sealed record AiCreateConfig(
    string Name,
    byte ClassNumber,
    int Level,
    ushort MapNumber,
    MUnique.OpenMU.Pathfinding.Point InitialPosition);
