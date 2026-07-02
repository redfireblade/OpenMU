// <copyright file="QuestInfo.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge;

/// <summary>
/// Information about a quest definition, pre-loaded from <see cref="DataModel.Configuration.Quests.QuestDefinition"/>.
/// </summary>
public record QuestInfo(
    int QuestGroup,
    int QuestNumber,
    string Name,
    int MinLevel,
    int MaxLevel,
    bool Repeatable,
    short? StartNpcNumber,
    int StartMoney,
    bool RequiresClientAction,
    string RequiredKillsDescription,
    string RequiredItemsDescription,
    string RewardsDescription);
