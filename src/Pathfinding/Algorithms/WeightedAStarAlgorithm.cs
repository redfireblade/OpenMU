// <copyright file="WeightedAStarAlgorithm.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Pathfinding.Algorithms;

using System.Threading;

/// <summary>
/// Weighted A* (A*++) with dynamic weighting and improved tie-breaking.
/// Uses f = g + w * h where w increases as the search progresses,
/// biasing toward depth-first search when far from the target.
/// Tie-breaking prefers nodes closer to the goal.
/// </summary>
public sealed class WeightedAStarAlgorithm : PathFindingAlgorithmBase
{
    private const int DefaultSearchLimit = 1000;

    /// <summary>
    /// Gets or sets the initial weight multiplier (default 1.0).
    /// </summary>
    public double InitialWeight { get; set; } = 1.0;

    /// <summary>
    /// Gets or sets the final weight multiplier (default 3.0).
    /// </summary>
    public double FinalWeight { get; set; } = 3.0;

    /// <inheritdoc />
    public override string Name => "A*++";

    /// <inheritdoc />
    public override string Description => "Weighted A* with dynamic weighting (w: 1.0→3.0) and improved tie-breaking.";

    /// <inheritdoc />
    public override IList<PathResultNode>? FindPath(Point start, Point end, byte[,] terrain, bool includeSafezone, CancellationToken cancellationToken = default)
    {
        var width = terrain.GetLength(0);
        var height = terrain.GetLength(1);

        // Quick bounds check
        if (!IsWalkable(terrain, start.X, start.Y, includeSafezone) ||
            !IsWalkable(terrain, end.X, end.Y, includeSafezone))
        {
            return null;
        }

        // Early exit: start == end
        if (start == end)
        {
            return new List<PathResultNode>();
        }

        var openList = new List<WeightedNode>(128);
        var closedSet = new HashSet<Point>();
        var nodeCache = new Dictionary<Point, WeightedNode>();

        var startNode = new WeightedNode(start, null, 0, HeuristicCost(start, end));
        openList.Add(startNode);
        nodeCache[start] = startNode;

        var nodesExpanded = 0;
        var pathFound = false;
        WeightedNode? endNode = null;

        while (openList.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            // Find node with lowest f
            var current = openList[0];
            var currentIdx = 0;
            for (var i = 1; i < openList.Count; i++)
            {
                // Dynamic weighting: weight increases with search progress for depth bias
                var progress = (double)nodesExpanded / DefaultSearchLimit;
                var w = this.InitialWeight + ((this.FinalWeight - this.InitialWeight) * Math.Min(progress, 1.0));

                var fi = openList[i].G + (w * openList[i].H);
                var fc = current.G + (w * current.H);

                // Tie-breaking: prefer node with smaller h (closer to goal)
                if (fi < fc || (Math.Abs(fi - fc) < 0.001 && openList[i].H < current.H))
                {
                    current = openList[i];
                    currentIdx = i;
                }
            }

            openList.RemoveAt(currentIdx);

            if (current.Position == end)
            {
                endNode = current;
                pathFound = true;
                break;
            }

            if (!closedSet.Add(current.Position))
            {
                continue;
            }

            if (nodesExpanded > DefaultSearchLimit)
            {
                return null;
            }

            nodesExpanded++;

            // Expand neighbors
            foreach (var neighbor in GetWalkableNeighbors(current.Position, terrain, includeSafezone))
            {
                if (closedSet.Contains(neighbor))
                {
                    continue;
                }

                var g = current.G + CostBetween(current.Position, neighbor);
                var h = HeuristicCost(neighbor, end);

                if (nodeCache.TryGetValue(neighbor, out var existingNode))
                {
                    if (g < existingNode.G)
                    {
                        existingNode.G = g;
                        existingNode.Parent = current;
                    }
                }
                else
                {
                    var newNode = new WeightedNode(neighbor, current, g, h);
                    openList.Add(newNode);
                    nodeCache[neighbor] = newNode;
                }
            }
        }

        if (!pathFound || endNode is null)
        {
            return null;
        }

        // Reconstruct path
        var path = new List<PathResultNode>();
        var node = endNode;
        while (node.Parent is not null)
        {
            path.Add(new PathResultNode(node.Position, node.Parent.Position));
            node = node.Parent;
        }

        path.Reverse();

        if (path.Count > this.MaxPathLength)
        {
            path = path.Take(this.MaxPathLength).ToList();
        }

        return path;
    }

    private static double HeuristicCost(Point a, Point b)
    {
        var dx = Math.Abs(a.X - b.X);
        var dy = Math.Abs(a.Y - b.Y);
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static double CostBetween(Point a, Point b)
    {
        return a.X == b.X || a.Y == b.Y ? 1.0 : 1.414; // 1 for cardinal, sqrt(2) for diagonal
    }

    private sealed class WeightedNode
    {
        public WeightedNode(Point position, WeightedNode? parent, double g, double h)
        {
            this.Position = position;
            this.Parent = parent;
            this.G = g;
            this.H = h;
        }

        public Point Position { get; }

        public WeightedNode? Parent { get; set; }

        public double G { get; set; }

        public double H { get; }
    }
}
