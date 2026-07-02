// <copyright file="PathfindingIntegrationTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.IO;
using MUnique.OpenMU.AIPlayer.World;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// Integration tests that exercise all pathfinding algorithms against real map terrain
/// loaded from the .att terrain files.
/// </summary>
[TestFixture]
public class PathfindingIntegrationTests
{
    private WorldMap _worldMap = null!;
    private byte[,] _lorenciaGrid = null!;
    private byte[,] _noriaGrid = null!;
    private IReadOnlyList<IPathFindingAlgorithm> _algorithms = null!;

    /// <summary>
    /// Loads the world map from .att files and prepares all algorithms once per test run.
    /// </summary>
    [OneTimeSetUp]
    public void Setup()
    {
        var resourcesDir = WorldMapTest.FindResourcesDirectory();
        this._worldMap = WorldMapBuilder.BuildFromDirectory(resourcesDir);

        Assert.That(this._worldMap.Maps, Does.ContainKey((ushort)0));
        Assert.That(this._worldMap.Maps, Does.ContainKey((ushort)3));

        this._lorenciaGrid = TerrainToAIGridConverter.Convert(this._worldMap.Maps[0].Tiles);
        this._noriaGrid = TerrainToAIGridConverter.Convert(this._worldMap.Maps[3].Tiles);

        var selector = new AlgorithmSelector();
        selector.RegisterAlgorithms();
        this._algorithms = selector.GetAll();
        Assert.That(this._algorithms.Count, Is.GreaterThanOrEqualTo(6));
    }

    /// <summary>
    /// Every algorithm finds a valid walkable path on Lorencia from the spawn area.
    /// </summary>
    [Test]
    public void AllAlgorithmsFindPathOnLorencia()
    {
        var start = new Point(130, 125);
        var end = this.FindWalkableDestination(this._lorenciaGrid, start, 12, 8);

        foreach (var algorithm in this._algorithms)
        {
            var result = algorithm.FindPath(start, end, this._lorenciaGrid, true);
            AssertPathIsValid(result, start, end, this._lorenciaGrid, algorithm.Name);
        }
    }

    /// <summary>
    /// Every algorithm finds a valid walkable path on Noria from the spawn area.
    /// </summary>
    [Test]
    public void AllAlgorithmsFindPathOnNoria()
    {
        var start = new Point(150, 120);
        var end = this.FindWalkableDestination(this._noriaGrid, start, 10, 6);

        foreach (var algorithm in this._algorithms)
        {
            var result = algorithm.FindPath(start, end, this._noriaGrid, true);
            AssertPathIsValid(result, start, end, this._noriaGrid, algorithm.Name);
        }
    }

    /// <summary>
    /// Every algorithm returns null when the start position is an blocked tile.
    /// </summary>
    [Test]
    public void AllAlgorithmsReturnNullForUnreachableStart()
    {
        var blocked = this.FindBlockedTile(this._lorenciaGrid);
        var end = new Point(130, 125);

        foreach (var algorithm in this._algorithms)
        {
            var result = algorithm.FindPath(blocked, end, this._lorenciaGrid, false);
            Assert.That(result, Is.Null, $"{algorithm.Name} should return null when start is blocked");
        }
    }

    /// <summary>
    /// Every algorithm returns null when the end position is a blocked tile.
    /// </summary>
    [Test]
    public void AllAlgorithmsReturnNullForUnreachableEnd()
    {
        var start = new Point(130, 125);
        var blocked = this.FindBlockedTile(this._lorenciaGrid);

        foreach (var algorithm in this._algorithms)
        {
            var result = algorithm.FindPath(start, blocked, this._lorenciaGrid, false);
            Assert.That(result, Is.Null, $"{algorithm.Name} should return null when end is blocked");
        }
    }

    /// <summary>
    /// Every algorithm returns an empty list (not null) when start equals end.
    /// </summary>
    [Test]
    public void AllAlgorithmsReturnEmptyForSameStartEnd()
    {
        var point = new Point(130, 125);

        foreach (var algorithm in this._algorithms)
        {
            var result = algorithm.FindPath(point, point, this._lorenciaGrid, true);
            Assert.That(result, Is.Not.Null, $"{algorithm.Name} should not return null for same start/end");
            Assert.That(result!.Count, Is.EqualTo(0), $"{algorithm.Name} should return empty path for same start/end");
        }
    }

    /// <summary>
    /// Validates that <see cref="TerrainToAIGridConverter"/> produces the same encoding
    /// as the expected AIgrid formula.
    /// </summary>
    [Test]
    public void TerrainConversionMatchesExpectedEncoding()
    {
        var map = this._worldMap.Maps[0];
        var grid = TerrainToAIGridConverter.Convert(map.Tiles);

        for (var x = 0; x < 256; x++)
        {
            for (var y = 0; y < 256; y++)
            {
                var tile = map.Tiles[x, y];
                byte expected = tile.Walkable
                    ? (byte)(1 | (tile.SafeZone ? 0b1000_0000 : 0))
                    : (byte)0;
                Assert.That(grid[x, y], Is.EqualTo(expected), $"Mismatch at ({x}, {y})");
            }
        }
    }

    private static bool IsWalkableCell(byte cell) => (cell & 0x7F) != 0;

    private Point FindWalkableDestination(byte[,] grid, Point start, int offsetX, int offsetY)
    {
        var cx = Math.Clamp(start.X + offsetX, 0, 255);
        var cy = Math.Clamp(start.Y + offsetY, 0, 255);

        // Check the primary candidate
        if (IsWalkableCell(grid[cx, cy]))
        {
            return new Point((byte)cx, (byte)cy);
        }

        // Scan a small area around the primary candidate
        for (var dx = -4; dx <= 4; dx++)
        {
            for (var dy = -4; dy <= 4; dy++)
            {
                var sx = Math.Clamp(cx + dx, 0, 255);
                var sy = Math.Clamp(cy + dy, 0, 255);
                if (IsWalkableCell(grid[sx, sy]))
                {
                    return new Point((byte)sx, (byte)sy);
                }
            }
        }

        Assert.Fail($"Could not find a walkable destination near ({cx}, {cy}) on this map.");
        return start; // never reached
    }

    private Point FindBlockedTile(byte[,] grid)
    {
        for (var y = 0; y < 256; y++)
        {
            for (var x = 0; x < 256; x++)
            {
                if (grid[x, y] == 0)
                {
                    return new Point((byte)x, (byte)y);
                }
            }
        }

        Assert.Fail("No blocked tile found in the grid.");
        return default; // never reached
    }

    private static void AssertPathIsValid(
        IList<PathResultNode>? path,
        Point start,
        Point end,
        byte[,] terrain,
        string algorithmName,
        bool expectNull = false)
    {
        if (expectNull)
        {
            Assert.That(path, Is.Null, $"{algorithmName} should return null");
            return;
        }

        Assert.That(path, Is.Not.Null, $"{algorithmName} should find a path from ({start.X},{start.Y}) to ({end.X},{end.Y})");

        if (path!.Count == 0)
        {
            Assert.That(start, Is.EqualTo(end), "Empty path is only valid when start == end");
            return;
        }

        Assert.That(path.Count, Is.LessThanOrEqualTo(128), $"{algorithmName} path exceeds MaxPathLength");

        // Last node must reach the destination
        Assert.That(path[^1].Point, Is.EqualTo(end), $"{algorithmName} path must end at target");

        var prevPoint = start;
        foreach (var node in path)
        {
            var dx = Math.Abs(node.Point.X - prevPoint.X);
            var dy = Math.Abs(node.Point.Y - prevPoint.Y);

            Assert.That(Math.Max(dx, dy), Is.EqualTo(1),
                $"{algorithmName} has non-adjacent step from ({prevPoint.X},{prevPoint.Y}) to ({node.Point.X},{node.Point.Y})");

            // Each step must be walkable
            var cell = terrain[node.Point.X, node.Point.Y];
            Assert.That(IsWalkableCell(cell), Is.True,
                $"{algorithmName} path includes non-walkable tile at ({node.Point.X},{node.Point.Y}) value={cell}");

            prevPoint = node.Point;
        }
    }
}
