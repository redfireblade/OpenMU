// <copyright file="SkillResult.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision.Skills;

/// <summary>
/// SKILL 执行结果。
/// </summary>
public enum SkillResult
{
    /// <summary>条件不满足，SKILL 未执行。</summary>
    Skipped,

    /// <summary>SKILL 已执行，但当前 tick 可以继续执行其他 SKILL 或进入决策系统。</summary>
    Executed,

    /// <summary>SKILL 已执行，当前 tick 结束（阻止后续 SKILL 和决策系统）。</summary>
    ExecutedAndStop,
}
