// <copyright file="RuntimeLearnerStats.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge.KnowledgeGraph;

/// <summary>
/// Diagnostic snapshot of the <see cref="KnowledgeGraphRuntimeLearner"/> state.
/// </summary>
public sealed class RuntimeLearnerStats
{
    /// <summary>
    /// Gets or sets the total number of drop observations recorded across all monster-item pairs.
    /// </summary>
    public int TotalObservations { get; set; }

    /// <summary>
    /// Gets or sets the number of monster-item pairs being tracked.
    /// </summary>
    public int ObservedPairs { get; set; }

    /// <summary>
    /// Gets or sets the number of distinct monsters being tracked.
    /// </summary>
    public int MonstersTracked { get; set; }

    /// <summary>
    /// Gets or sets the list of recent edge adjustment descriptions for diagnostic display.
    /// Limited to the most recent <c>100</c> entries.
    /// </summary>
    public List<string> RecentAdjustments { get; set; } = new();
}
