// <copyright file="ISkill.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision.Skills;

using MUnique.OpenMU.GameLogic;

/// <summary>
/// AI 角色的 SKILL 接口 — 一个 SKILL = 条件检测 + 执行脚本。
/// 轻量级、高频、无外部状态（除冷却计时器外），每心跳被 SkillExecutor 遍历。
/// </summary>
public interface ISkill
{
    /// <summary>SKILL 唯一标识。</summary>
    string Id { get; }

    /// <summary>优先级 — 越小越优先执行。</summary>
    int Priority { get; }

    /// <summary>分类标识，用于日志和监控分组。</summary>
    string Category { get; }

    /// <summary>条件检测：当前角色状态是否满足本 SKILL 的执行条件。</summary>
    bool CanExecute(AiPlayer player, IGameAdapter adapter);

    /// <summary>执行本 SKILL 的动作。仅在 CanExecute 返回 true 后调用。</summary>
    /// <param name="player">AI 角色实例。</param>
    /// <param name="adapter">游戏操作适配器。</param>
    /// <param name="cancellationToken">取消令牌 — 超时或关闭时触发。</param>
    ValueTask<SkillResult> ExecuteAsync(AiPlayer player, IGameAdapter adapter, CancellationToken cancellationToken = default);
}
