// <copyright file="BuyNpcItemAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.Items;

using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.Views.Inventory;

/// <summary>
/// Action to buy items from a Monster merchant.
/// </summary>
public class BuyNpcItemAction
{
    private readonly ItemPriceCalculator _priceCalculator;

    /// <summary>
    /// Initializes a new instance of the <see cref="BuyNpcItemAction"/> class.
    /// </summary>
    public BuyNpcItemAction()
    {
        this._priceCalculator = new ItemPriceCalculator();
    }

    /// <summary>
    /// Buys the item of the specified slot from the <see cref="Player.OpenedNpc"/> merchant store.
    /// </summary>
    /// <param name="player">The player who buys the item.</param>
    /// <param name="slot">The slot of the item.</param>
    public async ValueTask BuyItemAsync(Player player, byte slot)
    {
        if (player.OpenedNpc is null)
        {
            await player.InvokeViewPlugInAsync<IBuyNpcItemFailedPlugIn>(p => p.BuyNpcItemFailedAsync()).ConfigureAwait(false);
            return;
        }

        var npcDefinition = player.OpenedNpc.Definition;
        if (npcDefinition?.MerchantStore is null || npcDefinition.MerchantStore.Items.Count == 0)
        {
            await player.InvokeViewPlugInAsync<IBuyNpcItemFailedPlugIn>(p => p.BuyNpcItemFailedAsync()).ConfigureAwait(false);
            return;
        }

        var storeItem = npcDefinition.MerchantStore.Items.FirstOrDefault(i => i.ItemSlot == slot);
        if (storeItem is null)
        {
            await player.ShowLocalizedBlueMessageAsync(nameof(PlayerMessage.ItemUnknown)).ConfigureAwait(false);
            await player.InvokeViewPlugInAsync<IBuyNpcItemFailedPlugIn>(p => p.BuyNpcItemFailedAsync()).ConfigureAwait(false);
            return;
        }

        // Inventory Update:
        // 修正堆叠检查：不能用 Definition.Durability 当上限（药水 Definition.Durability=3 是充能数），
        // 实际堆叠上限是 byte.MaxValue(255)。使用独立的 CanStackItem 方法。
        var stackTarget = storeItem.IsStackable()
            ? player.Inventory!.Items.FirstOrDefault(invItem => BuyNpcItemAction.CanStackItem(storeItem, invItem))
            : null;
        if (stackTarget is { } targetItem)
        {
            if (!this.CheckMoney(player, storeItem))
            {
                return;
            }

            targetItem.Durability += storeItem.Durability;
            await player.InvokeViewPlugInAsync<IItemDurabilityChangedPlugIn>(p => p.ItemDurabilityChangedAsync(targetItem, false)).ConfigureAwait(false);
            await player.InvokeViewPlugInAsync<IUpdateMoneyPlugIn>(p => p.UpdateMoneyAsync()).ConfigureAwait(false);
            return;
        }
        else
        {
            var toSlot = player.Inventory!.CheckInvSpace(storeItem);
            if (toSlot is null)
            {
                await player.ShowLocalizedBlueMessageAsync(nameof(PlayerMessage.InventoryFull)).ConfigureAwait(false);
                await player.InvokeViewPlugInAsync<IBuyNpcItemFailedPlugIn>(p => p.BuyNpcItemFailedAsync()).ConfigureAwait(false);
                return;
            }

            if (!this.CheckMoney(player, storeItem))
            {
                await player.ShowLocalizedBlueMessageAsync(nameof(PlayerMessage.NotEnoughMoney)).ConfigureAwait(false);
                await player.InvokeViewPlugInAsync<IBuyNpcItemFailedPlugIn>(p => p.BuyNpcItemFailedAsync()).ConfigureAwait(false);
                return;
            }

            var newItem = player.PersistenceContext.CreateNew<Item>();
            newItem.AssignValues(storeItem);
            newItem.ItemSlot = (byte)toSlot;
            await player.InvokeViewPlugInAsync<INpcItemBoughtPlugIn>(p => p.NpcItemBoughtAsync(newItem)).ConfigureAwait(false);
            await player.Inventory.AddItemAsync(newItem).ConfigureAwait(false);
            player.GameContext.PlugInManager.GetPlugInPoint<IItemBoughtFromMerchantPlugIn>()?.ItemBought(player, newItem, storeItem, player.OpenedNpc);
        }

        await player.InvokeViewPlugInAsync<IUpdateMoneyPlugIn>(p => p.UpdateMoneyAsync()).ConfigureAwait(false);
    }

    /// <summary>
    /// 检查 NPC 商店物品能否堆叠到玩家背包的已有物品上。
    /// 与 CanCompletelyStackOn 不同：不使用 Definition.Durability 作为堆叠上限，
    /// 因为药水的 Definition.Durability=3（充能数），但实际上堆叠可以到 255。
    /// </summary>
    private static bool CanStackItem(Item storeItem, Item inventoryItem)
    {
        if (!storeItem.IsStackable()) return false;
        if (!storeItem.IsSameItemAs(inventoryItem)) return false;
        var maxStack = inventoryItem.Definition?.Durability ?? byte.MaxValue;
        if (maxStack < 10) maxStack = byte.MaxValue; // Durability<10 表示是充能数而非堆叠上限，用255
        var newTotal = storeItem.Durability + inventoryItem.Durability;
        return newTotal <= maxStack && newTotal <= byte.MaxValue;
    }

    private bool CheckMoney(Player player, Item item)
    {
        var price = this._priceCalculator.CalculateFinalBuyingPrice(item);
        if (!player.TryRemoveMoney((int)price))
        {
            return false;
        }

        return true;
    }
}