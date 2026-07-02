// <copyright file="TerrainToAIGridConverter.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.AIPlayer.World;

/// <summary>
/// Converts <see cref="WorldTile"/> terrain grids to the <c>byte[,]</c> AIgrid format
/// consumed by pathfinding algorithms, using the same encoding as
/// <see cref="MUnique.OpenMU.GameLogic.GameMapTerrain.UpdateAiGridValue"/>.
/// </summary>
internal static class TerrainToAIGridConverter
{
    /// <summary>
    /// Converts a <see cref="WorldTile"/> grid to a <c>byte[,]</c> AIgrid.
    /// </summary>
    /// <param name="tiles">The world tile grid (must be 256x256).</param>
    /// <returns>A byte grid where 0=blocked, 1=walkable, 0x81=walkable+safezone.</returns>
    public static byte[,] Convert(WorldTile[,] tiles)
    {
        var width = tiles.GetLength(0);
        var height = tiles.GetLength(1);
        var grid = new byte[width, height];

        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                var tile = tiles[x, y];
                grid[x, y] = tile.Walkable
                    ? (byte)(1 | (tile.SafeZone ? 0b1000_0000 : 0))
                    : (byte)0;
            }
        }

        return grid;
    }
}
