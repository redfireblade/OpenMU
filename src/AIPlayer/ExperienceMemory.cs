// <copyright file="ExperienceMemory.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using Microsoft.Extensions.Logging;

public sealed class ExperienceMemory
{
    public static Task<Dictionary<int, object>> LoadAsync(string dataDir, string name) => Task.FromResult(new Dictionary<int, object>());
    public Task SaveAsync(Dictionary<int, object> mapStats, string dir, string name) => Task.CompletedTask;
    public void Consolidate(Dictionary<int, object> mapStats) { }
    public static List<(int MapNumber, double Score)> GetHotspotRanking(Dictionary<int, object> mapStats, int topN, ILogger logger) => new();
}
