// <copyright file="DiffusionPathAlgorithm.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Pathfinding.Algorithms;

using System.Threading;

/// <summary>
/// Diffusion-based pathfinding using BFS distance field.
/// Starting from the target, a BFS flood-fill computes the distance
/// to every reachable cell. The path is then traced from start to
/// target by always moving to the neighbor with the lowest distance value.
///
/// Advantage: once the distance field is computed, paths from ANY start
/// position to the same target can be resolved instantly.
/// Useful for group pathfinding (multiple units pathing to same destination).
/// </summary>
public sealed class DiffusionPathAlgorithm : PathFindingAlgorithmBase
{
    /// <summary>
    /// Gets or sets the maximum search distance from the target (default 256).
    /// Limits how far the BFS spreads to control execution time.
    /// </summary>
    public int MaxSearchDistance { get; set; } = 256;

    /// <inheritdoc />
    public override string Name => "Diffusion";

    /// <inheritdoc />
    public override string Description => "BFS distance-field diffusion; optimal for multi-unit pathfinding to the same target.";

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

        var width = terrain.GetLength(0);
        var height = terrain.GetLength(1);

        // Phase 1: BFS from target to fill distance field
        var distance = new int[width, height];
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                distance[x, y] = int.MaxValue;
            }
        }

        var queue = new Queue<Point>();
        distance[end.X, end.Y] = 0;
        queue.Enqueue(end);

        while (queue.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            var current = queue.Dequeue();
            var nextDist = distance[current.X, current.Y] + 1;

            if (nextDist > this.MaxSearchDistance)
            {
                continue;
            }

            foreach (var neighbor in GetWalkableNeighbors(current, terrain, includeSafezone))
            {
                if (distance[neighbor.X, neighbor.Y] > nextDist)
                {
                    distance[neighbor.X, neighbor.Y] = nextDist;
                    queue.Enqueue(neighbor);
                }
            }
        }

        // If start was never reached, no path exists
        if (distance[start.X, start.Y] == int.MaxValue)
        {
            return null;
        }

        // Phase 2: Trace path from start to end by following the gradient
        var path = new List<PathResultNode>();
        var currentPos = start;

        while (currentPos != end && path.Count < this.MaxPathLength && !cancellationToken.IsCancellationRequested)
        {
            var bestNeighbor = currentPos;
            var bestDist = distance[currentPos.X, currentPos.Y];

            foreach (var neighbor in GetWalkableNeighbors(currentPos, terrain, includeSafezone))
            {
                if (distance[neighbor.X, neighbor.Y] < bestDist)
                {
                    bestDist = distance[neighbor.X, neighbor.Y];
                    bestNeighbor = neighbor;
                }
            }

            if (bestNeighbor == currentPos)
            {
                // Stuck — no better neighbor
                return null;
            }

            path.Add(new PathResultNode(bestNeighbor, currentPos));
            currentPos = bestNeighbor;
        }

        return path;
    }
}
