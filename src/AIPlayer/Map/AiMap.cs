// <copyright file="AiMap.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Map;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.Pathfinding;

public sealed class AiMap
{
    public const int MapSize = 256;
    public bool[,] WalkGrid { get; } = new bool[MapSize, MapSize];
    public bool[,] SafezoneGrid { get; } = new bool[MapSize, MapSize];
    public float[,] PheromoneGrid { get; } = new float[MapSize, MapSize];
    public int[,] TerritoryGrid { get; } = new int[MapSize, MapSize];
    public Dictionary<string, List<Point>> Waypoints { get; } = new();
    public Dictionary<string, List<Point>> AssemblyPoints { get; } = new();
    public void EvaporatePheromones(float factor) { }
    public ShadowMapLayer GetOrCreateShadowLayer(Guid ownerId, ILogger? logger) => new();
    public RouteTemplate? GetRoute(string routeId) => null;
    public RouteTemplate? PickRandomRoute(int teamId) => null;

    /// <summary>
    /// Finds a random walkable position within the safezone of this map.
    /// Used for AI spawn positioning — never hardcode map coordinates.
    /// </summary>
    /// <param name="maxAttempts">Maximum random attempts before linear fallback.</param>
    /// <returns>A walkable safezone position, or null if none found.</returns>
    public Point? FindWalkableDestination(int maxAttempts = 50)
    {
        // Fast path: random sampling
        for (int i = 0; i < maxAttempts; i++)
        {
            var x = Random.Shared.Next(MapSize);
            var y = Random.Shared.Next(MapSize);
            if (WalkGrid[x, y] && SafezoneGrid[x, y])
            {
                return new Point((byte)x, (byte)y);
            }
        }

        // Fallback: linear scan from center outward
        for (int radius = 0; radius < MapSize; radius++)
        {
            int cx = MapSize / 2, cy = MapSize / 2;
            for (int x = Math.Max(0, cx - radius); x <= Math.Min(MapSize - 1, cx + radius); x++)
            for (int y = Math.Max(0, cy - radius); y <= Math.Min(MapSize - 1, cy + radius); y++)
            {
                if (WalkGrid[x, y] && SafezoneGrid[x, y])
                    return new Point((byte)x, (byte)y);
            }
        }

        return null;
    }

    /// <summary>Finds a random blocked (non-walkable) tile. Useful for testing obstacle avoidance.</summary>
    public Point? FindBlockedTile(int maxAttempts = 50)
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            var x = Random.Shared.Next(MapSize);
            var y = Random.Shared.Next(MapSize);
            if (!WalkGrid[x, y])
                return new Point((byte)x, (byte)y);
        }
        return null;
    }
}
