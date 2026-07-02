// <copyright file="AntColonyAlgorithm.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Pathfinding.Algorithms;

using System.Threading;

/// <summary>
/// Simplified Ant Colony Optimization (ACO) for pathfinding.
///
/// Multiple ants traverse the grid from start to target. Each ant
/// probabilistically chooses edges based on pheromone concentration
/// and heuristic distance. Successful ants deposit pheromone along
/// their path. Over multiple iterations, paths converge toward the optimal route.
///
/// This is a simplified real-time variant — limited iterations and a
/// small ant population to keep execution time manageable.
/// </summary>
public sealed class AntColonyAlgorithm : PathFindingAlgorithmBase
{
    private const double DefaultPheromone = 1.0;
    private const double EvaporationRate = 0.1;
    private const double PheromoneDeposit = 10.0;

    private readonly Random _random = new();

    /// <summary>
    /// Gets or sets the number of ants per iteration (default 20).
    /// </summary>
    public int AntCount { get; set; } = 20;

    /// <summary>
    /// Gets or sets the maximum number of iterations (default 5).
    /// </summary>
    public int MaxIterations { get; set; } = 5;

    /// <summary>
    /// Gets or sets the pheromone influence exponent alpha (default 2.0).
    /// Higher = ants follow pheromone trails more strongly.
    /// </summary>
    public double Alpha { get; set; } = 2.0;

    /// <summary>
    /// Gets or sets the heuristic influence exponent beta (default 1.0).
    /// Higher = ants prefer shorter edges more strongly.
    /// </summary>
    public double Beta { get; set; } = 1.0;

    /// <inheritdoc />
    public override string Name => "AntColony";

    /// <inheritdoc />
    public override string Description => "Ant Colony Optimization; probabilistic pathfinding with pheromone trails.";

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

        var bestPath = new List<PathResultNode>();
        var bestLength = double.MaxValue;

        // Initialize pheromone map: pheromone[from][to]
        var width = terrain.GetLength(0);
        var height = terrain.GetLength(1);
        var pheromone = new double[width, height];
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                pheromone[x, y] = DefaultPheromone;
            }
        }

        for (var iteration = 0; iteration < this.MaxIterations && !cancellationToken.IsCancellationRequested; iteration++)
        {
            var iterationPaths = new List<(List<Point> Path, double Length)>();

            for (var ant = 0; ant < this.AntCount; ant++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var antPath = this.WalkAnt(start, end, terrain, includeSafezone, pheromone);
                if (antPath is null || antPath.Count == 0)
                {
                    continue;
                }

                var length = CalculatePathLength(antPath);
                iterationPaths.Add((antPath, length));

                if (length < bestLength)
                {
                    bestLength = length;
                    bestPath = ConvertToResult(antPath);
                }
            }

            // Evaporate
            for (var x = 0; x < width; x++)
            {
                for (var y = 0; y < height; y++)
                {
                    pheromone[x, y] *= 1.0 - EvaporationRate;
                }
            }

            // Deposit pheromone on iteration paths
            foreach (var (path, length) in iterationPaths)
            {
                var deposit = PheromoneDeposit / Math.Max(length, 1.0);
                foreach (var point in path)
                {
                    pheromone[point.X, point.Y] += deposit;
                }
            }
        }

        return bestPath.Count > 0 ? bestPath : null;
    }

    private static double CalculatePathLength(List<Point> path)
    {
        var len = 0.0;
        for (var i = 1; i < path.Count; i++)
        {
            var dx = Math.Abs(path[i].X - path[i - 1].X);
            var dy = Math.Abs(path[i].Y - path[i - 1].Y);
            len += dx == 0 || dy == 0 ? 1.0 : 1.414;
        }

        return len;
    }

    private static List<PathResultNode> ConvertToResult(List<Point> path)
    {
        var result = new List<PathResultNode>();
        for (var i = 1; i < path.Count; i++)
        {
            result.Add(new PathResultNode(path[i], path[i - 1]));
        }

        return result;
    }

    private List<Point>? WalkAnt(Point start, Point end, byte[,] terrain, bool includeSafezone, double[,] pheromone)
    {
        var path = new List<Point> { start };
        var current = start;
        var visited = new HashSet<Point> { start };
        var steps = 0;

        while (current != end && steps < 500 && path.Count < this.MaxPathLength)
        {
            var neighbors = GetWalkableNeighbors(current, terrain, includeSafezone)
                .Where(n => !visited.Contains(n) || n == end)
                .ToList();

            if (neighbors.Count == 0)
            {
                return null;
            }

            // Calculate selection probabilities
            var totalWeight = 0.0;
            var weights = new double[neighbors.Count];
            for (var i = 0; i < neighbors.Count; i++)
            {
                var n = neighbors[i];
                var tau = Math.Max(pheromone[n.X, n.Y], 0.001);
                var eta = 1.0 / Math.Max(n.EuclideanDistanceTo(end), 0.1);
                weights[i] = Math.Pow(tau, this.Alpha) * Math.Pow(eta, this.Beta);
                totalWeight += weights[i];
            }

            if (totalWeight <= 0)
            {
                return null;
            }

            // Roulette wheel selection
            var r = this._random.NextDouble() * totalWeight;
            var cumulative = 0.0;
            var chosen = neighbors[0];
            for (var i = 0; i < neighbors.Count; i++)
            {
                cumulative += weights[i];
                if (r <= cumulative)
                {
                    chosen = neighbors[i];
                    break;
                }
            }

            path.Add(chosen);
            visited.Add(chosen);
            current = chosen;
            steps++;
        }

        return current == end ? path : null;
    }
}
