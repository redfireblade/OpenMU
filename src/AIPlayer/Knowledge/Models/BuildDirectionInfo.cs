// <copyright file="BuildDirectionInfo.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge;

/// <summary>
/// 发展方向完整定义，包含显示名、基础职业、描述和各阶段加点方案。
/// </summary>
/// <param name="Direction">发展方向枚举。</param>
/// <param name="Name">显示名称(如"智力法师")。</param>
/// <param name="BaseClassNumber">基础职业编号(0/4/8/12/16/20/24)。</param>
/// <param name="Description">一句话描述。</param>
/// <param name="Phases">各阶段加点方案。</param>
public record BuildDirectionInfo(
    BuildDirection Direction,
    string Name,
    int BaseClassNumber,
    string Description,
    BuildPhase[] Phases);
