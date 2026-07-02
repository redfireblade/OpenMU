// <copyright file="DefaultMuHelperSettings.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using MUnique.OpenMU.GameLogic.MuHelper;

/// <summary>
/// Default implementation of <see cref="IMuHelperSettings"/> for AI characters.
/// Enables all essential auto-bot features: attack, heal, pickup, buff, repair.
/// </summary>
public sealed class DefaultMuHelperSettings : IMuHelperSettings
{
    /// <inheritdoc />
    public int BasicSkillId => 0;
    /// <inheritdoc />
    public int ActivationSkill1Id => 0;
    /// <inheritdoc />
    public int ActivationSkill2Id => 0;
    /// <inheritdoc />
    public int DelayMinSkill1 => 0;
    /// <inheritdoc />
    public int DelayMaxSkill1 => 0;
    /// <inheritdoc />
    public int DelayMinSkill2 => 0;
    /// <inheritdoc />
    public int DelayMaxSkill2 => 0;
    /// <inheritdoc />
    public bool Skill1UseTimer => false;
    /// <inheritdoc />
    public bool Skill1UseCondition => false;
    /// <inheritdoc />
    public bool Skill1ConditionAttacking => false;
    /// <inheritdoc />
    public int Skill1SubCondition => 0;
    /// <inheritdoc />
    public bool Skill2UseTimer => false;
    /// <inheritdoc />
    public bool Skill2UseCondition => false;
    /// <inheritdoc />
    public bool Skill2ConditionAttacking => false;
    /// <inheritdoc />
    public int Skill2SubCondition => 0;
    /// <inheritdoc />
    public bool UseCombo => false;
    /// <inheritdoc />
    public int HuntingRange => 15;
    /// <inheritdoc />
    public int MaxSecondsAway => 30;
    /// <inheritdoc />
    public bool LongRangeCounterAttack => true;
    /// <inheritdoc />
    public bool ReturnToOriginalPosition => true;
    /// <inheritdoc />
    public int BuffSkill0Id => 0;
    /// <inheritdoc />
    public int BuffSkill1Id => 0;
    /// <inheritdoc />
    public int BuffSkill2Id => 0;
    /// <inheritdoc />
    public bool BuffOnDuration => true;
    /// <inheritdoc />
    public bool BuffDurationForParty => false;
    /// <inheritdoc />
    public int BuffCastIntervalSeconds => 120;
    /// <inheritdoc />
    public bool AutoHeal => true;
    /// <inheritdoc />
    public int HealThresholdPercent => 50;
    /// <inheritdoc />
    public bool UseDrainLife => false;
    /// <inheritdoc />
    public bool UseHealPotion => true;
    /// <inheritdoc />
    public int PotionThresholdPercent => 60;
    /// <inheritdoc />
    public bool SupportParty => false;
    /// <inheritdoc />
    public bool AutoHealParty => false;
    /// <inheritdoc />
    public int HealPartyThresholdPercent => 50;
    /// <inheritdoc />
    public bool UseDarkRaven => false;
    /// <inheritdoc />
    public int DarkRavenMode => 0;
    /// <inheritdoc />
    public int ObtainRange => 15;
    /// <inheritdoc />
    public bool PickAllItems => true;
    /// <inheritdoc />
    public bool PickSelectItems => false;
    /// <inheritdoc />
    public bool PickJewel => true;
    /// <inheritdoc />
    public bool PickZen => true;
    /// <inheritdoc />
    public bool PickAncient => true;
    /// <inheritdoc />
    public bool PickExcellent => true;
    /// <inheritdoc />
    public bool PickExtraItems => false;
    /// <inheritdoc />
    public IReadOnlyList<string> ExtraItemNames => Array.Empty<string>();
    /// <inheritdoc />
    public bool RepairItem => true;
}
