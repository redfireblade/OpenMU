// <copyright file="ItemFarmModule.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using MUnique.OpenMU.DataModel.Configuration.Quests;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// 道具农场 — 任务需要某个道具才能接/交时，
/// 去对应怪物刷到掉落为止。
/// </summary>
public sealed class ItemFarmModule : IBehaviorSubModule
{
    private readonly AiPlayer _player;
    private readonly IGameAdapter _adapter;
    private readonly ILogger _logger;

    public string ModuleId => "item_farm";

    public ItemFarmModule(AiPlayer player, IGameAdapter adapter, ILogger logger)
    {
        this._player = player;
        this._adapter = adapter;
        this._logger = logger;
    }

    public async ValueTask<StepResult> ExecuteStepAsync(MissionItem item)
    {
        var quest = item.QuestDef;
        if (quest is null) return StepResult.Failed;

        var req = quest.RequiredItems.FirstOrDefault(r => !this.HasItem(r));
        if (req is null) return StepResult.Completed; // 道具够了

        var map = this._adapter.GetCurrentMap();
        if (map is null) return StepResult.Failed;

        // 从 DropItemGroup 知道掉哪个怪
        var monsterDef = req.DropItemGroup?.Monster;
        if (monsterDef is null)
        {
            this._logger.LogDebug("[ItemFarm] 不知道 {Item} 哪个怪掉", req.Item?.Name);
            return StepResult.NoTarget;
        }

        var pos = this._adapter.GetPlayerPosition();
        var target = map.GetAttackablesInRange(pos, 20)
            .OfType<Monster>()
            .FirstOrDefault(m => m.IsAlive && m.Definition?.Number == monsterDef.Number);

        if (target is null)
        {
            // 没找到目标 → 巡逻
            var rndTarget = new Point(
                (byte)Random.Shared.Next(Math.Max(0, pos.X - 15), Math.Min(255, pos.X + 15)),
                (byte)Random.Shared.Next(Math.Max(0, pos.Y - 15), Math.Min(255, pos.Y + 15)));
            await this._adapter.WalkToAsync(rndTarget, map).ConfigureAwait(false);
            return StepResult.NoTarget;
        }

        var dist = pos.EuclideanDistanceTo(target.Position);
        if (dist > 2.5f)
        {
            await this._adapter.WalkToAsync(
                new Point((byte)target.Position.X, (byte)target.Position.Y), map).ConfigureAwait(false);
            return StepResult.InProgress;
        }

        await this._adapter.HitAsync(target, 0, Direction.Undefined).ConfigureAwait(false);
        return StepResult.InProgress;
    }

    private bool HasItem(QuestItemRequirement req)
    {
        var inv = this._player.Inventory;
        if (inv is null) return false;
        return inv.Items.Any(i =>
            i.Definition?.Group == req.Item?.Group &&
            i.Definition?.Number == req.Item?.Number);
    }
}
