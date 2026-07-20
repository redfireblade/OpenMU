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
        // 安全区由 .att 文件定义 (value & 0x01) != 0 判定，无需硬编码
    }

    private void MarkSafeRectangle(int x1, int y1, int x2, int y2)
    {
        for (int x = x1; x <= x2 && x < 256; x++)
        for (int y = y1; y <= y2 && y < 256; y++)
        {
            this.SafezoneMap[x, y] = true;
            this.UpdateAiGridValue((byte)x, (byte)y);
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
            // .att 文件格式：3字节头 + 65536字节服务端地形 + 65536字节客户端纹理
            // 只读取第一层（服务端地形），避免第二层客户端纹理数据覆盖安全区标记
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
        byte tempx = (byte)Rand.NextInt(Math.Max(0, point.X - maximumRadius), Math.Min(255, point.X + maximumRadius + 1));
        byte tempy = (byte)Rand.NextInt(Math.Max(0, point.Y - maximumRadius), Math.Min(255, point.Y + maximumRadius + 1));
        int i = 0;
        while (!this.WalkMap[tempx, tempy] && i < 20)
        {
            tempx = (byte)Rand.NextInt(Math.Max(0, point.X - maximumRadius), Math.Min(255, point.X + maximumRadius + 1));
            tempy = (byte)Rand.NextInt(Math.Max(0, point.Y - maximumRadius), Math.Min(255, point.Y + maximumRadius + 1));
            i++;
        }

        if (i == 20)
        {
            return point;
        }

        return new Point(tempx, tempy);
    }

    /// <summary>
    /// Gets a random safezone coordinate near the specified point.
    /// </summary>
    public Point GetRandomSafezoneCoordinate(Point point, byte maximumRadius)
    {
        for (int r = 0; r <= maximumRadius; r++)
        {
            for (int attempt = 0; attempt < 30; attempt++)
            {
                byte tx = (byte)Rand.NextInt(Math.Max(0, point.X - r), Math.Min(255, point.X + r + 1));
                byte ty = (byte)Rand.NextInt(Math.Max(0, point.Y - r), Math.Min(255, point.Y + r + 1));
                if (this.WalkMap[tx, ty] && this.SafezoneMap[tx, ty])
                    return new Point(tx, ty);
            }
        }
        return point;
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
            // 匹配客户端 TerrainWall 判定位掩码: 0x5C = NOMOVE(0x04)|NOGROUND(0x08)|WATER(0x10)|HEIGHT(0x40)
            this.WalkMap[x, y] = value != 0xFF && (value & 0x5C) == 0;
            // 安全区: 客户端判定 (v & 0x01) != 0，值 1/3/5/7 等都算安全区
            this.SafezoneMap[x, y] = (value & 0x01) != 0;
            this.UpdateAiGridValue(x, y);
        }
    }
}