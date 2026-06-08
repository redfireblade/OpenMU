// <copyright file="RouteFollower.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Map;

using MUnique.OpenMU.Pathfinding;

public static class RouteFollower
{
    public static ValueTask<(bool hasMore, int nextStep)> WalkRouteAsync(AiPlayer player, AiMap aiMap, RouteTemplate route, int stepIndex, BehaviorContext context) => ValueTask.FromResult((false, 0));
}
