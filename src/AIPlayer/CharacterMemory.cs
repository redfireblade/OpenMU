// <copyright file="CharacterMemory.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using MUnique.OpenMU.Pathfinding;

public sealed class CharacterMemory
{
    public string CharacterName { get; set; } = "";
    public Dictionary<int, object> MapStats { get; set; } = new();
    public Dictionary<string, HotspotStats> HotspotStats { get; set; } = new();
    public HashSet<Guid> UnlockedKnowledge { get; set; } = new();

    public static CharacterMemory? LoadAsync(string path) => null;
    public static CharacterMemory CreateNew(string name, int classNum, int level) => new() { CharacterName = name };

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

    public void UpdateFromPlayer(AiPlayer player) { }
    public void RecordMapVisit(ushort mapNum) { }
    public void AddAnnotation(string key, Point pos) { }
    public Task SaveAsync(string path) => Task.CompletedTask;
}
