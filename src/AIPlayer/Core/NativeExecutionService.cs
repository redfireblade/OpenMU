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
        var hitInfo = await target.AttackByAsync(this._player, null, false).ConfigureAwait(false);
        this._player.Logger.LogInformation("[P0D] MeleeAttackAsync: target #{Target}({Name}) at ({X},{Y}), hpDmg={HpDmg}, shieldDmg={ShieldDmg}, targetAlive={Alive}",
            target is Monster m ? m.Definition?.Number : 0,
            target is Monster mon ? mon.Definition?.Designation ?? "?" : "?",
            target.Position.X, target.Position.Y,
            hitInfo?.HealthDamage ?? 0, hitInfo?.ShieldDamage ?? 0,
            target.IsAlive);
        await this.EnsureMinimumDamageAsync(target, hitInfo).ConfigureAwait(false);
    }

    /// <summary>
    /// 兜底伤害：如果攻击伤害太低（空手无加点导致），通过 `ApplyBleedingDamageAsync`
    /// 补充额外直接伤害确保怪物能被杀死。
    /// 和 GameAdapter.EnsureMinimumDamageAsync 同步实现（两条攻击路径都需要）。
    /// 不违反 AR-20（IAttackable 公开接口）。
    /// </summary>
    private async ValueTask EnsureMinimumDamageAsync(IAttackable target, HitInfo? hitInfo)
    {
        if (hitInfo is null || target is not Monster monster || !target.IsAlive)
        {
            return;
        }

        var level = this._player.Level;
        var minimumDamage = Math.Max(30, level / 2);

        if (hitInfo.Value.HealthDamage < minimumDamage)
        {
            var bonusDamage = (uint)(minimumDamage - hitInfo.Value.HealthDamage);
            this._player.Logger.LogInformation(
                "[P0D] EnsureMinimumDamageAsync: boosting damage from {ActualDmg} to {MinDmg} (+{Bonus}) for target #{Target}",
                hitInfo.Value.HealthDamage, minimumDamage, bonusDamage,
                monster.Definition?.Number);
            await target.ApplyBleedingDamageAsync(this._player, bonusDamage).ConfigureAwait(false);
        }
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
