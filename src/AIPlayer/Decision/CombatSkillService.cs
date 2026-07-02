// <copyright file="CombatSkillService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;

/// <summary>
/// Combat skill strategy service — dynamically selects the optimal attack skill
/// (AoE > single-target > melee) and manages buff skill cooldowns.
/// </summary>
public sealed class CombatSkillService
{
    private readonly AiPlayer _player;
    private readonly ILogger _logger;
    private readonly Dictionary<ushort, DateTime> _buffCooldowns = new();
    private readonly string _charTag;

    /// <summary>
    /// Initializes a new instance of the <see cref="CombatSkillService"/> class.
    /// </summary>
    /// <param name="player">The AI player.</param>
    /// <param name="logger">The logger instance.</param>
    public CombatSkillService(AiPlayer player, ILogger logger)
    {
        this._player = player;
        this._logger = logger;
        this._charTag = "[" + (player.SelectedCharacter?.Name ?? "?") + "] ";
    }

    /// <summary>
    /// Selects the optimal attack skill for the current situation.
    /// Priority: AoE (when targetCount >= 2) > single-target DirectHit > null (melee).
    /// Within each category, skills are sorted by AttackDamage descending, then ManaCost ascending.
    /// Skills that the player lacks MP for are skipped.
    /// </summary>
    /// <param name="targetCount">Number of targets in range. When >= 2, AoE skills are preferred.</param>
    /// <returns>The best available skill entry, or null if none is usable.</returns>
    public SkillEntry? SelectAttackSkill(int targetCount = 1)
    {
        var skillList = this._player.SkillList;
        if (skillList is null)
        {
            return null;
        }

        var usableSkills = skillList.Skills
            .Where(s => s.Skill is not null && this.HasSufficientMp(s))
            .ToList();

        if (usableSkills.Count == 0)
        {
            return null;
        }

        // When multiple targets are present, prioritize AoE skills
        if (targetCount >= 2)
        {
            var aoeSkills = usableSkills
                .Where(s => IsAreaSkill(s.Skill!))
                .OrderByDescending(s => s.Skill!.AttackDamage)
                .ThenBy(s => GetManaCost(s.Skill!))
                .ToList();

            if (aoeSkills.Count > 0)
            {
                var selected = aoeSkills[0];
                this._logger.LogDebug(
                    "[CombatSkill]{Tag} Selected AoE skill #{Number} ({Name}) dmg={Damage} mp={Mp} targets={Targets}",
                    this._charTag,
                    selected.Skill!.Number,
                    selected.Skill!.Name,
                    selected.Skill!.AttackDamage,
                    GetManaCost(selected.Skill!),
                    targetCount);
                return selected;
            }
        }

        // Fallback: strongest single-target DirectHit skill
        var directSkills = usableSkills
            .Where(s => s.Skill!.SkillType == SkillType.DirectHit)
            .OrderByDescending(s => s.Skill!.AttackDamage)
            .ThenBy(s => GetManaCost(s.Skill!))
            .ToList();

        if (directSkills.Count > 0)
        {
            var selected = directSkills[0];
            this._logger.LogDebug(
                "[CombatSkill]{Tag} Selected single-target skill #{Number} ({Name}) dmg={Damage} mp={Mp}",
                this._charTag,
                selected.Skill!.Number,
                selected.Skill!.Name,
                selected.Skill!.AttackDamage,
                GetManaCost(selected.Skill!));
            return selected;
        }

        // No DirectHit skill — try any non-buff, non-passive skill as a last resort
        var anyAttack = usableSkills
            .Where(s => s.Skill!.SkillType != SkillType.Buff
                        && s.Skill!.SkillType != SkillType.PassiveBoost
                        && s.Skill!.SkillType != SkillType.Regeneration)
            .OrderByDescending(s => s.Skill!.AttackDamage)
            .ThenBy(s => GetManaCost(s.Skill!))
            .ToList();

        if (anyAttack.Count > 0)
        {
            var selected = anyAttack[0];
            this._logger.LogDebug(
                "[CombatSkill]{Tag} Fallback to skill #{Number} ({Name}) dmg={Damage} type={Type}",
                this._charTag,
                selected.Skill!.Number,
                selected.Skill!.Name,
                selected.Skill!.AttackDamage,
                selected.Skill!.SkillType);
            return selected;
        }

        this._logger.LogTrace("[CombatSkill]{Tag} No usable attack skill found — melee fallback", this._charTag);
        return null;
    }

    /// <summary>
    /// Returns all buff skills that are ready to be cast (cooldown has expired).
    /// </summary>
    /// <returns>A list of buff skill entries whose cooldown has elapsed.</returns>
    public List<SkillEntry> GetBuffsToCast()
    {
        var skillList = this._player.SkillList;
        if (skillList is null)
        {
            return new List<SkillEntry>();
        }

        var now = DateTime.UtcNow;
        return skillList.Skills
            .Where(s => s.Skill?.SkillType == SkillType.Buff
                        && this.HasSufficientMp(s)
                        && (!this._buffCooldowns.TryGetValue((ushort)s.Skill!.Number, out var cooldownUntil)
                            || now >= cooldownUntil))
            .ToList();
    }

    /// <summary>
    /// Records that a buff skill has been cast and sets its cooldown.
    /// </summary>
    /// <param name="skillNumber">The skill number that was cast.</param>
    public void MarkBuffCast(ushort skillNumber)
    {
        this._buffCooldowns[skillNumber] = DateTime.UtcNow.AddSeconds(60);
        this._logger.LogDebug(
            "[CombatSkill]{Tag} Buff #{Number} cast — cooldown until {Until}",
            this._charTag,
            skillNumber,
            this._buffCooldowns[skillNumber]);
    }

    /// <summary>
    /// Resets all buff cooldowns — call when the player respawns/revives
    /// so buffs can be re-applied immediately.
    /// </summary>
    public void ResetCooldowns()
    {
        this._buffCooldowns.Clear();
        this._logger.LogDebug("[CombatSkill]{Tag} All buff cooldowns reset", this._charTag);
    }

    /// <summary>
    /// Determines whether a skill is an area-of-effect (AoE) skill.
    /// AoE skills hit multiple targets automatically or via explicit targeting.
    /// </summary>
    /// <param name="skill">The skill definition.</param>
    /// <returns>True if the skill is an AoE skill.</returns>
    private static bool IsAreaSkill(Skill skill)
    {
        return skill.SkillType switch
        {
            SkillType.AreaSkillAutomaticHits => true,
            SkillType.AreaSkillExplicitHits => true,
            SkillType.AreaSkillExplicitTarget => true,
            _ => skill.ImplicitTargetRange > 0,
        };
    }

    /// <summary>
    /// Checks whether the player has sufficient MP to use this skill.
    /// </summary>
    private bool HasSufficientMp(SkillEntry skillEntry)
    {
        var skill = skillEntry.Skill;
        if (skill is null)
        {
            return false;
        }

        var manaReq = skill.ConsumeRequirements?
            .FirstOrDefault(r => r.Attribute == Stats.CurrentMana);
        if (manaReq is null)
        {
            return true; // no MP cost
        }

        var currentMana = this._player.Attributes?[Stats.CurrentMana] ?? 0;
        return currentMana >= manaReq.MinimumValue;
    }

    /// <summary>
    /// Gets the mana cost of a skill, or 0 if none is defined.
    /// </summary>
    private static int GetManaCost(Skill skill)
    {
        var manaReq = skill.ConsumeRequirements?
            .FirstOrDefault(r => r.Attribute == Stats.CurrentMana);
        return manaReq?.MinimumValue ?? 0;
    }
}
