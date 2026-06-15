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
    public ValueTask<bool> RepairAllEquipmentAsync(AiPlayer player) => ValueTask.FromResult(false);
    public ValueTask<int> BuyPotionsAsync(AiPlayer player) => ValueTask.FromResult(0);

    public async ValueTask CloseDialogAsync(AiPlayer player)
    {
        var closeAction = new GameLogic.PlayerActions.CloseNpcDialogAction();
        await closeAction.CloseNpcDialogAsync(player).ConfigureAwait(false);
    }
}
