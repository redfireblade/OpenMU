// <copyright file="HotspotRankingService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using Microsoft.Extensions.Logging;

public static class HotspotRankingService
{
    public static List<(string Key, double Score)> GetHotspotRanking(Dictionary<string, HotspotStats> stats, int topN, ILogger? logger) => new();
}
