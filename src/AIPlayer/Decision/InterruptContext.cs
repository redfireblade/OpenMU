// <copyright file="InterruptContext.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

/// <summary>
/// 中断上下文 — 保存被中断时的看板状态，以便后续恢复。
/// 由 HeartbeatService 维护（MissionBoardService 没有此概念）。
/// </summary>
public sealed class InterruptContext
{
    public string Reason { get; set; } = string.Empty;
    public string? SavedTodoId { get; set; }
}
