// <copyright file="SkillExecutor.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision.Skills;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic;
using System.Threading;

/// <summary>
/// SKILL 执行器 — 每心跳遍历所有注册的 SKILL，条件匹配则执行。
/// 替代 HeartbeatService.BeatAsync 中散落的 RuleEngine.Evaluate + switch-case 逻辑。
///
/// 支持两种模式：
///   1. ExecuteAsync — 同步等待执行（旧的保留下）
///   2. FireAndForget — 后台独立运行，不阻塞心跳线程
/// </summary>
public sealed class SkillExecutor
{
    private readonly List<ISkill> _skills;
    private readonly ILogger _logger;

    public SkillExecutor(IEnumerable<ISkill> skills, ILogger logger)
    {
        this._skills = skills.OrderBy(s => s.Priority).ToList();
        this._logger = logger;
    }

    /// <summary>
    /// 同步执行模式（保留兼容性）：遍历 SKILL，匹配就 await 等待执行。
    /// </summary>
    public async ValueTask<bool> ExecuteAsync(AiPlayer player, IGameAdapter adapter)
    {
        var anyExecuted = false;

        foreach (var skill in this._skills)
        {
            if (!skill.CanExecute(player, adapter))
            {
                continue;
            }

            this._logger.LogDebug("[SkillExecutor] 执行 SKILL: {Id}({Category}, P={Priority})",
                skill.Id, skill.Category, skill.Priority);

            var result = await skill.ExecuteAsync(player, adapter).ConfigureAwait(false);

            switch (result)
            {
                case SkillResult.ExecutedAndStop:
                    this._logger.LogDebug("[SkillExecutor] SKILL {Id} 执行完毕(Stop)，结束当前 tick", skill.Id);
                    return true;

                case SkillResult.Executed:
                    this._logger.LogDebug("[SkillExecutor] SKILL {Id} 执行完毕(Continue)", skill.Id);
                    anyExecuted = true;
                    continue;

                case SkillResult.Skipped:
                    continue;
            }
        }

        return anyExecuted;
    }

    /// <summary>
    /// Fire-and-Forget 模式：遍历 SKILL，将匹配的 SKILL 发射到后台 AITaskManager 运行。
    /// 心跳线程不等待任何 SKILL 完成。高优先级 SKILL 发射后停止发射后续 SKILL。
    /// </summary>
    /// <param name="player">当前 AI 角色。</param>
    /// <param name="adapter">游戏操作适配器。</param>
    /// <param name="taskManager">后台任务管理器。</param>
    /// <returns>发射的 SKILL 任务 ID 列表。</returns>
    public List<string> FireAndForget(AiPlayer player, IGameAdapter adapter, AITaskManager taskManager)
    {
        var launched = new List<string>();

        foreach (var skill in this._skills)
        {
            if (!skill.CanExecute(player, adapter))
            {
                continue;
            }

            var skillId = skill.Id;
            this._logger.LogDebug("[SkillExecutor] 发射 SKILL: {Id}({Category}, P={Priority})",
                skillId, skill.Category, skill.Priority);

            var taskId = taskManager.RunTask(
                $"skill_{skillId}",
                async ct => await skill.ExecuteAsync(player, adapter, ct),
                TimeSpan.FromSeconds(10));

            launched.Add(taskId);

            // survival_hp 是最高优先级的阻断型 SKILL，发射后停止发射后续 SKILL
            if (skill is SurvivalHpSkill)
            {
                this._logger.LogDebug("[SkillExecutor] survival_hp 已发射，停止后续 SKILL 发射");
                break;
            }
        }

        return launched;
    }

    /// <summary>返回当前注册的所有 SKILL 列表（用于调试和展示）。</summary>
    public IReadOnlyList<ISkill> GetSkills() => this._skills.AsReadOnly();
}
