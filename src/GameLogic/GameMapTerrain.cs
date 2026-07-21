// <copyright file="GameMapTerrain.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic;

using System.Runtime.CompilerServices;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// The terrain of a map.
/// </summary>
public class GameMapTerrain
{
    /// <summary>
    /// The default terrain where all coordinates are walkable and not a safezone.
    /// </summary>
    private static readonly byte[] DefaultTerrain = Enumerable.Repeat<byte>(0, short.MaxValue).ToArray();

    /// <summary>
    /// Initializes a new instance of the <see cref="GameMapTerrain"/> class.
    /// </summary>
    /// <param name="definition">The game map definition.</param>
    public GameMapTerrain(GameMapDefinition definition)
        : this(definition?.TerrainData)
    {
        // 冰风谷硬编码安全区
        if (definition?.Number == 2)
            this.MarkSafeRect(220, 40, 235, 65);
        else if (definition?.Number == 3)
            this.MarkSafeRect(108, 171, 117, 177);
    }

    private void MarkSafeRect(int col1, int row1, int col2, int row2)
    {
        for (int col = col1; col <= col2 && col < 256; col++)
        for (int row = row1; row <= row2 && row < 256; row++)
        {
            this.SafezoneMap[col, row] = true;
            this.UpdateAiGridValue((byte)col, (byte)row);
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GameMapTerrain"/> class.
    /// </summary>
    /// <param name="terrainData">The terrain data.</param>
    public GameMapTerrain(byte[]? terrainData)
    {
        if (terrainData is { })
        {
            var firstLayerLength = Math.Min(terrainData.Length - 3, 65536);
            this.ReadTerrainData(terrainData.AsSpan(3, firstLayerLength));
        }
        else
        {
            this.ReadTerrainData(DefaultTerrain);
        }
    }

    /// <summary>
    /// Gets a grid of all safezone coordinates.
    /// </summary>
    public bool[,] SafezoneMap { get; } = new bool[256, 256];

    /// <summary>
    /// Gets a grid of all walkable coordinates.
    /// </summary>
    public bool[,] WalkMap { get; } = new bool[256, 256];

    /// <summary>
    /// Gets a grid of the walkable coordinates of monsters.
    /// </summary>
    public byte[,] AIgrid { get; } = new byte[256, 256];

    /// <summary>
    /// Gets a random drop coordinate at the specified point in the specified radius.
    /// </summary>
    /// <param name="point">The target point.</param>
    /// <param name="maximumRadius">The maximum radius around the specified coordinate.</param>
    /// <returns>The random drop coordinate.</returns>
    public Point GetRandomCoordinate(Point point, byte maximumRadius)
    {
        byte row = (byte)Rand.NextInt(Math.Max(0, point.X - maximumRadius), Math.Min(255, point.X + maximumRadius + 1));
        byte col = (byte)Rand.NextInt(Math.Max(0, point.Y - maximumRadius), Math.Min(255, point.Y + maximumRadius + 1));
        int i = 0;
        while (!this.WalkMap[col, row] && i < 20)
        {
            row = (byte)Rand.NextInt(Math.Max(0, point.X - maximumRadius), Math.Min(255, point.X + maximumRadius + 1));
            col = (byte)Rand.NextInt(Math.Max(0, point.Y - maximumRadius), Math.Min(255, point.Y + maximumRadius + 1));
            i++;
        }
        if (i == 20) return point;
        return new Point(row, col);
    }

    /// <summary>
    /// 找一个可行的出生坐标。
    /// WalkMap/Gate 坐标约定与 OpenMU 原有逻辑一致。
    /// 出生门内先找安全区，再无安全区则找可走格，仍然找不到返回 null。
    /// </summary>
    public Point? FindSpawnPoint(ExitGate? spawnGate)
    {
        if (spawnGate != null)
        {
            System.Console.WriteLine($"[SpawnFind] Gate=({spawnGate.X1},{spawnGate.Y1})-({spawnGate.X2},{spawnGate.Y2})");
            // 内部用 WalkMap[列,行]（和 ReadTerrainData 存储一致），返回 Point(行=posX, 列=posY)
            for (int attempt = 0; attempt < 20; attempt++)
            {
                byte col = (byte)Rand.NextInt(spawnGate.Y1, spawnGate.Y2 + 1);
                byte row = (byte)Rand.NextInt(spawnGate.X1, spawnGate.X2 + 1);
                if (this.WalkMap[col, row] && this.SafezoneMap[col, row])
                {
                    System.Console.WriteLine($"[SpawnFind] safe+walk found at (row={row},col={col})");
                    return new Point(row, col);
                }
            }
            for (int attempt = 0; attempt < 20; attempt++)
            {
                byte col = (byte)Rand.NextInt(spawnGate.Y1, spawnGate.Y2 + 1);
                byte row = (byte)Rand.NextInt(spawnGate.X1, spawnGate.X2 + 1);
                if (this.WalkMap[col, row])
                {
                    System.Console.WriteLine($"[SpawnFind] walk-only found at (row={row},col={col})");
                    return new Point(row, col);
                }
            }
            System.Console.WriteLine($"[SpawnFind] no valid point found in gate");
            return null;
        }

        for (byte col = 0; col < 256; col++)
        for (byte row = 0; row < 256; row++)
            if (this.WalkMap[col, row])
                return new Point(row, col);
        return new Point(100, 100);
    }

    /// <summary>
    /// Updates the ai grid value at the specified coordinate.
    /// </summary>
    /// <param name="x">The x.</param>
    /// <param name="y">The y.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateAiGridValue(byte x, byte y)
    {
        this.AIgrid[x, y] = (byte)((this.WalkMap[x, y] ? 1 : 0) | (this.SafezoneMap[x, y] ? 0b1000_0000 : 0));
    }

    /// <summary>
    /// Reads the terrain data from a stream.
    /// </summary>
    /// <param name="data">The data.</param>
    private void ReadTerrainData(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            byte x = (byte)(i & 0xFF);
            byte y = (byte)((i >> 8) & 0xFF);
            byte value = data[i];
            this.WalkMap[x, y] = value != 0xFF && (value & 0x5C) == 0;
            this.SafezoneMap[x, y] = (value & 0x01) != 0;
            this.UpdateAiGridValue(x, y);
        }
    }
}
