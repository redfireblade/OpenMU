// <copyright file="RouteTemplate.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Map;

using MUnique.OpenMU.Pathfinding;

public sealed class RouteTemplate
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<Point> Points { get; set; } = new();
    public string Source { get; set; } = string.Empty;
    public string TeamId { get; set; } = string.Empty;
    public int JitterRadius { get; set; }
}
