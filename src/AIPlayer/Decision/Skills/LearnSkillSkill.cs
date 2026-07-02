// <copyright file="LearnSkillSkill.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision.Skills;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic;
using System.Threading;

/// <summary>
/// learn_skill SKILL — 背包有技能书时自动学习。
/// 提取自 HeartbeatService.BeatAsync Step 5.6 (RuleEngine case "learn_skill")。
/// 不阻塞 tick，学技能后继续让其他 SKILL/决策系统执行。
/// </summary>
public sealed class LearnSkillSkill : ISkill
{
    private readonly SkillLearnService _skillLearn;
    private readonly ILogger _logger;

    public LearnSkillSkill(SkillLearnService skillLearn, ILogger logger)
    {
        this._skillLearn = skillLearn;
        this._logger = logger;
    }

    public string Id => "learn_skill";
    public int Priority => 25;
    public string Category => "skill";

    public bool CanExecute(AiPlayer player, IGameAdapter adapter)
    {
        var inv = player.Inventory;
        if (inv is null) return false;

        return inv.Items.Any(i => i.Definition?.Group == 15);
    }

    public async ValueTask<SkillResult> ExecuteAsync(AiPlayer player, IGameAdapter adapter, CancellationToken cancellationToken = default)
    {
        this._logger.LogDebug("[Skill:learn_skill] 执行技能学习检查");
        if (this._skillLearn is not null)
        {
            await this._skillLearn.TryLearnSkillsAsync().ConfigureAwait(false);
        }

        return SkillResult.Executed;
    }
}
