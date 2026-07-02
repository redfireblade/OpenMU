// <copyright file="PathFindingAlgorithmBase.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Pathfinding;

using System.Threading;

/// <summary>
/// Abstract base class for pathfinding algorithms providing shared utilities.
/// </summary>
public abstract class PathFindingAlgorithmBase : IPathFindingAlgorithm
{
    /// <summary>
    /// The cost bit mask (bits 0-6 of the terrain value).
    /// </summary>
    protected const byte CostBitMask = 0b0111_1111;

    /// <summary>
    /// The safezone bit flag (bit 7 of the terrain value).
    /// </summary>
    protected const byte SafezoneBitFlag = 0b1000_0000;

    /// <summary>
    /// The value indicating an unreachable grid cell.
    /// </summary>
    protected const byte UnreachableGridNodeValue = 0;

    /// <summary>
    /// The 8-directional neighbor offsets.
    /// </summary>
    protected static readonly (sbyte X, sbyte Y)[] DirectionOffsets =
    [
        (0, -1),  // North
        (1, -1),  // North-East
        (1, 0),   // East
        (1, 1),   // South-East
        (0, 1),   // South
        (-1, 1),  // South-West
        (-1, 0),  // West
        (-1, -1), // North-West
    ];

    /// <summary>
    /// Gets or sets the maximum number of steps in the returned path.
    /// </summary>
    public int MaxPathLength { get; set; } = 128;

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <inheritdoc />
    public abstract IList<PathResultNode>? FindPath(Point start, Point end, byte[,] terrain, bool includeSafezone, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a grid cell is walkable.
    /// </summary>
    /// <param name="terrain">The terrain grid.</param>
    /// <param name="x">X coordinate.</param>
    /// <param name="y">Y coordinate.</param>
    /// <param name="includeSafezone">If true, safezone cells are also considered walkable.</param>
    protected static bool IsWalkable(byte[,] terrain, int x, int y, bool includeSafezone)
    {
        if (x < 0 || x >= terrain.GetLength(0) || y < 0 || y >= terrain.GetLength(1))
        {
            return false;
        }

        var cell = terrain[x, y];
        var cost = cell & CostBitMask;
        if (cost == UnreachableGridNodeValue)
        {
            return false;
        }

        if (!includeSafezone && (cell & SafezoneBitFlag) != 0)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Gets walkable neighbor positions for a given point.
    /// </summary>
    /// <param name="position">The current position.</param>
    /// <param name="terrain">The terrain grid.</param>
    /// <param name="includeSafezone">If true, safezone cells are also considered walkable.</param>
    protected static IEnumerable<Point> GetWalkableNeighbors(Point position, byte[,] terrain, bool includeSafezone)
    {
        foreach (var (dx, dy) in DirectionOffsets)
        {
            var nx = position.X + dx;
            var ny = position.Y + dy;
            if (IsWalkable(terrain, nx, ny, includeSafezone))
            {
                yield return new Point((byte)nx, (byte)ny);
            }
        }
    }
}
