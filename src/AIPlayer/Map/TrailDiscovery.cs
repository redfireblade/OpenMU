// <copyright file="TrailDiscovery.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Map;

using MUnique.OpenMU.Pathfinding;

public static class TrailDiscovery
{
    public static object? FindTrail(AiMap aiMap, Point pos) => null;
    public static Task FollowTrailAsync(AiPlayer player, AiMap aiMap, BehaviorContext context) => Task.CompletedTask;
}
