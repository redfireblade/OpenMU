// <copyright file="ObservedDropData.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

/// <summary>
/// Stores observed item drop data for a specific monster-item pair,
/// learned from runtime gameplay and persisted across AI player sessions.
/// </summary>
public sealed class ObservedDropData
{
    /// <summary>
    /// Gets or sets the monster number.
    /// </summary>
    public int MonsterNumber { get; set; }

    /// <summary>
    /// Gets or sets the item group.
    /// </summary>
    public int ItemGroup { get; set; }

    /// <summary>
    /// Gets or sets the item number within the group.
    /// </summary>
    public int ItemNumber { get; set; }

    /// <summary>
    /// Gets or sets the number of times this item has been observed dropping from this monster.
    /// </summary>
    public long ObservedCount { get; set; }

    /// <summary>
    /// Gets or sets the total number of kills of this monster that have been observed.
    /// </summary>
    public long TotalKills { get; set; }
}
