// <copyright file="RuleMatchResult.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

/// <summary>
/// 规则匹配结果 — 由 RuleEngine.Evaluate 返回。
/// 包含匹配的规则定义以及生成的任务项（供 AIStateMachineExecutor 执行）。
/// </summary>
/// <param name="Rule">匹配的规则定义。</param>
/// <param name="GeneratedMission">规则匹配后生成的任务项（可能为 null）。</param>
public sealed record RuleMatchResult(RuleDef Rule, MissionItem? GeneratedMission);
