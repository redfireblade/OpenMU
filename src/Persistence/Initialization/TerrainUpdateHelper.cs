// <copyright file="TerrainUpdateHelper.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization;

using System.Diagnostics;
using System.IO;
using System.Reflection;
using MUnique.OpenMU.DataModel.Configuration;

/// <summary>
/// Update helper for terrain data.
/// </summary>
internal static class TerrainUpdateHelper
{
    /// <summary>
    /// Updates the <see cref="GameMapDefinition.TerrainData"/> from the embedded resources.
    /// </summary>
    /// <param name="gameMapDefinition">The game map definition.</param>
    /// <param name="terrainVersionPrefix">The terrain version prefix.</param>
    public static void UpdateTerrainFromResources(this GameMapDefinition gameMapDefinition, string terrainVersionPrefix = "")
    {
        var assembly = Assembly.GetExecutingAssembly();
        var terrainResourceName = gameMapDefinition.GetTerrainFileName(terrainVersionPrefix);
        if (string.IsNullOrWhiteSpace(terrainResourceName))
        {
            return;
        }

        using var stream = assembly.GetManifestResourceStream(terrainResourceName);
        if (stream is not null)
        {
            using var reader = new BinaryReader(stream);
            var terrainData = reader.ReadBytes(3 * ushort.MaxValue);
            gameMapDefinition.TerrainData = terrainData;
        }
        else
        {
            Debug.Fail($"Couldn't find terrain resource {terrainResourceName} for map {gameMapDefinition.Name}.");
        }
    }

    private static string GetTerrainFileName(this GameMapDefinition gameMapDefinition, string terrainVersionPrefix = "")
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceNames = assembly.GetManifestResourceNames();

        // 先尝试匹配自己的地图号（修复 Map 0 无地形文件时错误加载 Map 1 地形的BUG）
        for (var mapNumber = gameMapDefinition.Number; mapNumber >= 0 && mapNumber >= gameMapDefinition.Number - 10; mapNumber--)
        {
            var candidate = $"{assembly.GetName().Name}.Resources.{terrainVersionPrefix}Terrain{mapNumber}{(gameMapDefinition.Discriminator > 0 ? ("_" + gameMapDefinition.Discriminator) : string.Empty)}.att";
            if (resourceNames.Contains(candidate))
            {
                return candidate;
            }

            if (gameMapDefinition.Discriminator > 0)
            {
                var candidate2 = $"{assembly.GetName().Name}.Resources.{terrainVersionPrefix}Terrain{mapNumber}.att";
                if (resourceNames.Contains(candidate2))
                {
                    return candidate2;
                }
            }

            if (!char.IsDigit(gameMapDefinition.Name.ValueInNeutralLanguageAsSpan[^1]))
            {
                break;
            }
        }

        // 没有找到地形文件：生成一个全可走的默认地形（修复 Map 0 洛伦西亚的不可移动BUG）
        var defaultTerrain = new byte[3 * ushort.MaxValue];
        for (int i = 0; i < defaultTerrain.Length; i++)
        {
            // 默认所有格子可走（值0=可走非安全区，值1=可走安全区）
            // 中央100x100区域设为安全区（值1）
            byte x = (byte)(i & 0xFF);
            byte y = (byte)((i >> 8) & 0xFF);
            defaultTerrain[i] = (x >= 80 && x < 180 && y >= 80 && y < 180) ? (byte)1 : (byte)0;
        }

        gameMapDefinition.TerrainData = defaultTerrain;
        Debug.WriteLine($"[TerrainUpdateHelper] 未找到地形文件，已为地图 {gameMapDefinition.Name} (#{gameMapDefinition.Number}) 生成默认全可走地形");
        return string.Empty;
    }
}