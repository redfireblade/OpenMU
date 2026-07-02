// <copyright file="IPathFindingAlgorithm.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Pathfinding;

using System.Threading;

/// <summary>
/// Interface for a pathfinding algorithm that can be used by AI entities.
/// </summary>
public interface IPathFindingAlgorithm
{
    /// <summary>
    /// Gets the name of this algorithm.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets a brief description of how this algorithm works.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Finds a path from <paramref name="start"/> to <paramref name="end"/> on the given terrain grid.
    /// </summary>
    /// <param name="start">The starting coordinate.</param>
    /// <param name="end">The target coordinate.</param>
    /// <param name="terrain">The terrain grid. Bit 7 = safezone flag, bits 0-6 = cost (0 = unreachable).</param>
    /// <param name="includeSafezone">If set to <c>true</c>, path may cross safezone tiles.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ordered list of path steps (excluding start, including end), or <c>null</c> if no path found.</returns>
    IList<PathResultNode>? FindPath(Point start, Point end, byte[,] terrain, bool includeSafezone, CancellationToken cancellationToken = default);
}
