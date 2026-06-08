// <copyright file="ShadowEntry.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Map;

public sealed class ShadowEntry
{
    public Guid Id { get; set; }
    public byte X { get; set; }
    public byte Y { get; set; }
    public ShadowEntryType Type { get; set; }
    public int SubType { get; set; }
    public ShadowVisibility Visibility { get; set; }
    public Guid OwnerId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public Guid[] References { get; set; } = Array.Empty<Guid>();
}

public enum ShadowEntryType
{
    DangerZone = 0,
}

public enum ShadowVisibility
{
    AIOnly = 0,
}
