// <copyright file="GeneticPathAlgorithm.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Pathfinding.Algorithms;

using System.Threading;

/// <summary>
/// Genetic Algorithm for pathfinding.
///
/// Maintains a population of path candidates (chromosomes). Each chromosome
/// is a sequence of waypoints from start to target. Evolution occurs through
/// selection, crossover (splicing two paths at shared waypoints), and mutation
/// (random detours). Fitness favors shorter, valid paths.
///
/// Note: This is a simplified real-time variant with small population and
/// limited generations to keep execution practical.
/// </summary>
public sealed class GeneticPathAlgorithm : PathFindingAlgorithmBase
{
    private readonly Random _random = new();

    /// <summary>
    /// Gets or sets the population size (default 30).
    /// </summary>
    public int PopulationSize { get; set; } = 30;

    /// <summary>
    /// Gets or sets the maximum number of generations (default 30).
    /// </summary>
    public int MaxGenerations { get; set; } = 30;

    /// <summary>
    /// Gets or sets the mutation rate (default 0.2).
    /// </summary>
    public double MutationRate { get; set; } = 0.2;

    /// <inheritdoc />
    public override string Name => "Genetic";

    /// <inheritdoc />
    public override string Description => "Genetic algorithm; evolves path populations through crossover and mutation.";

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

        // Initialize population with random walk paths
        var population = new List<List<Point>>();
        for (var i = 0; i < this.PopulationSize; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var path = this.GenerateRandomPath(start, end, terrain, includeSafezone);
            if (path is not null)
            {
                population.Add(path);
            }
        }

        if (population.Count == 0)
        {
            return null;
        }

        List<Point>? bestChromosome = null;
        var bestFitness = double.MinValue;

        for (var generation = 0; generation < this.MaxGenerations && population.Count > 0 && !cancellationToken.IsCancellationRequested; generation++)
        {
            // Evaluate fitness
            var scored = population
                .Select(p => (Path: p, Fitness: CalculateFitness(p, end)))
                .OrderByDescending(x => x.Fitness)
                .ToList();

            if (scored[0].Fitness > bestFitness)
            {
                bestFitness = scored[0].Fitness;
                bestChromosome = scored[0].Path;
            }

            // If a perfect path was found (fitness near max), stop early
            if (scored[0].Fitness > 0.9)
            {
                break;
            }

            // Selection: keep top 20%
            var keepCount = Math.Max(1, population.Count / 5);
            var nextGeneration = scored.Take(keepCount).Select(x => x.Path).ToList();

            // Crossover and mutate the rest
            while (nextGeneration.Count < this.PopulationSize)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var parent1 = scored[this._random.Next(keepCount)].Path;
                var parent2 = scored[this._random.Next(keepCount)].Path;

                var child = this.Crossover(parent1, parent2, terrain, includeSafezone);
                if (child is null)
                {
                    continue;
                }

                if (this._random.NextDouble() < this.MutationRate)
                {
                    child = this.Mutate(child, terrain, includeSafezone);
                }

                if (child is not null && child.Count >= 2)
                {
                    nextGeneration.Add(child);
                }
            }

            population = nextGeneration;
        }

        if (bestChromosome is null || bestChromosome.Count < 2)
        {
            return null;
        }

        // Ensure the path ends at the target
        if (bestChromosome[^1] != end)
        {
            bestChromosome.Add(end);
        }

        var result = new List<PathResultNode>();
        for (var i = 1; i < bestChromosome.Count; i++)
        {
            result.Add(new PathResultNode(bestChromosome[i], bestChromosome[i - 1]));
        }

        return result.Count > 0 ? result : null;
    }

    private static double CalculateFitness(List<Point> path, Point end)
    {
        if (path.Count < 2)
        {
            return 0;
        }

        // Check if path reaches the target
        var reachesTarget = path[^1] == end ? 1.0 : 0.0;

        // Calculate path length (shorter = better)
        var length = 0.0;
        for (var i = 1; i < path.Count; i++)
        {
            var dx = Math.Abs(path[i].X - path[i - 1].X);
            var dy = Math.Abs(path[i].Y - path[i - 1].Y);
            length += dx == 0 || dy == 0 ? 1.0 : 1.414;
        }

        // Fitness: reward reaching target, penalize length
        // Normalize: length of 100 gives ~0.5 fitness contribution
        var lengthScore = 1.0 / (1.0 + (length * 0.01));
        return (reachesTarget * 0.7) + (lengthScore * 0.3);
    }

    private static int FindCommonPoint(List<Point> path1, List<Point> path2)
    {
        var set = new HashSet<Point>(path1);
        for (var i = 1; i < path2.Count - 1; i++)
        {
            if (set.Contains(path2[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private List<Point>? Crossover(List<Point> parent1, List<Point> parent2, byte[,] terrain, bool includeSafezone)
    {
        var commonIdx = FindCommonPoint(parent1, parent2);
        if (commonIdx < 0)
        {
            // No common point — just return the shorter parent
            return parent1.Count <= parent2.Count ? new List<Point>(parent1) : new List<Point>(parent2);
        }

        var commonPoint = parent2[commonIdx];

        // Find where common point appears in parent1
        var idx1 = parent1.IndexOf(commonPoint);
        if (idx1 < 0)
        {
            return new List<Point>(parent1);
        }

        // Combine: parent1[0..idx1] + parent2[commonIdx..]
        var child = new List<Point>();
        for (var i = 0; i <= idx1; i++)
        {
            child.Add(parent1[i]);
        }

        for (var i = commonIdx + 1; i < parent2.Count; i++)
        {
            child.Add(parent2[i]);
        }

        return child;
    }

    private List<Point>? Mutate(List<Point> path, byte[,] terrain, bool includeSafezone)
    {
        if (path.Count < 3)
        {
            return path;
        }

        var mutated = new List<Point>(path);
        var mutationPoint = this._random.Next(1, mutated.Count - 1);

        // Try to add a random detour of 1-3 steps from the mutation point
        var current = mutated[mutationPoint];
        var originalNext = mutated[mutationPoint + 1];
        var detour = new List<Point>();
        var pos = current;
        var detourLength = this._random.Next(1, 4);

        for (var i = 0; i < detourLength; i++)
        {
            var neighbors = GetWalkableNeighbors(pos, terrain, includeSafezone)
                .Where(n => !detour.Contains(n) && n != current)
                .ToList();

            if (neighbors.Count == 0)
            {
                break;
            }

            // Prefer neighbors that are closer to originalNext
            neighbors = neighbors.OrderBy(n => n.EuclideanDistanceTo(originalNext)).ToList();
            var chosen = this._random.Next(0, Math.Min(3, neighbors.Count));
            pos = neighbors[chosen];
            detour.Add(pos);
        }

        // Insert detour
        mutated.InsertRange(mutationPoint + 1, detour);
        return mutated;
    }

    private List<Point>? GenerateRandomPath(Point start, Point end, byte[,] terrain, bool includeSafezone)
    {
        var path = new List<Point> { start };
        var current = start;
        var visited = new HashSet<Point> { start };
        var steps = 0;

        while (current != end && steps < 300 && path.Count < this.MaxPathLength)
        {
            var neighbors = GetWalkableNeighbors(current, terrain, includeSafezone)
                .Where(n => !visited.Contains(n))
                .ToList();

            if (neighbors.Count == 0)
            {
                // Can't reach target by random walk — fail
                return null;
            }

            // 30% chance to move toward target, 70% random
            Point next;
            if (this._random.NextDouble() < 0.3)
            {
                next = neighbors.OrderBy(n => n.EuclideanDistanceTo(end)).First();
            }
            else
            {
                next = neighbors[this._random.Next(neighbors.Count)];
            }

            path.Add(next);
            visited.Add(next);
            current = next;
            steps++;
        }

        if (current != end)
        {
            // Try to append the target at the end
            if (IsWalkable(terrain, end.X, end.Y, includeSafezone))
            {
                path.Add(end);
            }
            else
            {
                return null;
            }
        }

        return path;
    }
}
