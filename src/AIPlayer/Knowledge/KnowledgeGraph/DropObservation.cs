// <copyright file="DropObservation.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge.KnowledgeGraph;

/// <summary>
/// Tracks an observed drop relationship between a monster and an item.
/// Thread-safe for concurrent reads and writes via the <see cref="KnowledgeGraphRuntimeLearner"/>.
/// </summary>
public sealed class DropObservation
{
    private static readonly double DefaultEmpiricalRate = 0.0;

    /// <summary>
    /// Gets or sets the number of times this item has been observed dropping from the monster.
    /// </summary>
    public long ObservedCount { get; set; }

    /// <summary>
    /// Gets or sets the total number of kills observed for the monster.
    /// </summary>
    public long TotalKills { get; set; }

    /// <summary>
    /// Gets the empirical drop rate computed as <see cref="ObservedCount"/> / <see cref="TotalKills"/>.
    /// Returns 0 if no kills have been recorded.
    /// </summary>
    public double EmpiricalRate => this.TotalKills > 0 ? (double)this.ObservedCount / this.TotalKills : DefaultEmpiricalRate;
}
