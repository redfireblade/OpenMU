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
}
