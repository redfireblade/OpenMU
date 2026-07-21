// <copyright file="AiLoop.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// AI 角色主循环 — 感知→决策→行动。
/// 由 AiHost 的分帧调度器调用，不在独立线程运行。
/// </summary>
public sealed class AiLoop
{
    private readonly AiEntity _entity;
    private int _tickCount;

    internal AiLoop(AiEntity entity)
    {
        this._entity = entity;
    }

    /// <summary>
    /// 执行一帧 AI 逻辑。
    /// 由 AiHost 的定时器每帧调度。
    /// </summary>
    public async ValueTask TickAsync()
    {
        if (!this._entity.IsAlive) return;
        if (this._entity.CurrentMap is null) return;

        this._tickCount++;

        try
        {
            // Phase 1: 行走（目前仅巡逻）
            if (!this._entity.IsWalking)
            {
                await this.RoamAsync();
            }
        }
        catch (Exception ex)
        {
            // 单个 AI 异常不影响其他 AI
            System.Diagnostics.Debug.WriteLine($"[AiLoop] {this._entity.Name}: {ex.Message}");
        }
    }

    private async ValueTask RoamAsync()
    {
        var map = this._entity.CurrentMap!;
        var pos = this._entity.Position;
        var rng = Random.Shared;

        for (int i = 0; i < 5; i++)
        {
            var rx = (byte)Math.Clamp(pos.X + rng.Next(-15, 16), 5, 250);
            var ry = (byte)Math.Clamp(pos.Y + rng.Next(-15, 16), 5, 250);
            if (!map.Terrain.WalkMap[ry, rx]) continue;
            if (Math.Abs(rx - pos.X) < 3 && Math.Abs(ry - pos.Y) < 3) continue;

            await this._entity.WalkTargetAsync(new Point(rx, ry));
            return;
        }
    }
}
