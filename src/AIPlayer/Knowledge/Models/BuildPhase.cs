// <copyright file="BuildPhase.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge;

/// <summary>
/// 加点阶段定义，包含等级范围和四项属性分配权重。
/// </summary>
/// <param name="MinLevel">阶段起始等级。</param>
/// <param name="MaxLevel">阶段结束等级。</param>
/// <param name="StrWeight">力量分配权重(0.0~1.0，同级总和=1)。</param>
/// <param name="AgiWeight">敏捷分配权重。</param>
/// <param name="VitWeight">体力分配权重。</param>
/// <param name="EneWeight">智力分配权重。</param>
/// <param name="Note">该阶段说明。</param>
public record BuildPhase(
    int MinLevel,
    int MaxLevel,
    float StrWeight,
    float AgiWeight,
    float VitWeight,
    float EneWeight,
    string Note);
