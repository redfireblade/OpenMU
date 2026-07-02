// <copyright file="NpcShoppingMvp.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;

/// <summary>
/// NPC 商店购物 MVP — 找 NPC、走过去、打开对话、买东西、关闭。
/// 支持类型：药水、修理、技能书。
/// </summary>
public sealed class NpcShoppingMvp
{
    private readonly AiPlayer _player;
    private readonly IGameAdapter _adapter;
    private readonly NpcInteractionService _npcService;
    private readonly ILogger _logger;

    public enum ShopType
    {
        Potions,
        Repair,
        SkillBook,
    }

    private enum ShopPhase
    {
        Idle,
        LocateNpc,
        WalkToNpc,
        OpenDialog,
        BuyItems,
        CloseDialog,
    }

    private ShopPhase _phase = ShopPhase.Idle;
    private NonPlayerCharacter? _targetNpc;
    private ShopType _currentType;

    public NpcShoppingMvp(AiPlayer player, IGameAdapter adapter, NpcInteractionService npcService, ILogger logger)
    {
        this._player = player;
        this._adapter = adapter;
        this._npcService = npcService;
        this._logger = logger;
    }

    /// <summary>启动一次 NPC 购物。type 指定商店类型。</summary>
    public void BeginShopping(ShopType type)
    {
        this._currentType = type;
        this._phase = ShopPhase.LocateNpc;
        this._logger.LogInformation("[NpcShop] 开始购物: {Type}", type);
    }

    /// <summary>返回是否正在购物中。</summary>
    public bool IsShopping => this._phase != ShopPhase.Idle;

    /// <summary>执行当前购物阶段。</summary>
    public async ValueTask<StepResult> ExecuteAsync()
    {
        var map = this._adapter.GetCurrentMap();
        if (map is null) return StepResult.Failed;

        switch (this._phase)
        {
            case ShopPhase.Idle:
                return StepResult.Completed;

            case ShopPhase.LocateNpc:
            {
                this._targetNpc = this._npcService.FindNearestMerchant(map, this._player.Position);
                if (this._targetNpc is null)
                {
                    this._logger.LogDebug("[NpcShop] 商店 NPC 不在当前地图");
                    this._phase = ShopPhase.Idle;
                    return StepResult.Failed;
                }

                this._phase = ShopPhase.WalkToNpc;
                return StepResult.InProgress;
            }

            case ShopPhase.WalkToNpc:
            {
                if (this._targetNpc is null) { this._phase = ShopPhase.Idle; return StepResult.Failed; }

                var dist = this._player.Position.EuclideanDistanceTo(this._targetNpc.Position);
                if (dist > 3f)
                {
                    await this._adapter.WalkToAsync(
                        new((byte)this._targetNpc.Position.X, (byte)this._targetNpc.Position.Y), map).ConfigureAwait(false);
                    return StepResult.InProgress;
                }

                this._phase = ShopPhase.OpenDialog;
                return StepResult.InProgress;
            }

            case ShopPhase.OpenDialog:
            {
                if (this._targetNpc is null) { this._phase = ShopPhase.Idle; return StepResult.Failed; }

                var talkAction = new MUnique.OpenMU.GameLogic.PlayerActions.TalkNpcAction();
                await talkAction.TalkToNpcAsync(this._player, this._targetNpc).ConfigureAwait(false);
                this._phase = ShopPhase.BuyItems;
                return StepResult.InProgress;
            }

            case ShopPhase.BuyItems:
            {
                switch (this._currentType)
                {
                    case ShopType.Potions:
                        await this._npcService.BuyPotionsAsync(this._player, 10).ConfigureAwait(false);
                        break;
                    case ShopType.Repair:
                        await this._npcService.RepairAllEquipmentAsync(this._player).ConfigureAwait(false);
                        break;
                }

                this._phase = ShopPhase.CloseDialog;
                return StepResult.InProgress;
            }

            case ShopPhase.CloseDialog:
            {
                await this._npcService.CloseDialogAsync(this._player).ConfigureAwait(false);
                this._phase = ShopPhase.Idle;
                this._logger.LogInformation("[NpcShop] 购物完成: {Type}", this._currentType);
                return StepResult.Completed;
            }

            default:
                this._phase = ShopPhase.Idle;
                return StepResult.Completed;
        }
    }
}
