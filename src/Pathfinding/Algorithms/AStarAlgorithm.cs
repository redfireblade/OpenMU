// <copyright file="AStarAlgorithm.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Pathfinding.Algorithms;

using System.Threading;

/// <summary>
/// Standard A* pathfinding algorithm wrapping the existing <see cref="PathFinder"/>.
/// Uses Euclidean heuristic with 8-directional movement on a 2D grid.
/// </summary>
public sealed class AStarAlgorithm : PathFindingAlgorithmBase
{
    private readonly ThreadLocal<PathFinder> _pathFinder = new(() => new PathFinder(new ScopedGridNetwork(maximumSegmentSideLength: 64))
    {
        Heuristic = new EuclideanHeuristic(),
        HeuristicEstimate = 2,
        SearchLimit = 1000,
    });

    /// <inheritdoc />
    public override string Name => "AStar";

    /// <inheritdoc />
    public override string Description => "Standard A* with Euclidean heuristic, 8-directional grid search.";

    /// <inheritdoc />
    public override IList<PathResultNode>? FindPath(Point start, Point end, byte[,] terrain, bool includeSafezone, CancellationToken cancellationToken = default)
    {
        if (!IsWalkable(terrain, start.X, start.Y, includeSafezone) ||
            !IsWalkable(terrain, end.X, end.Y, includeSafezone))
        {
            return null;
        }

        if (start == end)
        {
            return new List<PathResultNode>();
        }

        var pf = this._pathFinder.Value!;
        pf.ResetPathFinder();
        return pf.FindPath(start, end, terrain, includeSafezone, cancellationToken);
    }
}
