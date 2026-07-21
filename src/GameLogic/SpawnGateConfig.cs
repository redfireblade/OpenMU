// <copyright file="SpawnGateConfig.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic;

using System.IO;
using System.Text.Json;

/// <summary>
/// 从 spawn_gates.json 加载出生门配置，替代 ExitGate.IsSpawnGate
/// </summary>
public static class SpawnGateConfig
{
    private static Dictionary<int, SpawnArea>? _spawnAreas;

    /// <summary>
    /// 出生门区域定义
    /// </summary>
    public class SpawnArea
    {
        public int X1 { get; set; }
        public int Y1 { get; set; }
        public int X2 { get; set; }
        public int Y2 { get; set; }
    }

    /// <summary>
    /// 加载配置文件
    /// </summary>
    public static void Load(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                _spawnAreas = [];
                return;
            }
            var raw = File.ReadAllText(configPath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, List<List<int>>>>(raw);
            _spawnAreas = [];
            if (dict == null) return;

            foreach (var kv in dict)
            {
                if (int.TryParse(kv.Key, out int mapId) && kv.Value.Count > 0)
                {
                    var area = kv.Value[0];
                    if (area.Count >= 4)
                        _spawnAreas[mapId] = new SpawnArea { X1 = area[0], Y1 = area[1], X2 = area[2], Y2 = area[3] };
                }
            }
        }
        catch
        {
            _spawnAreas = [];
        }
    }

    /// <summary>
    /// 获取指定地图的出生门区域
    /// </summary>
    public static SpawnArea? GetSpawnArea(int mapId)
    {
        if (_spawnAreas == null) return null;
        _spawnAreas.TryGetValue(mapId, out var area);
        return area;
    }
}
