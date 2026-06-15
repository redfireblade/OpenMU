// <copyright file="WarpPlanner.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Warp;

using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// 跨地图传送路由引擎。构建门图 + 传送菜单图，BFS 计算最佳多跳路线。
/// </summary>
public sealed class WarpPlanner
{
    private readonly GameConfiguration _config;
    private readonly ILogger _logger;
    private readonly Dictionary<short, List<WarpEdge>> _adjacencyList = new();

    /// <summary>最大路由跳数硬限制，防止循环</summary>
    private const int MaxHops = 20;

    public WarpPlanner(GameConfiguration config, ILogger logger)
    {
        this._config = config;
        this._logger = logger;
        this.BuildGraph();
    }

    /// <summary>
    /// 计算从 <paramref name="fromMap"/> 到 <paramref name="toMap"/> 的最佳路线。
    /// Phase 1: 等级全满足的路线中取最低金币成本。
    /// Phase 2: 如果 Phase 1 无结果，取需要最少等级的路线（不强制可行）。
    /// </summary>
    public WarpRoute? ComputeRoute(short fromMap, short toMap, int playerLevel, int playerMoney)
    {
        if (fromMap == toMap)
        {
            return new WarpRoute
            {
                SourceMap = fromMap,
                DestinationMap = toMap,
                Steps = Array.Empty<WarpStep>(),
                IsFeasible = true,
            };
        }

        // BFS to find all routes, score by (feasible, -feasibilityBias, goldCost)
        var bestFeasible = this.FindBestRoute(fromMap, toMap, playerLevel, playerMoney, requireFeasible: true);
        if (bestFeasible is not null)
        {
            return bestFeasible;
        }

        // Phase 2: no feasible route — find one with minimal blocker
        return this.FindBestRoute(fromMap, toMap, playerLevel, playerMoney, requireFeasible: false);
    }

    /// <summary>
    /// 获取当前地图的出边（用于探查）。
    /// </summary>
    public IReadOnlyList<WarpEdge> GetOutgoingEdges(short mapNumber)
    {
        return this._adjacencyList.TryGetValue(mapNumber, out var edges)
            ? edges
            : System.Array.Empty<WarpEdge>();
    }

    private void BuildGraph()
    {
        // Build gate edges: for each map's EnterGates
        foreach (var mapDef in this._config.Maps)
        {
            if (mapDef.EnterGates is null) continue;

            foreach (var enterGate in mapDef.EnterGates)
            {
                if (enterGate.TargetGate?.Map is null) continue;

                var targetMapNum = enterGate.TargetGate.Map.Number;
                var centerX = (byte)((enterGate.X1 + enterGate.X2) / 2);
                var centerY = (byte)((enterGate.Y1 + enterGate.Y2) / 2);

                if (!this._adjacencyList.TryGetValue(mapDef.Number, out var list))
                {
                    list = new List<WarpEdge>();
                    this._adjacencyList[mapDef.Number] = list;
                }

                list.Add(new WarpEdge
                {
                    FromMapNumber = mapDef.Number,
                    ToMapNumber = targetMapNum,
                    EnterGate = enterGate,
                    EdgeType = WarpEdgeType.Gate,
                    LevelRequirement = enterGate.LevelRequirement,
                    GoldCost = 0,
                    GateCenter = new Point(centerX, centerY),
                });
            }
        }

        // Build warp menu edges: from ANY map (user presses N from anywhere)
        if (this._config.WarpList is not null)
        {
            foreach (var warpInfo in this._config.WarpList)
            {
                if (warpInfo.Gate?.Map is null) continue;

                var targetMapNum = warpInfo.Gate.Map.Number;
                var warpEdge = new WarpEdge
                {
                    FromMapNumber = 0, // sentinel: from any map
                    ToMapNumber = targetMapNum,
                    WarpInfo = warpInfo,
                    EdgeType = WarpEdgeType.WarpMenu,
                    LevelRequirement = warpInfo.LevelRequirement,
                    GoldCost = warpInfo.Costs,
                    GateCenter = default,
                };

                // Add this edge to EVERY map (or at least ones that make sense)
                foreach (var mapDef in this._config.Maps)
                {
                    if (!this._adjacencyList.TryGetValue(mapDef.Number, out var list))
                    {
                        list = new List<WarpEdge>();
                        this._adjacencyList[mapDef.Number] = list;
                    }

                    list.Add(new WarpEdge
                    {
                        FromMapNumber = mapDef.Number,
                        ToMapNumber = targetMapNum,
                        WarpInfo = warpInfo,
                        EdgeType = WarpEdgeType.WarpMenu,
                        LevelRequirement = warpInfo.LevelRequirement,
                        GoldCost = warpInfo.Costs,
                        GateCenter = default,
                    });
                }

                // Also add from sentinel 0 so planners can find it
                if (!this._adjacencyList.ContainsKey(0))
                    this._adjacencyList[0] = new List<WarpEdge>();
            }
        }

        this._logger.LogInformation("[WarpPlanner] Graph built: {MapCount} maps, {WarpCount} warp entries",
            this._adjacencyList.Count,
            this._config.WarpList?.Count ?? 0);
    }

    private WarpRoute? FindBestRoute(short fromMap, short toMap, int playerLevel, int playerMoney, bool requireFeasible)
    {
        // BFS with priority: (goldCost, hops) as score
        var visited = new HashSet<(short Map, int Score)>();
        var queue = new System.Collections.Generic.List<(short Map, List<WarpStep> Steps, int TotalGold, int MaxLevel)>();

        queue.Add((fromMap, new System.Collections.Generic.List<WarpStep>(), 0, 0));
        visited.Add((fromMap, 0));

        WarpRoute? bestRoute = null;
        var bestScore = int.MaxValue;

        while (queue.Count > 0)
        {
            // Sort by score so lowest-scored expands first
            queue.Sort((a, b) => a.TotalGold.CompareTo(b.TotalGold));
            var (currentMap, steps, totalGold, maxLevel) = queue[0];
            queue.RemoveAt(0);

            if (currentMap == toMap)
            {
                var score = totalGold;
                if (score < bestScore)
                {
                    bestScore = score;
                    var feasible = true;
                    foreach (var s in steps)
                    {
                        if (s.LevelRequirement > playerLevel)
                        {
                            feasible = false;
                            break;
                        }
                        if (s.GoldCost > 0 && totalGold > playerMoney)
                        {
                            feasible = false;
                            break;
                        }
                    }

                    if (!requireFeasible || feasible)
                    {
                        bestRoute = new WarpRoute
                        {
                            SourceMap = fromMap,
                            DestinationMap = toMap,
                            Steps = steps.ToArray(),
                            IsFeasible = feasible,
                            TotalGoldCost = totalGold,
                            HighestLevelRequirement = maxLevel,
                        };
                    }
                }
                continue;
            }

            if (steps.Count >= MaxHops) continue;

            if (!this._adjacencyList.TryGetValue(currentMap, out var edges)) continue;

            foreach (var edge in edges)
            {
                if (edge.ToMapNumber == currentMap) continue; // skip self-loop

                if (requireFeasible)
                {
                    if (edge.LevelRequirement > playerLevel) continue;
                    if (edge.GoldCost > 0 && totalGold + edge.GoldCost > playerMoney) continue;
                }

                var stateKey = (edge.ToMapNumber, totalGold + edge.GoldCost);
                if (visited.Contains(stateKey)) continue;
                visited.Add(stateKey);

                var newSteps = new System.Collections.Generic.List<WarpStep>(steps)
                {
                    new WarpStep
                    {
                        FromMap = currentMap,
                        ToMap = edge.ToMapNumber,
                        Method = edge.EdgeType,
                        EnterGate = edge.EnterGate,
                        WarpInfo = edge.WarpInfo,
                        LevelRequirement = edge.LevelRequirement,
                        GoldCost = edge.GoldCost,
                        GateCenter = edge.GateCenter,
                    },
                };

                queue.Add((
                    edge.ToMapNumber,
                    newSteps,
                    totalGold + edge.GoldCost,
                    System.Math.Max(maxLevel, edge.LevelRequirement)
                ));
            }
        }

        return bestRoute;
    }
}
