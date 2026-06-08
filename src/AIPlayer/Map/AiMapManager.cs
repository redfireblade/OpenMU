// <copyright file="AiMapManager.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Map;

public sealed class AiMapManager
{
    public static AiMapManager Default { get; } = new();
    public AiMap GetOrCreateMap(ushort mapId) => new();
}
