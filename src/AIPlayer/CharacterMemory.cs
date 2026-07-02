// <copyright file="CharacterMemory.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using System.IO;
using System.Text.Json;
using MUnique.OpenMU.AIPlayer.Knowledge.KnowledgeGraph;
using MUnique.OpenMU.Pathfinding;

public sealed class CharacterMemory
{
    private static readonly int ItemDomainIdGroupShift = 32;
    private static readonly long ItemDomainIdNumberMask = 0xFFFFFFFF;

    public string CharacterName { get; set; } = "";
    public Dictionary<int, object> MapStats { get; set; } = new();
    public Dictionary<string, HotspotStats> HotspotStats { get; set; } = new();
    public HashSet<Guid> UnlockedKnowledge { get; set; } = new();

    /// <summary>
    /// Gets or sets the observed drop rates learned from runtime gameplay.
    /// Key is a composite string "Monster{monsterNumber}_Item{group}_{number}".
    /// These are merged from the <see cref="KnowledgeGraphRuntimeLearner"/> and
    /// persisted with the character across AI player sessions.
    /// </summary>
    public Dictionary<string, ObservedDropData> ObservedDropRates { get; set; } = new();

    /// <summary>
    /// JSON serializer options used for persistence.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Loads a <see cref="CharacterMemory"/> from a JSON file.
    /// Returns <c>null</c> if the file does not exist or deserialization fails.
    /// </summary>
    /// <param name="path">The full file path to load from.</param>
    /// <returns>The deserialized character memory, or <c>null</c>.</returns>
    public static async Task<CharacterMemory?> LoadAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var memory = JsonSerializer.Deserialize<CharacterMemory>(json, JsonOptions);
            if (memory is null)
            {
                return null;
            }

            // Ensure sub-collections are never null after deserialization
            memory.MapStats ??= new Dictionary<int, object>();
            memory.HotspotStats ??= new Dictionary<string, HotspotStats>();
            memory.UnlockedKnowledge ??= new HashSet<Guid>();
            memory.ObservedDropRates ??= new Dictionary<string, ObservedDropData>();

            return memory;
        }
        catch (Exception)
        {
            // Corrupted file — return null so caller falls back to CreateNew
            return null;
        }
    }

    /// <summary>
    /// Creates a new <see cref="CharacterMemory"/> with default values for a fresh character.
    /// </summary>
    public static CharacterMemory CreateNew(string name, int classNum, int level) => new() { CharacterName = name };

    /// <summary>
    /// Merges observed drop data from the runtime learner into persistent memory.
    /// Existing entries are overwritten with the latest observation values.
    /// </summary>
    /// <param name="observations">The observations from the <see cref="KnowledgeGraphRuntimeLearner"/>,
    /// keyed by (MonsterNodeId, ItemNodeId).</param>
    public void MergeObservedDrops(IEnumerable<KeyValuePair<(NodeId MonsterNodeId, NodeId ItemNodeId), DropObservation>> observations)
    {
        foreach (var kvp in observations)
        {
            var (monsterNodeId, itemNodeId) = kvp.Key;
            var observation = kvp.Value;

            var itemGroup = (int)(itemNodeId.DomainId >> ItemDomainIdGroupShift);
            var itemNumber = (int)(itemNodeId.DomainId & ItemDomainIdNumberMask);
            var key = $"Monster{monsterNodeId.DomainId}_Item{itemGroup}_{itemNumber}";

            if (this.ObservedDropRates.TryGetValue(key, out var existing))
            {
                existing.ObservedCount = observation.ObservedCount;
                existing.TotalKills = observation.TotalKills;
            }
            else
            {
                this.ObservedDropRates[key] = new ObservedDropData
                {
                    MonsterNumber = (short)monsterNodeId.DomainId,
                    ItemGroup = itemGroup,
                    ItemNumber = itemNumber,
                    ObservedCount = observation.ObservedCount,
                    TotalKills = observation.TotalKills,
                };
            }
        }
    }

    public HotspotStats GetOrCreateHotspot(ushort mapNum, byte x, byte y)
    {
        var key = global::MUnique.OpenMU.AIPlayer.HotspotStats.MakeKey(mapNum, x, y);
        if (!HotspotStats.TryGetValue(key, out var hs))
        {
            hs = new HotspotStats();
            HotspotStats[key] = hs;
        }
        return hs;
    }

    /// <summary>
    /// 从玩家当前状态更新记忆。
    /// </summary>
    public void UpdateFromPlayer(AiPlayer player)
    {
        var map = player.CurrentMap;
        if (map is null) return;

        var mapNum = (ushort)map.Definition.Number;
        var x = (byte)player.Position.X;
        var y = (byte)player.Position.Y;

        // 记录地图访问
        this.RecordMapVisit(mapNum);

        // 记录当前位置到热点统计
        var hotspot = this.GetOrCreateHotspot(mapNum, x, y);
        hotspot.SessionsCount++;
        hotspot.LastVisitedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// 记录到访某地图。
    /// </summary>
    public void RecordMapVisit(ushort mapNum)
    {
        if (!this.MapStats.TryGetValue(mapNum, out var stats))
        {
            stats = new object();
            this.MapStats[mapNum] = stats;
        }
    }
    public void AddAnnotation(string key, Point pos) { }

    /// <summary>
    /// Persists this <see cref="CharacterMemory"/> to a JSON file.
    /// Creates the directory if it does not exist.
    /// </summary>
    /// <param name="path">The full file path to write to.</param>
    public async Task SaveAsync(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(this, JsonOptions);
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Log but never throw from memory persistence
            System.Diagnostics.Debug.WriteLine($"[CharacterMemory] Save failed: {ex.Message}");
        }
    }
}
