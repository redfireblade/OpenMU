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
    public double Score => this.TotalActiveSeconds > 0
        ? (this.TotalExperience / Math.Max(1, this.TotalActiveSeconds) * 0.35)
          + ((double)this.TotalKills / Math.Max(1, this.TotalActiveSeconds) * 0.15)
          + ((1.0 - Math.Min(1.0, (double)this.TotalDeaths / Math.Max(1, this.TotalKills))) * 0.25)
          + ((double)this.TotalDrops / Math.Max(1, this.TotalKills) * 0.15)
          + ((this.ExcellentDrops * 5.0 + this.JewelDrops * 10.0) / Math.Max(1, this.TotalKills) * 0.10)
        : 0;

    public static string MakeKey(ushort mapNum, byte x, byte y) => $"{mapNum}_{x}_{y}";
}
