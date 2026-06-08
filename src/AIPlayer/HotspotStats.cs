// <copyright file="HotspotStats.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

public sealed class HotspotStats
{
    public double TotalActiveSeconds { get; set; }
    public DateTime LastVisitedAt { get; set; }
    public int SessionsCount { get; set; }
    public int TotalKills { get; set; }
    public double TotalExperience { get; set; }
    public int TotalDeaths { get; set; }
    public int TotalDrops { get; set; }
    public int ExcellentDrops { get; set; }
    public int JewelDrops { get; set; }
    public double Score => 0;

    public static string MakeKey(ushort mapNum, byte x, byte y) => $"{mapNum}_{x}_{y}";
}
