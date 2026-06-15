// <copyright file="QuestMonsterKillCountPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns;

using System.ComponentModel.DataAnnotations;
using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// This plugin increases the monster kill count of the quest state of active quests.
/// </summary>
[PlugIn]
[Display(Name = nameof(PlugInResources.QuestMonsterKillCountPlugIn_Name), Description = nameof(PlugInResources.QuestMonsterKillCountPlugIn_Description), ResourceType = typeof(PlugInResources))]
[Guid("416C2231-D7FE-414A-9321-26622E262EF5")]
public class QuestMonsterKillCountPlugIn : IAttackableGotKilledPlugIn, ISupportCustomConfiguration<QuestMonsterKillCountPlugInConfiguration>, ISupportDefaultCustomConfiguration
{
    /// <inheritdoc/>
    public QuestMonsterKillCountPlugInConfiguration? Configuration { get; set; }

    /// <summary>
    /// Is called when an <see cref="IAttackable" /> object got killed by another.
    /// </summary>
    /// <param name="killed">The killed <see cref="IAttackable" />.</param>
    /// <param name="killer">The killer.</param>
    public async ValueTask AttackableGotKilledAsync(IAttackable killed, IAttacker? killer)
    {
        var configuration = this.Configuration ??= CreateDefaultConfiguration();

        if (!(killer is Player player && killed is Monster monster)
            || player.SelectedCharacter?.QuestStates is not { } questStates)
        {
            return;
        }

        var killedMonsterNumber = monster.Definition.Number;
        player.Logger.LogDebug("[QuestKillCount] AttackableGotKilled: monster #{Monster} ({Name}) killed by player {Player}",
            killedMonsterNumber, monster.Definition.Designation, player.SelectedCharacter?.Name);

        foreach (var questState in questStates)
        {
            if (questState.ActiveQuest is null)
            {
                continue;
            }

            player.Logger.LogDebug("[QuestKillCount] ActiveQuest: G{Group}#{Number} '{Name}' with {Count} kill requirements",
                questState.Group, questState.ActiveQuest.Number, questState.ActiveQuest.Name,
                questState.ActiveQuest.RequiredMonsterKills?.Count ?? 0);

            // Changed from reference equality (object.Equals) to Number comparison
            // to work correctly with InMemory persistence where MonsterDefinition
            // instances may not be the same reference.
            var killRequirements = questState.ActiveQuest.RequiredMonsterKills;
            if (killRequirements is null)
            {
                continue;
            }

            foreach (var killRequirement in killRequirements.Where(r => r.Monster?.Number == killedMonsterNumber))
            {
                player.Logger.LogDebug("[QuestKillCount] Matched kill requirement: monster #{ReqMonster} (killed #{KilledMonster})",
                    killRequirement.Monster?.Number, killedMonsterNumber);

                if (questState.RequirementStates.FirstOrDefault(s => object.Equals(s.Requirement, killRequirement))
                    is not { } requirementState)
                {
                    requirementState = player.PersistenceContext.CreateNew<QuestMonsterKillRequirementState>();
                    requirementState.Requirement = killRequirement;
                    questState.RequirementStates.Add(requirementState);
                }

                requirementState!.KillCount++;
                player.Logger.LogInformation("[QuestKillCount] KillCount incremented to {Count}/{Required} for monster #{Monster}",
                    requirementState.KillCount, killRequirement.MinimumNumber, killedMonsterNumber);

                if (killRequirement.MinimumNumber >= requirementState!.KillCount
                    && configuration.Message.GetTranslation(player.Culture) is { Length: > 0 } translation)
                {
                    var message = string.Format(
                        translation,
                        questState.ActiveQuest.Name.GetTranslation(player.Culture),
                        monster.Definition.Designation.GetTranslation(player.Culture),
                        requirementState.KillCount,
                        killRequirement.MinimumNumber);

                    await player.ShowBlueMessageAsync(message).ConfigureAwait(false);
                }
            }
        }
    }

    /// <inheritdoc/>
    public object CreateDefaultConfig()
    {
        return CreateDefaultConfiguration();
    }

    private static QuestMonsterKillCountPlugInConfiguration CreateDefaultConfiguration()
    {
        return new QuestMonsterKillCountPlugInConfiguration();
    }
}