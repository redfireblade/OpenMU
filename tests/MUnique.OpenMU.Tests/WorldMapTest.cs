// <copyright file="WorldMapTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.IO;
using MUnique.OpenMU.AIPlayer.World;

/// <summary>
/// Tests for <see cref="WorldMapBuilder"/> and <see cref="WorldMap"/>.
/// </summary>
[TestFixture]
public class WorldMapTest
{
    internal static string FindResourcesDirectory()
    {
        // Walk up from the test assembly location to find the solution root,
        // then descend into the Persistence.Initialization Resources directory.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Persistence", "Initialization", "Resources");
            if (Directory.Exists(candidate) && Directory.GetFiles(candidate, "Terrain*.att").Length > 0)
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        // Fallback: check relative to current directory
        var fallback = Path.GetFullPath(Path.Combine(
            Environment.CurrentDirectory,
            "..", "..", "..", "..", "..",
            "src", "Persistence", "Initialization", "Resources"));

        if (Directory.Exists(fallback))
        {
            return fallback;
        }

        Assert.Fail("Could not find the .att terrain resources directory.");
        return string.Empty;
    }

    /// <summary>
    /// Tests that all .att terrain files can be loaded and produce a valid world map.
    /// </summary>
    [Test]
    public void AllTerrainFilesLoadSuccessfully()
    {
        var resourcesDir = FindResourcesDirectory();
        var worldMap = WorldMapBuilder.BuildFromDirectory(resourcesDir);

        Assert.That(worldMap, Is.Not.Null);
        Assert.That(worldMap.Maps.Count, Is.GreaterThanOrEqualTo(30),
            "Expected at least 30 map definitions to be loaded from .att files");

        foreach (var (mapNumber, map) in worldMap.Maps)
        {
            Assert.That(map.Tiles.GetLength(0), Is.EqualTo(256),
                $"Map {mapNumber} ({map.Name}) should have 256 columns");
            Assert.That(map.Tiles.GetLength(1), Is.EqualTo(256),
                $"Map {mapNumber} ({map.Name}) should have 256 rows");
            Assert.That(map.Name, Is.Not.Null.And.Not.Empty,
                $"Map {mapNumber} should have a name");
        }
    }

    /// <summary>
    /// Tests that well-known maps have the correct walkable areas.
    /// </summary>
    [Test]
    public void WellKnownMapsHaveCorrectTiles()
    {
        var resourcesDir = FindResourcesDirectory();
        var worldMap = WorldMapBuilder.BuildFromDirectory(resourcesDir);

        // Lorencia (map 0): spawn area should be walkable
        Assert.That(worldMap.IsWalkable(0, 130, 125), Is.True,
            "Lorencia spawn area (130,125) should be walkable");

        // A safezone in Lorencia
        var lorencia = worldMap.GetTile(0, 130, 125);
        Assert.That(lorencia.Walkable, Is.True);

        // Noria (map 3)
        Assert.That(worldMap.IsWalkable(3, 150, 120), Is.True,
            "Noria should have walkable tiles");
    }

    /// <summary>
    /// Tests that the number of loaded maps matches expected count.
    /// </summary>
    [Test]
    public void MapCountMatchesExpected()
    {
        var resourcesDir = FindResourcesDirectory();
        var worldMap = WorldMapBuilder.BuildFromDirectory(resourcesDir);

        // We have 69 .att files, but some are variants (same map, different discriminator)
        // So the unique map count should be >= 30
        Assert.That(worldMap.Maps.Count, Is.GreaterThanOrEqualTo(30));

        // Fundamental maps that MUST exist (note: map 8/Tarkan has no terrain file)
        var expectedMaps = new ushort[] { 0, 1, 2, 3, 4, 5, 6, 7 };
        foreach (var mapNumber in expectedMaps)
        {
            Assert.That(worldMap.Maps.ContainsKey(mapNumber), Is.True,
                $"Map {mapNumber} should be present in the world map");
            Assert.That(worldMap.Maps[mapNumber].Name, Is.Not.Null.And.Not.Empty);
        }
    }

    /// <summary>
    /// Tests that the builder handles non-existent directory gracefully.
    /// </summary>
    [Test]
    public void EmptyDirectoryReturnsEmptyWorld()
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), "nonexistent_terrain_dir_" + Guid.NewGuid());
        var worldMap = WorldMapBuilder.BuildFromDirectory(emptyDir);
        Assert.That(worldMap, Is.Not.Null);
        Assert.That(worldMap.Maps.Count, Is.EqualTo(0));
    }
}
