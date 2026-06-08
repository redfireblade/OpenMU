// <copyright file="AccountKnowledge.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

public sealed class AccountKnowledge
{
    public HashSet<Guid> UnlockedKnowledge { get; set; } = new();
    public Dictionary<int, object> SharedMapStats { get; set; } = new();
    public static AccountKnowledge Load(string dir) => new();
    public void Save(string dir) { }
    public void MergeMapStats(Dictionary<int, object> mapStats) { }
    public void MergeUnlockedKnowledge(HashSet<Guid> unlocked) { }
}
