// <copyright file="MiniGameEventInfo.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge;

using MUnique.OpenMU.DataModel.Configuration;

/// <summary>
/// Information about a mini game event definition, pre-loaded from <see cref="DataModel.Configuration.MiniGameDefinition"/>.
/// </summary>
public record MiniGameEventInfo(
    MiniGameType Type,
    int GameLevel,
    string Name,
    int MinLevel,
    int MaxLevel,
    int? TicketItemGroup,
    int? TicketItemNumber,
    int TicketItemLevel,
    int EntranceFee,
    int GameDurationMinutes,
    int EnterDurationMinutes,
    int MaxPlayers);
