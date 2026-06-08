// <copyright file="KnowledgeAccessService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

public sealed class KnowledgeAccessService
{
    public KnowledgeAccessService(AiPlayer player, CharacterMemory memory) { }
    public bool IsUnlocked(string key) => true;
    public IEnumerable<KnowledgeEntry> GetUnlocked() => Enumerable.Empty<KnowledgeEntry>();
}

public sealed class KnowledgeEntry
{
    public Guid Id { get; set; }
}
