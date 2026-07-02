// <copyright file="AlgorithmTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Pathfinding.Tests;

using MUnique.OpenMU.Pathfinding.Algorithms;

/// <summary>
/// Tests for all pathfinding algorithms to verify they return valid paths.
/// </summary>
[TestFixture]
public class AlgorithmTests
{
    private byte[,] _grid = null!;

    /// <summary>
    /// Sets up a basic, unrestricted grid with a safezone region.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        this._grid = new byte[0x100, 0x100];
        for (int x = 100; x < 200; x++)
        {
            for (int y = 100; y < 200; y++)
            {
                this._grid[x, y] = 10;
            }
        }

        // Safezone (bit 7 set, cost = 1):
        for (int x = 50; x < 100; x++)
        {
            for (int y = 50; y < 100; y++)
            {
                this._grid[x, y] = 0b1000_0001;
            }
        }
    }

    /// <summary>
    /// Tests that all registered algorithms return a valid path for a straight line.
    /// </summary>
    [Test]
    public void AllAlgorithmsFindStraightPath()
    {
        var selector = new AlgorithmSelector();
        selector.RegisterAlgorithms();
        var start = new Point(110, 100);
        var end = new Point(120, 100);
        var algorithms = selector.GetAll();

        Assert.That(algorithms.Count, Is.GreaterThanOrEqualTo(6), "Expected at least 6 algorithms");

        foreach (var algorithm in algorithms)
        {
            var result = algorithm.FindPath(start, end, this._grid, false);
            Assert.That(result, Is.Not.Null, $"{algorithm.Name} should find a straight path");
            if (result is { Count: > 0 })
            {
                Assert.That(result[^1].Point, Is.EqualTo(end), $"{algorithm.Name} should end at target");
            }
        }
    }

    /// <summary>
    /// Tests that all registered algorithms return a valid path for a diagonal line.
    /// </summary>
    [Test]
    public void AllAlgorithmsFindDiagonalPath()
    {
        var selector = new AlgorithmSelector();
        selector.RegisterAlgorithms();
        var start = new Point(100, 100);
        var end = new Point(110, 110);
        var algorithms = selector.GetAll();

        foreach (var algorithm in algorithms)
        {
            var result = algorithm.FindPath(start, end, this._grid, false);
            Assert.That(result, Is.Not.Null, $"{algorithm.Name} should find a diagonal path");
            if (result is { Count: > 0 })
            {
                Assert.That(result[^1].Point, Is.EqualTo(end), $"{algorithm.Name} should end at target");
                Assert.That(result.Count, Is.LessThanOrEqualTo(128), $"{algorithm.Name} path should not exceed MaxPathLength");
            }
        }
    }

    /// <summary>
    /// Tests that algorithms return null when start is unreachable.
    /// </summary>
    [Test]
    public void AllAlgorithmsReturnNullForUnreachableStart()
    {
        var selector = new AlgorithmSelector();
        selector.RegisterAlgorithms();
        var start = new Point(0, 0); // unreachable (cost = 0)
        var end = new Point(110, 100);
        var algorithms = selector.GetAll();

        foreach (var algorithm in algorithms)
        {
            var result = algorithm.FindPath(start, end, this._grid, false);
            Assert.That(result, Is.Null, $"{algorithm.Name} should return null for unreachable start");
        }
    }

    /// <summary>
    /// Tests that algorithms return empty list when start equals end.
    /// </summary>
    [Test]
    public void AllAlgorithmsReturnEmptyForSameStartEnd()
    {
        var selector = new AlgorithmSelector();
        selector.RegisterAlgorithms();
        var point = new Point(110, 100);
        var algorithms = selector.GetAll();

        foreach (var algorithm in algorithms)
        {
            var result = algorithm.FindPath(point, point, this._grid, false);
            Assert.That(result, Is.Not.Null, $"{algorithm.Name} should not return null for same start/end");
            Assert.That(result!.Count, Is.EqualTo(0), $"{algorithm.Name} should return empty path for same start/end");
        }
    }

    /// <summary>
    /// Tests safezone handling across all algorithms.
    /// </summary>
    [Test]
    public void AllAlgorithmsHandleSafezoneCorrectly()
    {
        var selector = new AlgorithmSelector();
        selector.RegisterAlgorithms();
        var start = new Point(51, 60);
        var end = new Point(60, 60);
        var algorithms = selector.GetAll();

        foreach (var algorithm in algorithms)
        {
            // With safezone included, should find path
            var resultWithSafezone = algorithm.FindPath(start, end, this._grid, true);
            Assert.That(resultWithSafezone, Is.Not.Null, $"{algorithm.Name} should find path in safezone when included");

            // Without safezone, should NOT find path (start is in safezone)
            var resultWithoutSafezone = algorithm.FindPath(start, end, this._grid, false);
            Assert.That(resultWithoutSafezone, Is.Null, $"{algorithm.Name} should not path through safezone when excluded");
        }
    }

    /// <summary>
    /// Tests RandomWalk produces different results with different seeds (nondeterministic).
    /// </summary>
    [Test]
    public void RandomWalkIsNondeterministic()
    {
        var algorithms = new AlgorithmSelector();
        algorithms.RegisterAlgorithms();
        var algorithm = algorithms.Select(SelectionMode.ByName, "RandomWalk");
        Assert.That(algorithm, Is.Not.Null);

        var start = new Point(100, 100);
        var end = new Point(150, 150);

        // Run twice and compare - RandomWalk should produce different paths
        var result1 = algorithm!.FindPath(start, end, this._grid, false);
        var result2 = algorithm!.FindPath(start, end, this._grid, false);

        Assert.That(result1, Is.Not.Null);
        Assert.That(result2, Is.Not.Null);

        if (result1!.Count > 0 && result2!.Count > 0)
        {
            // At least one step should differ (very high probability)
            var differs = false;
            for (int i = 0; i < Math.Min(result1.Count, result2.Count); i++)
            {
                if (result1[i].Point != result2[i].Point)
                {
                    differs = true;
                    break;
                }
            }

            Assert.That(differs, Is.True, "RandomWalk should produce different paths on successive runs");
        }
    }

    /// <summary>
    /// Tests that AlgorithmSelector modes work correctly.
    /// </summary>
    [Test]
    public void AlgorithmSelectorModesWork()
    {
        var selector = new AlgorithmSelector();
        selector.RegisterAlgorithms();

        // ByName should return the correct algorithm
        var aStar = selector.Select(SelectionMode.ByName, "AStar");
        Assert.That(aStar, Is.Not.Null);
        Assert.That(aStar!.Name, Is.EqualTo("AStar"));

        var antColony = selector.Select(SelectionMode.ByName, "AntColony");
        Assert.That(antColony, Is.Not.Null);
        Assert.That(antColony!.Name, Is.EqualTo("AntColony"));

        // Random should return a non-null algorithm
        var random = selector.Select(SelectionMode.Random);
        Assert.That(random, Is.Not.Null);

        // RoundRobin should cycle through algorithms
        var first = selector.Select(SelectionMode.RoundRobin);
        var second = selector.Select(SelectionMode.RoundRobin);
        Assert.That(first, Is.Not.Null);
        Assert.That(second, Is.Not.Null);

        // Invalid name should return null
        var invalid = selector.Select(SelectionMode.ByName, "NonExistent");
        Assert.That(invalid, Is.Null);
    }
}
