// <copyright file="RandomWalkAlgorithm.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Pathfinding.Algorithms;

using System.Threading;

/// <summary>
/// Random walk pathfinding algorithm. Each step picks a random walkable neighbor,
/// with configurable bias toward the target direction.
///
/// Bias = 0:   fully random walk (Brownian motion)
/// Bias = 0.5: 50% chance to move toward target, 50% random
/// Bias = 1.0: always moves toward target (greedy descent)
///
/// Useful for patrol, search, and exploration behaviors where
/// optimal paths are not required.
/// </summary>
public sealed class RandomWalkAlgorithm : PathFindingAlgorithmBase
{
    private readonly Random _random = new();

    /// <summary>
    /// Gets or sets the bias toward the target direction. Range [0.0, 1.0].
    /// 0 = fully random, 1 = always toward target.
    /// </summary>
    public double BiasTowardTarget { get; set; } = 0.3;

    /// <summary>
    /// Gets or sets the maximum number of steps before giving up (default 500).
    /// </summary>
    public int MaxSteps { get; set; } = 500;

    /// <inheritdoc />
    public override string Name => "RandomWalk";

    /// <inheritdoc />
    public override string Description => "Random walk with configurable target bias; for patrol and exploration.";

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

        var path = new List<PathResultNode>();
        var current = start;
        var consecutiveFailures = 0;

        while (current != end && path.Count < this.MaxPathLength && path.Count < this.MaxSteps && !cancellationToken.IsCancellationRequested)
        {
            var neighbors = GetWalkableNeighbors(current, terrain, includeSafezone).ToList();
            if (neighbors.Count == 0)
            {
                return null;
            }

            // Pick next step: bias toward target or random
            Point next;
            if (this._random.NextDouble() < this.BiasTowardTarget)
            {
                // Pick neighbor closest to target
                var bestDist = double.MaxValue;
                var bestNeighbor = current;
                foreach (var n in neighbors)
                {
                    var d = n.EuclideanDistanceTo(end);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestNeighbor = n;
                    }
                }

                next = bestNeighbor;
            }
            else
            {
                // Pick random neighbor
                next = neighbors[this._random.Next(neighbors.Count)];
            }

            if (next == current)
            {
                consecutiveFailures++;
                if (consecutiveFailures > 10)
                {
                    return null;
                }

                continue;
            }

            consecutiveFailures = 0;
            path.Add(new PathResultNode(next, current));
            current = next;
        }

        return path.Count > 0 ? path : null;
    }
}
