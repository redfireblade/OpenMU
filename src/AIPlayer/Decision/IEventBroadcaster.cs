// <copyright file="IEventBroadcaster.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using MUnique.OpenMU.GameLogic.MiniGames;

/// <summary>
/// 事件广播接口 — 由 AiPlayerManager 聚合实现，负责将事件分发到各 AI 角色。
///
/// EventWatcherService（群体级）检测事件状态变更后调用此接口；
/// AiPlayerManager 的 IEventBroadcaster 实现会遍历所有活跃 AI，按等级过滤后分发。
/// 每个 AI 的 HeartbeatService 也实现此接口，接收特定 AI 的事件回调。
/// </summary>
public interface IEventBroadcaster
{
    /// <summary>事件入场窗口开放。</summary>
    void OnEventOpen(MiniGameType type, int gameLevel, string name, int entranceFee);

    /// <summary>事件持续期间定期提醒。</summary>
    void OnEventReminder(MiniGameType type, int gameLevel, string name, int minutesLeft);

    /// <summary>事件已关闭。</summary>
    void OnEventClosed(MiniGameType type, int gameLevel, string name);
}
