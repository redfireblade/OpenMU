// <copyright file="InventoryMaintenanceMvp.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using Microsoft.Extensions.Logging;

/// <summary>
/// 背包维护 MVP — 多步骤、有状态。
/// 职责：存值钱物品→卖垃圾→装备强化→合成翅膀。
/// 每 tick 只执行一个阶段，由决策系统或心跳触发。
/// </summary>
public sealed class InventoryMaintenanceMvp
{
    private readonly AiPlayer _player;
    private readonly InventoryManagerService _inventoryManager;
    private readonly ILogger _logger;

    public enum MaintenancePhase
    {
        Idle,
        ScanInventory,
        StoreValuables,
        SellJunk,
        UpgradeAndCraft,
    }

    public MaintenancePhase CurrentPhase { get; private set; } = MaintenancePhase.Idle;

    public InventoryMaintenanceMvp(AiPlayer player, InventoryManagerService inventoryManager, ILogger logger)
    {
        this._player = player;
        this._inventoryManager = inventoryManager;
        this._logger = logger;
    }

    /// <summary>返回是否正在维护中。</summary>
    public bool IsActive() => this.CurrentPhase != MaintenancePhase.Idle;

    /// <summary>启动一次背包维护。</summary>
    public void BeginMaintenance()
    {
        this.CurrentPhase = MaintenancePhase.ScanInventory;
        this._logger.LogInformation("[InvMaint] 开始背包维护");
    }

    /// <summary>启动或继续执行背包维护。</summary>
    public async ValueTask<StepResult> ExecuteAsync()
    {
        switch (this.CurrentPhase)
        {
            case MaintenancePhase.Idle:
                this.CurrentPhase = MaintenancePhase.ScanInventory;
                this._logger.LogInformation("[InvMaint] 开始背包维护");
                return StepResult.InProgress;

            case MaintenancePhase.ScanInventory:
            {
                var freeSlots = this._inventoryManager.GetFreeSlotCount();
                this._logger.LogDebug("[InvMaint] 探查背包: {Free} 格空闲", freeSlots);
                this.CurrentPhase = MaintenancePhase.StoreValuables;
                return StepResult.InProgress;
            }

            case MaintenancePhase.StoreValuables:
            {
                // 委托给现有的 StoreVaultItemsAsync 逻辑
                var result = await this._inventoryManager.ForceCleanupAsync().ConfigureAwait(false);
                if (result == StepResult.Completed)
                {
                    this._logger.LogInformation("[InvMaint] 值钱物品已存入仓库");
                }

                this.CurrentPhase = MaintenancePhase.SellJunk;
                return StepResult.InProgress;
            }

            case MaintenancePhase.SellJunk:
            {
                // 委托给清理逻辑
                var result = await this._inventoryManager.ForceCleanupAsync().ConfigureAwait(false);

                this.CurrentPhase = MaintenancePhase.UpgradeAndCraft;
                return StepResult.InProgress;
            }

            case MaintenancePhase.UpgradeAndCraft:
            {
                this._logger.LogInformation("[InvMaint] 背包维护完毕");
                this.CurrentPhase = MaintenancePhase.Idle;
                return StepResult.Completed;
            }

            default:
                this.CurrentPhase = MaintenancePhase.Idle;
                return StepResult.Completed;
        }
    }
}
