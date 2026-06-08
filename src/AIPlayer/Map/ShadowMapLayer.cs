// <copyright file="ShadowMapLayer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Map;

using MUnique.OpenMU.Pathfinding;

public sealed class ShadowMapLayer
{
    public const int MapSize = 256;
    public void AddEntry(byte x, byte y, ShadowEntry entry) { }
    public void RecordPathOutcome(Point from, Point to, bool success, Guid ownerId) { }
    public void RecordMonsterObservation(byte x, byte y, short monsterNumber, Guid ownerId) { }
    public void RecordPathTrail(Point from, Point to, int count, Guid ownerId) { }
}

public static class ShadowGridCostMerger
{
    public static byte[,] MergeGrid(byte[,] aiGrid, ShadowMapLayer shadowLayer, Point pos, Point target, Guid playerId) => new byte[256, 256];
}
