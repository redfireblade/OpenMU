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
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GameMapTerrain"/> class.
    /// </summary>
    /// <param name="terrainData">The terrain data.</param>
    public GameMapTerrain(byte[]? terrainData)
    {
        if (terrainData is { })
        {
            this.ReadTerrainData(terrainData.AsSpan(3));
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
    /// 如果指定半径内无可走格子，逐步扩大搜索半径（最多到 50 格），确保返回可走坐标。
    /// 修复：出生/重生落在喷泉等不可走装饰物上的 BUG。
    /// </summary>
    /// <param name="point">The target point.</param>
    /// <param name="maximumRadius">The maximum radius around the specified coordinate.</param>
    /// <returns>The random drop coordinate.</returns>
    public Point GetRandomCoordinate(Point point, byte maximumRadius)
    {
        return this.GetRandomCoordinate(point, maximumRadius, false);
    }

    /// <summary>
    /// 获取安全区内的可走随机坐标。优先在安全区内找，如果安全区内不可走则按普通逻辑。
    /// 用于玩家出生/重生，保证不在非安全区外重生。
    /// </summary>
    public Point GetRandomSafezoneCoordinate(Point point, byte maximumRadius)
    {
        return this.GetRandomCoordinate(point, maximumRadius, true);
    }

    private Point GetRandomCoordinate(Point point, byte maximumRadius, bool requireSafezone)
    {
        // 快速尝试：在给定半径内随机找可走格
        for (int attempt = 0; attempt < 50; attempt++)
        {
            byte tempx = (byte)Rand.NextInt(Math.Max(0, point.X - maximumRadius), Math.Min(255, point.X + maximumRadius + 1));
            byte tempy = (byte)Rand.NextInt(Math.Max(0, point.Y - maximumRadius), Math.Min(255, point.Y + maximumRadius + 1));
            if (this.WalkMap[tempx, tempy] && (!requireSafezone || this.SafezoneMap[tempx, tempy]))
            {
                return new Point(tempx, tempy);
            }
        }

        // 50 次还没找到 → 逐步扩大半径扫描（从 2 格到 50 格）
        for (int radius = Math.Max(maximumRadius + 1, 2); radius <= 50; radius++)
        {
            for (int attempt = 0; attempt < 60; attempt++)
            {
                byte tempx = (byte)Rand.NextInt(Math.Max(0, point.X - radius), Math.Min(255, point.X + radius + 1));
                byte tempy = (byte)Rand.NextInt(Math.Max(0, point.Y - radius), Math.Min(255, point.Y + radius + 1));
                if (this.WalkMap[tempx, tempy] && (!requireSafezone || this.SafezoneMap[tempx, tempy]))
                {
                    return new Point(tempx, tempy);
                }
            }
        }

        // 要求安全区但实在找不到 → 降级为不要求安全区
        if (requireSafezone)
        {
            return this.GetRandomCoordinate(point, maximumRadius, false);
        }

        // 终极退路：全地图线性扫描找最近可走格
        for (int r = 1; r <= 100; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    int x = point.X + dx;
                    int y = point.Y + dy;
                    if (x >= 0 && x < 256 && y >= 0 && y < 256 && this.WalkMap[x, y])
                    {
                        return new Point((byte)x, (byte)y);
                    }
                }
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
            // 原始 .att 地形数据中，值 0=空地, 1=安全区, 2-9=可行走地面纹理
            // 值 10+ 包括装饰物(树/石头/喷泉)和边缘挡墙，但服务器端统一设为可走
            // 避免玩家卡在看似可走的位置（如仙踪林树木间的缝隙）
            this.WalkMap[x, y] = value != 0xFF;
            this.SafezoneMap[x, y] = value == 1;
            this.UpdateAiGridValue(x, y);
        }
    }
}