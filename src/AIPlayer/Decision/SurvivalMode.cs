// <copyright file="SurvivalMode.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// 生存模式 — 所有任务做完了，靠自由刷怪维持。
/// 自动根据等级决定：升级→打钱→等事件。
/// </summary>
public sealed class SurvivalMode : IBehaviorSubModule
{
    private readonly AiPlayer _player;
    private readonly IGameAdapter _adapter;
    private readonly ILogger _logger;

    public string ModuleId => "survival";

    public SurvivalMode(AiPlayer player, IGameAdapter adapter, ILogger logger)
    {
        this._player = player;
        this._adapter = adapter;
        this._logger = logger;
    }

    public async ValueTask<StepResult> ExecuteStepAsync(MissionItem item)
    {
        var map = this._adapter.GetCurrentMap();
        if (map is null) return StepResult.Failed;

        // 低血 → 用药水 + 回安全区
        var hp = this._adapter.GetCurrentHp();
        var maxHp = this._adapter.GetMaxHp();
        if (maxHp > 0 && (float)hp / maxHp < 0.4f)
        {
            // 尝试使用最强可用药水 (slot 2=大红 1=中红 0=小红)
            for (byte slot = 2; slot >= 0; slot--)
            {
                await this._adapter.ConsumeItemAsync(slot).ConfigureAwait(false);
            }

            // 重新读取血量（喝药后可能已恢复）
            hp = this._adapter.GetCurrentHp();
            if ((float)hp / maxHp >= 0.3f)
            {
                return StepResult.InProgress; // 喝药后血量回到安全线
            }

            // 血量仍低 → 往安全区走
            var safeZone = new Point(130, 130); // 默认安全区中心
            // AiMap reference removed — just walk to map center
            await this._adapter.WalkToAsync(safeZone, map).ConfigureAwait(false);
        }
        // 有怪物 → 打
        var currentPos = this._adapter.GetPlayerPosition();
        var target = map.GetAttackablesInRange(currentPos, 20)
            .OfType<Monster>()
            .FirstOrDefault(m => m.IsAlive);

        if (target is not null)
        {
            var dist = currentPos.EuclideanDistanceTo(target.Position);
            if (dist > 2.5f)
            {
                await this._adapter.WalkToAsync(
                    new Point((byte)target.Position.X, (byte)target.Position.Y), map).ConfigureAwait(false);
                return StepResult.InProgress;
            }

            // 用最高伤害技能
            var skill = this._player.SkillList?.Skills
                .OrderByDescending(s => s.Skill?.AttackDamage ?? 0)
                .FirstOrDefault();
            if (skill?.Skill is not null)
            {
                await this._adapter.HitWithSkillAsync(target, skill).ConfigureAwait(false);
            }
            else
            {
                await this._adapter.HitAsync(target, 0, Direction.Undefined).ConfigureAwait(false);
            }
            return StepResult.InProgress;
        }

        // 没怪物 → 巡逻
        var rnd = new Point(
            (byte)Random.Shared.Next(Math.Max(0, currentPos.X - 20), Math.Min(255, currentPos.X + 20)),
            (byte)Random.Shared.Next(Math.Max(0, currentPos.Y - 20), Math.Min(255, currentPos.Y + 20)));
        await this._adapter.WalkToAsync(rnd, map).ConfigureAwait(false);
        return StepResult.NoTarget;
    }

    /// <summary>找到距离最近的安全区格坐标 — AiMap removed, returns null.</summary>
    private static Point? FindNearestSafeZone(Point pos)
    {
        return null;
    }
}
