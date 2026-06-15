// <copyright file="MaterialFarmModule.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// 材料定向刷取 — 专门负责刷特定材料物品。
/// 当 MissionItem 的 Module="material_farm" 时使用。
/// 状态机: CheckMap → FindMonster → AttackMonster → CheckInventory。
/// </summary>
public sealed class MaterialFarmModule : IBehaviorSubModule
{
    private readonly AiPlayer _player;
    private readonly IGameAdapter _adapter;
    private readonly ILogger _logger;

    /// <summary>背包检查 tick 计数器，每 10 tick 检查一次。</summary>
    private int _inventoryCheckTick;

    /// <summary>当前会话中已刷到目标材料的计数（用完即停）。</summary>
    private int _collectedCount;

    public string ModuleId => "material_farm";

    public MaterialFarmModule(AiPlayer player, IGameAdapter adapter, ILogger logger)
    {
        this._player = player;
        this._adapter = adapter;
        this._logger = logger;
    }

    public async ValueTask<StepResult> ExecuteStepAsync(MissionItem item)
    {
        // === 解析 Context 参数 ===
        if (!TryGetContext(item, out var itemGroup, out var itemNumber, out var monsterNumber, out var mapNumber, out var requiredCount))
        {
            return StepResult.Failed;
        }

        var map = this._adapter.GetCurrentMap();
        if (map is null) return StepResult.Failed;

        // === 阶段1: CheckMap — 检查是否在目标地图 ===
        if (map.Definition.Number != mapNumber)
        {
            this._logger.LogInformation(
                "[MaterialFarm] 不在目标地图 #{TargetMap} (当前 #{CurMap})，请求传送",
                mapNumber, map.Definition.Number);

            // 设置 TargetMapNumber 让 HeartbeatService 处理跨地图传送
            if (this._player.Logic is { } logic)
            {
                logic.TargetMapNumber = mapNumber;
            }

            return StepResult.NoTarget;
        }

        var pos = this._adapter.GetPlayerPosition();

        // === 阶段2: FindMonster — 在范围内找目标怪物 ===
        var target = map.GetAttackablesInRange(pos, 20)
            .OfType<Monster>()
            .FirstOrDefault(m => m.IsAlive && m.Definition?.Number == monsterNumber);

        if (target is null)
        {
            // 没找到目标 → 巡逻
            var patrolTarget = new Point(
                (byte)Math.Clamp(pos.X + Random.Shared.Next(-20, 20), 0, 255),
                (byte)Math.Clamp(pos.Y + Random.Shared.Next(-20, 20), 0, 255));
            await this._adapter.WalkToAsync(patrolTarget, map).ConfigureAwait(false);
            this._logger.LogDebug(
                "[MaterialFarm] 怪物 #{Monster} 不在视野，巡逻到 ({X},{Y})",
                monsterNumber, patrolTarget.X, patrolTarget.Y);
            return StepResult.NoTarget;
        }

        // === 阶段3: AttackMonster — 走近→攻击 ===
        var dist = pos.EuclideanDistanceTo(target.Position);
        if (dist > 2.5f)
        {
            await this._adapter.WalkToAsync(
                new Point((byte)target.Position.X, (byte)target.Position.Y), map).ConfigureAwait(false);
            return StepResult.InProgress;
        }

        // 连续攻击直到目标死亡
        for (int attempt = 0; attempt < 10 && target.IsAlive; attempt++)
        {
            await this._adapter.HitAsync(target, 0, Direction.Undefined).ConfigureAwait(false);
        }

        // === 阶段4: CheckInventory — 每10tick检查背包是否有目标材料 ===
        this._inventoryCheckTick++;
        if (this._inventoryCheckTick >= 10)
        {
            this._inventoryCheckTick = 0;
            var inv = this._player.Inventory;
            if (inv is not null)
            {
                var currentCount = inv.Items.Count(i =>
                    i.Definition?.Group == itemGroup && i.Definition?.Number == itemNumber);

                if (currentCount > this._collectedCount)
                {
                    // 刷到了新材料，更新计数
                    this._collectedCount = currentCount;
                    this._logger.LogInformation(
                        "[MaterialFarm] ✅ 刷到材料 G{Group}N{Number}，当前 {Count}/{Required}",
                        itemGroup, itemNumber, currentCount, requiredCount);
                }

                if (currentCount >= requiredCount)
                {
                    this._logger.LogInformation(
                        "[MaterialFarm] ✅ 材料收集完成 G{Group}N{Number} ({Count}/{Required})",
                        itemGroup, itemNumber, currentCount, requiredCount);
                    return StepResult.Completed;
                }
            }
        }

        return StepResult.InProgress;
    }

    /// <summary>
    /// 从 MissionItem.Context 字典解析目标参数。
    /// </summary>
    private static bool TryGetContext(
        MissionItem item,
        out int itemGroup,
        out int itemNumber,
        out short monsterNumber,
        out ushort mapNumber,
        out int requiredCount)
    {
        itemGroup = 0;
        itemNumber = 0;
        monsterNumber = 0;
        mapNumber = 0;
        requiredCount = 1;

        if (!item.Context.TryGetValue("ItemGroup", out var groupObj))
        {
            return false;
        }

        if (groupObj is not int group)
        {
            return false;
        }

        if (!item.Context.TryGetValue("ItemNumber", out var numObj))
        {
            return false;
        }

        if (numObj is not int number)
        {
            return false;
        }

        if (!item.Context.TryGetValue("MonsterNumber", out var monObj))
        {
            return false;
        }

        if (monObj is not short monster)
        {
            return false;
        }

        if (!item.Context.TryGetValue("MapNumber", out var mapObj))
        {
            return false;
        }

        if (mapObj is not ushort map)
        {
            return false;
        }

        itemGroup = group;
        itemNumber = number;
        monsterNumber = monster;
        mapNumber = map;

        if (item.Context.TryGetValue("RequiredCount", out var countObj) && countObj is int count)
        {
            requiredCount = count;
        }

        return true;
    }
}
