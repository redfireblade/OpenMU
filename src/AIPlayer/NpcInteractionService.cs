// <copyright file="NpcInteractionService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.Pathfinding;

public sealed class NpcInteractionService
{
    private readonly AiPlayer _player;
    private readonly ILogger _logger;
    private readonly MUnique.OpenMU.GameLogic.PlayerActions.Items.BuyNpcItemAction _buyAction = new();
    private readonly MUnique.OpenMU.GameLogic.PlayerActions.CloseNpcDialogAction _closeAction = new();

    /// <summary>商店中药水的物品槽位: 大红=0, 中红=1, 小红=2, 大蓝=3...</summary>
    private static readonly byte[] PotionStoreSlots = { 0, 1, 2 };

    /// <summary>修理 NPC 编号 (Potion Girl / Merchant)。</summary>
    private const short RepairNpcNumber = 226; // Potion Girl 默认有修理功能 // 取决于 NPC 商店配置

    public NpcInteractionService(AiPlayer player, ILogger logger)
    {
        this._player = player;
        this._logger = logger;
    }

    public bool IsReadyForInteraction => true;

    /// <summary>获取 NPC 在当前地图的刷出坐标（从 MonsterSpawns 配置读取）。</summary>
    /// <param name="map">当前地图。</param>
    /// <param name="npcNumber">NPC 编号。</param>
    /// <returns>刷出坐标，如果该 NPC 不在当前地图则返回 null。</returns>
    public Point? GetQuestNpcSpawn(GameMap map, short npcNumber)
    {
        if (npcNumber == 0) return null;
        var spawn = map.Definition.MonsterSpawns
            .FirstOrDefault(s => s.MonsterDefinition?.Number == npcNumber);
        if (spawn is null) return null;
        return new Point((byte)((spawn.X1 + spawn.X2) / 2), (byte)((spawn.Y1 + spawn.Y2) / 2));
    }

    /// <summary>在当前地图的 NPC 实例中查找指定编号的 NPC（必须在附近）。</summary>
    public NonPlayerCharacter? FindQuestNpc(GameMap map, Point playerPos, short? npcNumber)
    {
        if (npcNumber is null) return null;
        if (npcNumber.Value == 0) return null;
        return map.GetNpcsInRange(playerPos, 200)
            .FirstOrDefault(n => n.Definition?.Number == npcNumber.Value);
    }

    public NonPlayerCharacter? FindNearestMerchant(GameMap map, Point playerPos)
    {
        return map.GetNpcsInRange(playerPos, 200)
            .Where(n => n.Definition?.MerchantStore?.Items.Count > 0)
            .OrderBy(n => playerPos.EuclideanDistanceTo(n.Position))
            .FirstOrDefault();
    }

    /// <summary>搜索所有 GameConfiguration.Maps 的 MonsterSpawns 定位NPC出生地图。</summary>
    public ushort? FindNpcMapNumber(AiPlayer player, short? npcNumber)
    {
        if (npcNumber is null || npcNumber.Value == 0) return null;
        var config = player.GameContext?.Configuration;
        if (config is null) return null;
        foreach (var mapDef in config.Maps)
        {
            if (mapDef.MonsterSpawns?.Any(s => s.MonsterDefinition?.Number == npcNumber.Value) == true)
                return (ushort)mapDef.Number;
        }
        return null;
    }

    public async ValueTask<bool> TryOpenDialogAsync(AiPlayer player, NonPlayerCharacter npc)
    {
        if (player.OpenedNpc == npc) return true;
        var talkAction = new GameLogic.PlayerActions.TalkNpcAction();
        await talkAction.TalkToNpcAsync(player, npc).ConfigureAwait(false);
        return player.OpenedNpc == npc;
    }

    public ValueTask<int> SellItemsAsync(AiPlayer player) => ValueTask.FromResult(0);

    /// <summary>
    /// 修理所有装备。需要先打开 NPC 对话（Potion Girl 或任何修理 NPC）。
    /// </summary>
    public async ValueTask<bool> RepairAllEquipmentAsync(AiPlayer player)
    {
        try
        {
            var repairAction = new GameLogic.PlayerActions.Items.ItemRepairAction();
            // 依次修理所有已装备物品（Slot 0-11）
            for (byte slot = 0; slot <= 11; slot++)
            {
                await repairAction.RepairItemAsync(player, slot).ConfigureAwait(false);
            }
            this._logger.LogInformation("[NpcService] ✅ 装备全部修理完成");
            return true;
        }
        catch (Exception ex)
        {
            this._logger.LogWarning("[NpcService] 装备修理失败: {Msg}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 从当前打开的 NPC 商店购买药水。
    /// 寻找 Potion Girl(226) 或其他有药水的商店 NPC。
    /// 购买的药水数量由 count 决定。
    /// </summary>
    public async ValueTask<int> BuyPotionsAsync(AiPlayer player, int count = 5)
    {
        if (player.OpenedNpc is null)
        {
            this._logger.LogWarning("[NpcService] 购买药水失败: 未打开 NPC 对话");
            return 0;
        }

        var store = player.OpenedNpc.Definition?.MerchantStore;
        if (store?.Items is null || store.Items.Count == 0)
        {
            this._logger.LogWarning("[NpcService] 购买药水失败: NPC #{Npc} 无商店",
                player.OpenedNpc.Definition?.Number);
            return 0;
        }

        var bought = 0;
        var hpPotionsInStore = store.Items
            .Where(i => i.Definition?.Group == 14 && i.Definition?.Number is >= 1 and <= 3)
            .OrderBy(i => i.Definition!.Number) // 小→中→大红
            .ToList();

        if (hpPotionsInStore.Count == 0)
        {
            this._logger.LogDebug("[NpcService] NPC #{Npc} 商店无药水", player.OpenedNpc.Definition?.Number);
            return 0;
        }

        foreach (var slot in hpPotionsInStore)
        {
            if (bought >= count) break;
            await this._buyAction.BuyItemAsync(player, (byte)slot.ItemSlot).ConfigureAwait(false);
            bought++;
        }

        if (bought > 0)
        {
            this._logger.LogInformation("[NpcService] ✅ 购买 {Count} 瓶药水", bought);
        }

        return bought;
    }

    public async ValueTask CloseDialogAsync(AiPlayer player)
    {
        var closeAction = new GameLogic.PlayerActions.CloseNpcDialogAction();
        await closeAction.CloseNpcDialogAsync(player).ConfigureAwait(false);
    }
}
