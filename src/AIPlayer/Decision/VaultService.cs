// <copyright file="VaultService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.MiniGames;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;

/// <summary>
/// 仓库服务 — 与仓库 NPC 交互存取物品。
///
/// 查询操作（HasItemInVault, HasCraftingMaterialsInVault）直接读 Account.Vault，
/// 不依赖 NPC 对话打开状态。
///
/// 实际操作（TakeItemsFromVaultAsync）需要 player.Vault（IStorage）已打开，
/// 这由 VaultModule（IBehaviorSubModule）的状态机负责确保。
/// </summary>
public sealed class VaultService
{
    private readonly AiPlayer _player;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="VaultService"/> class.
    /// </summary>
    /// <param name="player">The AI player.</param>
    /// <param name="logger">The logger.</param>
    public VaultService(AiPlayer player, ILogger logger)
    {
        this._player = player;
        this._logger = logger;
    }

    /// <summary>
    /// 从仓库中提取指定 Group/Number 的物品到背包。
    /// 需要 vault 对话已打开（player.Vault 非 null）。
    /// 最多取 maxCount 个，返回实际取到的数量。
    /// </summary>
    /// <param name="group">物品 Group。</param>
    /// <param name="number">物品 Number。</param>
    /// <param name="maxCount">最多提取数量。</param>
    /// <returns>实际取到的物品数量。</returns>
    public async ValueTask<int> TakeItemsFromVaultAsync(int group, int number, int maxCount)
    {
        var vaultStorage = this._player.Vault;
        if (vaultStorage is null)
        {
            this._logger.LogWarning("[VaultService] Vault 存储不可用（对话未打开或未初始化）");
            return 0;
        }

        var vaultItems = vaultStorage.Items
            .Where(i => i.Definition?.Group == group && i.Definition?.Number == number && i.Durability > 0)
            .Take(maxCount)
            .ToList();

        if (vaultItems.Count == 0)
        {
            this._logger.LogDebug(
                "[VaultService] 仓库中未找到 Group={Group} Number={Number} 的物品",
                group,
                number);
            return 0;
        }

        var moveAction = new MoveItemAction();
        int taken = 0;
        foreach (var item in vaultItems)
        {
            if (taken >= maxCount)
            {
                break;
            }

            var fromSlot = item.ItemSlot;

            // 每次取物前重新查背包空位
            var freeSlots = this._player.Inventory!.FreeSlots.ToList();
            if (freeSlots.Count == 0)
            {
                this._logger.LogWarning("[VaultService] 背包已满，无法继续取物");
                break;
            }

            var targetSlot = freeSlots[0];

            await moveAction.MoveItemAsync(
                    this._player,
                    fromSlot,
                    Storages.Vault,
                    targetSlot,
                    Storages.Inventory)
                .ConfigureAwait(false);

            taken++;
            this._logger.LogDebug(
                "[VaultService] 从仓库取出物品 ({Group}/{Number}) slot={FromSlot} → 背包 slot={ToSlot}",
                group,
                number,
                fromSlot,
                targetSlot);
        }

        if (taken > 0)
        {
            this._logger.LogInformation(
                "[VaultService] ✅ 从仓库取出 {Count} 个 ({Group}/{Number})",
                taken,
                group,
                number);
        }

        return taken;
    }

    /// <summary>
    /// 检查仓库中是否有指定 Group/Number 的物品。
    /// </summary>
    /// <param name="group">物品 Group。</param>
    /// <param name="number">物品 Number。</param>
    /// <returns>如果仓库中存在该物品则返回 true。</returns>
    public bool HasItemInVault(int group, int number)
    {
        var vaultItems = this._player.Account?.Vault?.Items;
        if (vaultItems is null)
        {
            return false;
        }

        return vaultItems.Any(i => i.Definition?.Group == group && i.Definition?.Number == number && i.Durability > 0);
    }

    /// <summary>
    /// 检查仓库中是否有门票合成材料。
    /// 混沌宝石（Jewel of Chaos, Group=12, Number=15）是所有合成必需的。
    /// </summary>
    /// <param name="miniGameDef">迷你游戏定义（含门票和等级信息）。</param>
    /// <returns>如果仓库中有所有必需材料则返回 true。</returns>
    public bool HasCraftingMaterialsInVault(MiniGameDefinition miniGameDef)
    {
        var vaultItems = this._player.Account?.Vault?.Items;
        if (vaultItems is null)
        {
            return false;
        }

        // 混沌宝石
        if (!vaultItems.Any(i => i.Definition?.Group == 12 && i.Definition?.Number == 15))
        {
            return false;
        }

        var ticketLevel = miniGameDef.TicketItemLevel;
        return miniGameDef.Type switch
        {
            MiniGameType.BloodCastle =>
                vaultItems.Any(i => i.Definition?.Group == 13 && i.Definition?.Number == 16 && i.Level == ticketLevel)
                && vaultItems.Any(i => i.Definition?.Group == 13 && i.Definition?.Number == 17 && i.Level == ticketLevel),
            MiniGameType.DevilSquare =>
                vaultItems.Any(i => i.Definition?.Group == 14 && i.Definition?.Number == 17 && i.Level == ticketLevel)
                && vaultItems.Any(i => i.Definition?.Group == 14 && i.Definition?.Number == 18 && i.Level == ticketLevel),
            _ => false,
        };
    }

    /// <summary>
    /// 从仓库中提取指定 Group/Number 且指定 Level 的物品到背包。
    /// </summary>
    /// <param name="group">物品 Group。</param>
    /// <param name="number">物品 Number。</param>
    /// <param name="level">物品等级。</param>
    /// <param name="maxCount">最多提取数量。</param>
    /// <returns>实际取到的物品数量。</returns>
    public async ValueTask<int> TakeItemsFromVaultByLevelAsync(int group, int number, int level, int maxCount)
    {
        var vaultStorage = this._player.Vault;
        if (vaultStorage is null)
        {
            this._logger.LogWarning("[VaultService] Vault 存储不可用（对话未打开或未初始化）");
            return 0;
        }

        var vaultItems = vaultStorage.Items
            .Where(i => i.Definition?.Group == group
                        && i.Definition?.Number == number
                        && i.Level == level
                        && i.Durability > 0)
            .Take(maxCount)
            .ToList();

        if (vaultItems.Count == 0)
        {
            return 0;
        }

        var moveAction = new MoveItemAction();
        int taken = 0;
        foreach (var item in vaultItems)
        {
            if (taken >= maxCount)
            {
                break;
            }

            var fromSlot = item.ItemSlot;

            // 每次取物前重新查背包空位
            var freeSlots = this._player.Inventory!.FreeSlots.ToList();
            if (freeSlots.Count == 0)
            {
                this._logger.LogWarning("[VaultService] 背包已满，无法继续取物");
                break;
            }

            var targetSlot = freeSlots[0];

            await moveAction.MoveItemAsync(
                    this._player,
                    fromSlot,
                    Storages.Vault,
                    targetSlot,
                    Storages.Inventory)
                .ConfigureAwait(false);

            taken++;
        }

        if (taken > 0)
        {
            this._logger.LogInformation(
                "[VaultService] ✅ 从仓库取出 {Count} 个 ({Group}/{Number} Lv.{Level})",
                taken,
                group,
                number,
                level);
        }

        return taken;
    }

    /// <summary>
    /// 从仓库中提取门票（根据 MiniGameDefinition 定义）。
    /// </summary>
    /// <param name="miniGameDef">迷你游戏定义。</param>
    /// <returns>是否成功取到门票。</returns>
    public async ValueTask<bool> TakeTicketFromVaultAsync(MiniGameDefinition miniGameDef)
    {
        var ticketDef = miniGameDef.TicketItem;
        if (ticketDef is null)
        {
            return false;
        }

        var taken = await this.TakeItemsFromVaultByLevelAsync(
                ticketDef.Group,
                ticketDef.Number,
                miniGameDef.TicketItemLevel,
                1)
            .ConfigureAwait(false);

        return taken > 0;
    }
}
