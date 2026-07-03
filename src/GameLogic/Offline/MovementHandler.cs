// <copyright file="MovementHandler.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Offline;

using MUnique.OpenMU.GameLogic.MuHelper;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// Handles movement logic including walking, regrouping, and return-to-origin.
/// </summary>
public sealed class MovementHandler
{
    private const byte RegroupDistanceThreshold = 1;

    private readonly Player _player;
    private readonly IMuHelperSettings? _config;
    private readonly Point _originPosition;

    private DateTime? _outOfRangeSince;

    /// <summary>
    /// Initializes a new instance of the <see cref="MovementHandler"/> class.
    /// </summary>
    /// <param name="player">The offline player.</param>
    /// <param name="config">The MU Helper configuration.</param>
    /// <param name="originPosition">The original spawn position.</param>
    public MovementHandler(Player player, IMuHelperSettings? config, Point originPosition)
    {
        this._player = player;
        this._config = config;
        this._originPosition = originPosition;
    }

    /// <summary>
    /// Gets the hunting range in tiles.
    /// </summary>
    private byte HuntingRange => CombatHandler.CalculateHuntingRange(this._config);

    /// <summary>
    /// Returns the character to the original position if configured and distance/time thresholds are met.
    /// </summary>
    /// <returns>True, if the loop can continue; False, if a regrouping walk was initiated.</returns>
    public async ValueTask<bool> RegroupAsync()
    {
        if (this._config is null || !this._config.ReturnToOriginalPosition)
        {
            return true;
        }

        if (this.ShouldRegroup(out var distance))
        {
            await this.WalkToAsync(this._originPosition).ConfigureAwait(false);
            this._outOfRangeSince = null;
            return false;
        }

        if (distance <= RegroupDistanceThreshold)
        {
            this._outOfRangeSince = null;
        }

        return true;
    }

    /// <summary>
    /// Moves the player closer to a target within the specified range.
    /// </summary>
    /// <param name="target">The target to move closer to.</param>
    /// <param name="range">The range to stop within.</param>
    public async ValueTask MoveCloserToTargetAsync(IAttackable target, byte range)
    {
        if (target.IsInRange(this._originPosition, this.HuntingRange))
        {
            var walkTarget = this._player.CurrentMap!.Terrain.GetRandomCoordinate(target.Position, range);
            await this.WalkToAsync(walkTarget).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Walks the player to the specified target position.
    /// </summary>
    /// <param name="target">The target position to walk to.</param>
    /// <returns>True if the walk was successful; otherwise, false.</returns>
    private async ValueTask<bool> WalkToAsync(Point target)
    {
        if (this._player.IsWalking || this._player.CurrentMap is not { } map)
        {
            return false;
        }

        var pathFinder = await this._player.GameContext.PathFinderPool.GetAsync().ConfigureAwait(false);
        try
        {
            pathFinder.ResetPathFinder();

            // 使用当前位置而非起止偏移判断，避免 WalkToAsync 中 offset>3 触发 MoveType.Instant 瞬移
            var currentPos = this._player.Position;
            var path = pathFinder.FindPath(currentPos, target, map.Terrain.AIgrid, false);
            if (path is null || path.Count == 0)
            {
                // 路径不通 → 找最近的可行走目标
                var altTarget = map.Terrain.GetRandomCoordinate(target, 3);
                currentPos = this._player.Position;
                path = pathFinder.FindPath(currentPos, altTarget, map.Terrain.AIgrid, false);
                if (path is null || path.Count == 0) return false;
                target = altTarget;
            }

            var stepsCount = Math.Min(path.Count, 10); // 10步最多，避免长途路径中间偏移
            var steps = new WalkingStep[stepsCount];
            var actualStart = this._player.Position;
            for (int i = 0; i < stepsCount; i++)
            {
                var node = path[i];
                var prevPos = i == 0 ? actualStart : steps[i - 1].To;
                steps[i] = new WalkingStep(prevPos, node.Point, prevPos.GetDirectionTo(node.Point));
            }

            // 确保第一步的起点与当前位置一致
            if (stepsCount > 0 && steps[0].From.EuclideanDistanceTo(actualStart) > 2)
            {
                steps[0] = new WalkingStep(actualStart, steps[0].To, actualStart.GetDirectionTo(steps[0].To));
            }

            await this._player.WalkToAsync(target, steps).ConfigureAwait(false);
            return true;
        }
        finally
        {
            this._player.GameContext.PathFinderPool.Return(pathFinder);
        }
    }

    private bool ShouldRegroup(out double distance)
    {
        distance = this._player.GetDistanceTo(this._originPosition);
        if (distance <= RegroupDistanceThreshold)
        {
            return false;
        }

        this._outOfRangeSince ??= DateTime.UtcNow;
        var secondsAway = (DateTime.UtcNow - this._outOfRangeSince.Value).TotalSeconds;

        return secondsAway >= this._config!.MaxSecondsAway || distance > this.HuntingRange;
    }
}
