// <copyright file="NativeExecutionService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions.Quests;
using Microsoft.Extensions.Logging;

/// <summary>
/// L3 atomic execution service — calls game public APIs directly, like NPC AI does.
/// No intermediate layers, no packet injection, no simulation.
/// Reference: <see cref="BasicMonsterIntelligence"/> (50 lines does what we used 2000 for).
/// </summary>
public sealed class NativeExecutionService
{
    private readonly Player _player;
    private readonly ILogger _logger;

    public NativeExecutionService(Player player, ILogger logger)
    {
        this._player = player;
        this._logger = logger;
    }

    /// <summary>
    /// Melee physical attack — like Monster.AttackAsync => target.AttackByAsync(this, null, false).
    /// This properly triggers QuestMonsterKillCountPlugIn, unlike packet injection.
    /// </summary>
    public async ValueTask MeleeAttackAsync(IAttackable target)
    {
        if (!target.IsAlive || target is Monster { IsAlive: false }) return;
        this._player.Rotation = this._player.Position.GetDirectionTo(target.Position);
        await target.AttackByAsync(this._player, null, false).ConfigureAwait(false);
    }

    /// <summary>
    /// Skill attack — directly calls target.AttackByAsync with the skill entry.
    /// This path triggers QuestMonsterKillCountPlugIn and all damage pipelines.
    /// </summary>
    public async ValueTask SkillAttackAsync(IAttackable target, SkillEntry skillEntry)
    {
        if (!target.IsAlive || target is Monster { IsAlive: false }) return;
        if (skillEntry.Skill is null) { await this.MeleeAttackAsync(target).ConfigureAwait(false); return; }

        var manaReq = skillEntry.Skill.ConsumeRequirements?.FirstOrDefault(r => r.Attribute == Stats.CurrentMana);
        if (manaReq is not null)
        {
            var currentMana = this._player.Attributes?[Stats.CurrentMana] ?? 0;
            if (currentMana < manaReq.MinimumValue)
            {
                this._logger.LogDebug("[NativeExec] Skill #{Skill} MP cost {Cost} > current {Mp}, fallback to melee",
                    skillEntry.Skill.Number, manaReq.MinimumValue, currentMana);
                await this.MeleeAttackAsync(target).ConfigureAwait(false);
                return;
            }
        }

        this._player.Rotation = this._player.Position.GetDirectionTo(target.Position);
        await target.AttackByAsync(this._player, skillEntry, false).ConfigureAwait(false);
    }

    /// <summary>
    /// Accept a quest — delegates to QuestStartAction.
    /// </summary>
    public async ValueTask<bool> AcceptQuestAsync(short group, short number)
    {
        var action = new QuestStartAction();
        await action.StartQuestAsync(this._player, group, number).ConfigureAwait(false);
        this._logger.LogInformation("[NativeExec] AcceptQuest group={Group}, number={Number}", group, number);
        return true;
    }

    /// <summary>
    /// Complete a quest — delegates to QuestCompletionAction.
    /// </summary>
    public async ValueTask<bool> CompleteQuestAsync(short group, short number)
    {
        var action = new QuestCompletionAction();
        await action.CompleteQuestAsync(this._player, group, number).ConfigureAwait(false);
        this._logger.LogInformation("[NativeExec] CompleteQuest group={Group}, number={Number}", group, number);
        return true;
    }
}
