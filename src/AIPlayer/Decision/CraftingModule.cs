// <copyright file="CraftingModule.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.Pathfinding;
using Microsoft.Extensions.Logging;

/// <summary>
/// 合成执行模块 — 驱动混沌合成流程。
/// 状态机: WalkToNpc → OpenDialog → MoveItemsToTmp → DoMix → MoveResultBack。
/// 第一版：走完 WalkToNpc + OpenDialog，Mix 步骤委托给游戏 ItemCraftAction。
/// </summary>
public sealed class CraftingModule : IBehaviorSubModule
{
    private readonly AiPlayer _player;
    private readonly IGameAdapter _adapter;
    private readonly ILogger _logger;
    private readonly TalkNpcAction _talkNpcAction = new();
    private readonly ItemCraftAction _craftAction = new();
    private readonly MoveItemAction _moveItemAction = new();

    /// <summary>混沌合成 NPC 编号 (Chaos Goblin)。</summary>
    private const short ChaosGoblinNumber = 237;

    public string ModuleId => "crafting_executor";

    public CraftingModule(AiPlayer player, IGameAdapter adapter, ILogger logger)
    {
        this._player = player;
        this._adapter = adapter;
        this._logger = logger;
    }

    /// <summary>
    /// 执行合成任务的一步。
    /// 状态机通过 CheckState() 判断当前所属阶段并推进。
    /// </summary>
    public async ValueTask<StepResult> ExecuteStepAsync(MissionItem item)
    {
        var map = this._adapter.GetCurrentMap();
        if (map is null) return StepResult.Failed;

        var npc = map.GetNpcsInRange(this._player.Position, 150)
            .FirstOrDefault(n => n.Definition?.Number == ChaosGoblinNumber);

        // Phase 1: 找不到 NPC → 巡逻搜索
        if (npc is null)
        {
            this._logger.LogDebug("[Crafting] 混沌合成NPC #{Num} 不在视野，巡逻中", ChaosGoblinNumber);
            var pos = this._adapter.GetPlayerPosition();
            var walkTarget = new Point(
                (byte)Math.Clamp(pos.X + Random.Shared.Next(-15, 15), 0, 255),
                (byte)Math.Clamp(pos.Y + Random.Shared.Next(-15, 15), 0, 255));
            await this._adapter.WalkToAsync(walkTarget, map).ConfigureAwait(false);
            return StepResult.InProgress;
        }

        // Phase 2: 找到 NPC 但不在旁边 → 走过去
        var dist = this._player.Position.EuclideanDistanceTo(npc.Position);
        if (dist > 3f)
        {
            this._logger.LogDebug("[Crafting] 走向混沌合成NPC #{Num} ({X},{Y})", ChaosGoblinNumber, npc.Position.X, npc.Position.Y);
            await this._adapter.WalkToAsync(
                new Point((byte)npc.Position.X, (byte)npc.Position.Y), map).ConfigureAwait(false);
            return StepResult.InProgress;
        }

        // Phase 3: 在 NPC 旁边但未打开对话 → 打开对话
        if (this._player.OpenedNpc != npc)
        {
            this._logger.LogInformation("[Crafting] 打开混沌合成NPC #{Num} 对话", ChaosGoblinNumber);
            await this._talkNpcAction.TalkToNpcAsync(this._player, npc).ConfigureAwait(false);
            return StepResult.InProgress;
        }

        // Phase 4: 对话已打开，合成窗口就绪
        if (this._player.TemporaryStorage is { } tmp && tmp.Items.Any())
        {
            // 已有材料 → 执行合成
            this._logger.LogInformation("[Crafting] 执行混沌合成");
            await this._craftAction.MixItemsAsync(this._player, 0, 0).ConfigureAwait(false);
            var closeAction = new CloseNpcDialogAction();
            await closeAction.CloseNpcDialogAsync(this._player).ConfigureAwait(false);
            this._player.OpenedNpc = null;
            return StepResult.Completed;
        }

        // 还没放材料 → 从背包中找 Chaos 宝石移到 TemporaryStorage
        if (await TryMoveChaosToTemporaryStorage().ConfigureAwait(false))
        {
            this._logger.LogInformation("[Crafting] 已移动 Chaos 宝石到 TemporaryStorage");
            return StepResult.InProgress;
        }

        // 没有 Chaos 宝石 → 无法合成
        this._logger.LogWarning("[Crafting] 背包中无 Chaos 宝石，合成任务失败");
        var closeAction2 = new CloseNpcDialogAction();
        await closeAction2.CloseNpcDialogAsync(this._player).ConfigureAwait(false);
        this._player.OpenedNpc = null;
        return StepResult.Failed;
    }

    /// <summary>
    /// 从背包中找到 Chaos 宝石（Group=12, Number=15）并移到 TemporaryStorage。
    /// </summary>
    private async ValueTask<bool> TryMoveChaosToTemporaryStorage()
    {
        var inv = this._player.Inventory;
        if (inv is null) return false;

        var chaosItem = inv.Items.FirstOrDefault(i =>
            i.Definition?.Group == 12 && i.Definition?.Number == 15);
        if (chaosItem is null) return false;

        var fromSlot = chaosItem.ItemSlot;
        await this._moveItemAction.MoveItemAsync(this._player, fromSlot, Storages.Inventory, 0, Storages.ChaosMachine)
            .ConfigureAwait(false);
        return true;
    }
}
