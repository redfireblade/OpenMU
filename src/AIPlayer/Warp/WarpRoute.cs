// <copyright file="WarpRoute.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Warp;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Pathfinding;

/// <summary>传送方式。</summary>
public enum WarpEdgeType { Gate, WarpMenu }

/// <summary>单条边 — 通过门或菜单从一图到另一图。</summary>
public sealed class WarpEdge
{
    public short FromMapNumber { get; init; }
    public short ToMapNumber { get; init; }
    public EnterGate? EnterGate { get; init; }
    public WarpInfo? WarpInfo { get; init; }
    public int LevelRequirement { get; init; }
    public int GoldCost { get; init; }
    public WarpEdgeType EdgeType { get; init; }
    public Point GateCenter { get; init; }
}

/// <summary>路由中的一步。</summary>
public sealed class WarpStep
{
    public short FromMap { get; init; }
    public short ToMap { get; init; }
    public WarpEdgeType Method { get; init; }
    public EnterGate? EnterGate { get; init; }
    public WarpInfo? WarpInfo { get; init; }
    public int LevelRequirement { get; init; }
    public int GoldCost { get; init; }
    public Point GateCenter { get; init; }
}

/// <summary>完整多跳路线。</summary>
public sealed class WarpRoute
{
    public short SourceMap { get; init; }
    public short DestinationMap { get; init; }
    public IReadOnlyList<WarpStep> Steps { get; init; } = System.Array.Empty<WarpStep>();
    public bool IsFeasible { get; init; }
    public int TotalGoldCost { get; init; }
    public int HighestLevelRequirement { get; init; }
}

/// <summary>Warp 阻塞详情。</summary>
public sealed class WarpBlockReason
{
    public bool IsBlocked { get; init; }
    public string Reason { get; init; } = string.Empty;
    public int? LowestMissingLevel { get; init; }
    public int? GoldShortfall { get; init; }
    public short? NearestReachableMap { get; init; }
}
